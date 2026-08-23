using Clinic.Domain.Configuration;
using NodaTime;
using NodaTime.TimeZones;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// The availability computation (02-domain-model.md §4, design F1).
/// </summary>
/// <remarks>
/// <para>
/// Interval arithmetic in the protected core, not SQL. The input for a normal window is small —
/// one professional's month is tens of rows — and the daylight-saving reasoning below is exactly
/// the kind of thing that wants to be pure, unit-testable domain code rather than something the
/// database's zone handling is trusted with. Dapper earns its place on change 5's write path,
/// where the <c>tstzrange</c> columns and GiST indexes it is justified by actually exist.
/// </para>
/// <para>
/// The shape of the answer, in order: candidate wall-clock hours for a date, converted to
/// instants, sliced by this professional's duration, filtered by lead time and horizon, and
/// reduced by the busy set. Every step is a stated requirement; none of them is an optimisation.
/// </para>
/// </remarks>
public static class AvailabilitySolver
{
    /// <summary>
    /// Every time an appointment of the requested type could be placed, ordered by when.
    /// </summary>
    public static IReadOnlyList<AvailabilitySlot> Solve(AvailabilityInputs inputs)
    {
        // No room of the required type means nothing is bookable, however free the professionals
        // are. Short-circuited rather than discovered per slot, because the answer cannot differ
        // between slots when the candidate set is empty.
        if (inputs.Resources.Count == 0 || inputs.ToDate < inputs.FromDate)
        {
            return [];
        }

        var earliest = inputs.Now + inputs.Parameters.MinimumLeadTime;
        var latest = inputs.Now + inputs.Parameters.Horizon;

        var slots = new List<AvailabilitySlot>();

        foreach (var professional in inputs.Professionals)
        {
            var duration = Duration.FromMinutes(professional.DurationMinutes);

            if (duration <= Duration.Zero)
            {
                // Refused at configuration time, so this is a guard against a corrupt row
                // rather than a rule. Skipping beats an infinite loop below.
                continue;
            }

            for (var date = inputs.FromDate; date <= inputs.ToDate; date = date.PlusDays(1))
            {
                foreach (var span in CandidateHours(professional, date))
                {
                    Collect(inputs, professional, duration, date, span, earliest, latest, slots);
                }
            }
        }

        // Deterministic ordering, so a test can assert a sequence and a client can render one
        // without sorting. Professional breaks the tie because two of them are genuinely
        // offerable at the same instant.
        return slots
            .OrderBy(slot => slot.Start)
            .ThenBy(slot => slot.ProfessionalId)
            .ToList();
    }

    /// <summary>
    /// The wall-clock hours this professional works on this date, before anything is subtracted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An active exception <b>replaces</b> the day rather than reducing it (design F4):
    /// unavailable-all-day yields nothing, and different-hours yields those hours instead of
    /// every matching recurring segment. Treating it as another busy interval would make
    /// "works 14:00-18:00 instead" inexpressible without the administrator having entered a
    /// block, and they entered hours.
    /// </para>
    /// <para>
    /// Segment matching is <b>two-dimensional</b> (design F3): the weekday must match and the
    /// date must fall inside the effective period. Filtering on "currently effective" instead —
    /// evaluating the period against today rather than against the queried date — is the
    /// tempting shortcut, and it is wrong for the exact case the period exists for.
    /// </para>
    /// <para>
    /// Several segments may match one date; the answer is their union. 3b refuses two active
    /// segments whose effective periods AND times of day overlap, so any two matching here are
    /// disjoint in time — a morning block and an afternoon one — and no merging is needed.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<WorkingHoursSpan> CandidateHours(
        ProfessionalSchedule professional,
        LocalDate date)
    {
        var exception = professional.Exceptions
            .FirstOrDefault(candidate => candidate.IsActive && candidate.Date == date);

        if (exception is not null)
        {
            return exception.Span is { } replacement ? [replacement] : [];
        }

        return professional.Segments
            .Where(segment =>
                segment.IsActive
                && segment.DayOfWeek == date.DayOfWeek
                && segment.Period.Covers(date))
            .Select(segment => segment.Span)
            .ToList();
    }

    /// <summary>
    /// Converts one span against one date and adds every slot it yields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The load-bearing line in this file is that slicing happens after conversion.</b> The
    /// span becomes an instant interval first, and candidate starts are stepped through
    /// <em>that</em>. Slicing the wall clock first and converting each start is the natural way
    /// to write it and is wrong twice over: on a spring-forward date several distinct wall-clock
    /// starts resolve to the same instant, so the answer contains duplicates; on a fall-back date
    /// the repeated hour is never offered at all, so the answer is short by an hour of genuinely
    /// bookable time. Both bugs pass every test written in a zone without daylight saving, which
    /// is why 00-context.md §6 requires the tests to use a zone with it.
    /// </para>
    /// <para>
    /// Endpoints resolve leniently: a local time that does not exist moves forward past the gap,
    /// and one that occurs twice takes its earlier occurrence. The resulting interval is then
    /// simply shorter or longer than the wall clock suggests, which is what the day actually is —
    /// a clinic open "09:00-17:00" across a spring-forward transition is open seven real hours,
    /// and offering eight would offer a slot that does not exist. The strict alternative, which
    /// throws, would take availability down for a day because a government moved a clock.
    /// </para>
    /// </remarks>
    private static void Collect(
        AvailabilityInputs inputs,
        ProfessionalSchedule professional,
        Duration duration,
        LocalDate date,
        WorkingHoursSpan span,
        Instant earliest,
        Instant latest,
        List<AvailabilitySlot> slots)
    {
        var opens = Resolve(inputs.ClinicZone, date.At(span.Start));
        var closes = Resolve(inputs.ClinicZone, date.At(span.End));

        if (closes <= opens)
        {
            // Only reachable if a zone's transition were longer than the span itself. Nothing
            // is offerable, and that is a better answer than a negative interval.
            return;
        }

        for (var start = opens; start + duration <= closes; start += inputs.Parameters.SlotStartStep)
        {
            var end = start + duration;

            // Lead time and horizon are invariant I8, enforced at write time in change 5. They
            // are applied here from the same configured values, because a read that offers a
            // slot the write will refuse is a lying read (design F8).
            if (start < earliest || start > latest)
            {
                continue;
            }

            if (professional.BusyIntervals.Any(busy => busy.Overlaps(start, end)))
            {
                continue;
            }

            // The third constraint, evaluated per slot rather than per answer: a time is only
            // offerable if some room of the required type is free for it.
            if (FirstFreeResource(inputs.Resources, start, end) is not { } resourceId)
            {
                continue;
            }

            slots.Add(new AvailabilitySlot(
                professional.ProfessionalId,
                inputs.AppointmentTypeId,
                resourceId,
                start,
                end));
        }
    }

    /// <summary>
    /// The room this slot would use, or null if every one of them is taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// First free in the order the caller supplied, which makes the caller's ordering the
    /// assignment policy and keeps the choice out of here. Today every candidate is free — nothing
    /// occupies a room until change 5 — so the answer is always the first, and it is deterministic
    /// rather than arbitrary, which matters for a test asserting an id.
    /// </para>
    /// <para>
    /// A resource's occupied interval extends past the appointment by its type's turnaround buffer
    /// (02-domain-model.md, decision F1), so a room being cleaned is not offered. Note the
    /// asymmetry with the professional check above, which uses no buffer: turnaround is a property
    /// of the room, not of the person walking out of it.
    /// </para>
    /// <para>
    /// The consequence worth knowing, and already recorded as a conscious trade-off (P-4): the
    /// buffer lives only here, while change 5's database exclusion constraint will operate on the
    /// raw appointment interval. Two exactly-abutting bookings in one room therefore stay
    /// theoretically race-possible.
    /// </para>
    /// </remarks>
    private static Guid? FirstFreeResource(
        IReadOnlyList<ResourceCandidate> resources,
        Instant start,
        Instant end)
    {
        foreach (var resource in resources)
        {
            var buffer = Duration.FromMinutes(resource.BufferMinutes);

            if (!resource.BusyIntervals.Any(busy => busy.Overlaps(start, end, buffer)))
            {
                return resource.ResourceId;
            }
        }

        return null;
    }

    /// <summary>Wall clock to instant, with both daylight-saving cases answered rather than thrown.</summary>
    private static Instant Resolve(DateTimeZone zone, LocalDateTime local) =>
        zone.ResolveLocal(local, Resolvers.LenientResolver).ToInstant();
}
