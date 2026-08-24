using Clinic.Domain.Configuration;
using NodaTime;
using NodaTime.TimeZones;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// What one candidate slot came to: the room it would use, or the reason it would not happen.
/// </summary>
/// <remarks>
/// Exactly one of the two is set. <see cref="ResourceId"/> is the room the server assigns
/// (domain-model F2), which is why <see cref="AvailabilitySolver.Explain"/> returns it rather
/// than leaving the caller to choose — the choosing has one implementation, in the same walk that
/// decided the slot was offerable at all.
/// </remarks>
public readonly record struct SlotVerdict
{
    private SlotVerdict(Guid? resourceId, BookingRefusal? refusal)
    {
        ResourceId = resourceId;
        Refusal = refusal;
    }

    /// <summary>The room this slot would use, when it is offerable.</summary>
    public Guid? ResourceId { get; }

    /// <summary>Why it is not offerable, when it is not.</summary>
    public BookingRefusal? Refusal { get; }

    /// <summary>Whether the slot can be booked.</summary>
    public bool IsOfferable => ResourceId is not null;

    internal static SlotVerdict Offerable(Guid resourceId) => new(resourceId, null);

    internal static SlotVerdict Refused(BookingRefusal refusal) => new(null, refusal);
}

/// <summary>
/// The availability computation (02-domain-model.md §4, design F1) and the booking check that
/// must agree with it (design B1).
/// </summary>
/// <remarks>
/// <para>
/// Interval arithmetic in the protected core, not SQL. The input for a normal window is small —
/// one professional's month is tens of rows — and the daylight-saving reasoning below is exactly
/// the kind of thing that wants to be pure, unit-testable domain code rather than something the
/// database's zone handling is trusted with.
/// </para>
/// <para>
/// The shape of the answer, in order: candidate wall-clock hours for a date, converted to
/// instants, sliced by this professional's duration, filtered by lead time and horizon, and
/// reduced by the busy set. Every step is a stated requirement; none of them is an optimisation.
/// </para>
/// <para>
/// <b>Two entry points, one walk (design B1).</b> <see cref="Solve"/> enumerates every offerable
/// slot in a window; <see cref="Explain"/> takes one requested start and returns either the room
/// it would get or the named reason it would not. They share
/// <see cref="CandidateStarts"/> and <see cref="Judge"/> rather than copying them, because the
/// obvious alternative — a list of validations in the booking handler — is how a read that offers
/// a slot and a write that refuses it come to disagree. That drift is the exact failure this
/// product exists to prevent, so it is made unrepresentable instead of merely tested for.
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

        var slots = new List<AvailabilitySlot>();

        foreach (var professional in inputs.Professionals)
        {
            foreach (var (start, end) in CandidateStarts(inputs, professional))
            {
                if (Judge(inputs, professional, start, end).ResourceId is { } resourceId)
                {
                    slots.Add(new AvailabilitySlot(
                        professional.ProfessionalId,
                        inputs.AppointmentTypeId,
                        resourceId,
                        start,
                        end));
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
    /// Whether one requested start is bookable, and if not, why not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The booking path's question, answered by the same walk that answers the read's. The caller
    /// supplies inputs covering the requested date for the one professional being booked; this
    /// finds the candidate start equal to <paramref name="requestedStart"/> and judges it.
    /// </para>
    /// <para>
    /// <b>A start that is not a candidate start at all is refused as outside working hours</b>,
    /// and that covers more than it looks: a time the professional does not work, a time an
    /// exception removed, a start that does not sit on the configured step grid, and a start whose
    /// appointment would run past the end of the day. All four have the same remedy — pick a time
    /// the search offered — and all four are cases where the read never offered this start. Giving
    /// each its own code would split one user-meaningful failure into four, which the catalogue's
    /// own rule forbids.
    /// </para>
    /// <para>
    /// Lead time and horizon are deliberately judged <em>after</em> grid membership rather than
    /// filtered out of the candidate list, so that a real slot too close to now is refused as a
    /// lead-time violation and not mislabelled as outside working hours.
    /// </para>
    /// </remarks>
    public static SlotVerdict Explain(AvailabilityInputs inputs, Instant requestedStart)
    {
        if (inputs.Resources.Count == 0)
        {
            // Consistent with Solve's short-circuit: the clinic owns no room this visit could
            // happen in, which is a resource answer rather than a working-hours one.
            return SlotVerdict.Refused(BookingRefusal.ResourceUnavailable);
        }

        foreach (var professional in inputs.Professionals)
        {
            foreach (var (start, end) in CandidateStarts(inputs, professional))
            {
                if (start == requestedStart)
                {
                    return Judge(inputs, professional, start, end);
                }
            }
        }

        return SlotVerdict.Refused(BookingRefusal.OutsideWorkingHours);
    }

    /// <summary>
    /// Every start this professional's hours yield in the window, before anything is subtracted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately unfiltered by lead time, horizon, busy intervals or rooms — this is the grid,
    /// and <see cref="Judge"/> is the decision. Splitting it that way is what lets
    /// <see cref="Explain"/> tell "you asked for a time we do not offer" apart from "we offer that
    /// time and cannot give it to you", which are different sentences with different remedies.
    /// </para>
    /// <para>
    /// <b>The load-bearing line is that slicing happens after conversion.</b> The span becomes an
    /// instant interval first, and candidate starts are stepped through <em>that</em>. Slicing the
    /// wall clock first and converting each start is the natural way to write it and is wrong
    /// twice over: on a spring-forward date several distinct wall-clock starts resolve to the same
    /// instant, so the answer contains duplicates; on a fall-back date the repeated hour is never
    /// offered at all, so the answer is short by an hour of genuinely bookable time. Both bugs
    /// pass every test written in a zone without daylight saving, which is why 00-context.md §6
    /// requires the tests to use a zone with it.
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
    private static IEnumerable<(Instant Start, Instant End)> CandidateStarts(
        AvailabilityInputs inputs,
        ProfessionalSchedule professional)
    {
        var duration = Duration.FromMinutes(professional.DurationMinutes);

        if (duration <= Duration.Zero)
        {
            // Refused at configuration time, so this is a guard against a corrupt row rather
            // than a rule. Yielding nothing beats an infinite loop below.
            yield break;
        }

        for (var date = inputs.FromDate; date <= inputs.ToDate; date = date.PlusDays(1))
        {
            foreach (var span in CandidateHours(professional, date))
            {
                var opens = Resolve(inputs.ClinicZone, date.At(span.Start));
                var closes = Resolve(inputs.ClinicZone, date.At(span.End));

                if (closes <= opens)
                {
                    // Only reachable if a zone's transition were longer than the span itself.
                    // Nothing is offerable, and that is a better answer than a negative interval.
                    continue;
                }

                for (var start = opens; start + duration <= closes; start += inputs.Parameters.SlotStartStep)
                {
                    yield return (start, start + duration);
                }
            }
        }
    }

    /// <summary>
    /// The decision about one candidate slot: the room it gets, or the reason it gets none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single place every offerability rule is applied, reached identically from
    /// <see cref="Solve"/> and <see cref="Explain"/>. A rule added here is added to both, which is
    /// the structural half of design B1 — the property test asserting the two agree is the
    /// verification, this is the reason it holds.
    /// </para>
    /// <para>
    /// The order is chosen so the refusal names the most actionable cause. Lead time and horizon
    /// come first because they are properties of the time itself and no amount of retrying changes
    /// them. Then the professional's own busy set, blocks before appointments, because a
    /// professional who has blocked the afternoon is unavailable for a reason a patient should not
    /// be told was a race. Rooms come last, since a room is the thing most likely to free up.
    /// </para>
    /// </remarks>
    private static SlotVerdict Judge(
        AvailabilityInputs inputs,
        ProfessionalSchedule professional,
        Instant start,
        Instant end)
    {
        // Lead time and horizon are invariant I8, enforced at write time by the aggregate. They
        // are applied here from the same configured values, because a read that offers a slot the
        // write will refuse is a lying read (design F8), and because Explain has to be able to
        // name which of the two it was.
        if (start < inputs.Now + inputs.Parameters.MinimumLeadTime)
        {
            return SlotVerdict.Refused(BookingRefusal.LeadTimeViolation);
        }

        if (start > inputs.Now + inputs.Parameters.Horizon)
        {
            return SlotVerdict.Refused(BookingRefusal.HorizonExceeded);
        }

        // One list, subtracted identically whatever its origin (design F5). The cause is read
        // only to name the refusal — the arithmetic below is the same for every value — which is
        // the distinction change 4 anticipated when it said the write path is where an I7
        // refusal has to name its cause.
        foreach (var busy in professional.BusyIntervals)
        {
            if (busy.Overlaps(start, end))
            {
                return SlotVerdict.Refused(busy.Cause switch
                {
                    BusyCause.Appointment => BookingRefusal.SlotTaken,

                    // An internal block, and an external one until change 7 gives it its own
                    // answer. Both mean the professional declared themselves unavailable rather
                    // than somebody having been faster.
                    _ => BookingRefusal.SlotBlocked,
                });
            }
        }

        // The third constraint, evaluated per slot rather than per answer: a time is only
        // offerable if some room of the required type is free for it.
        return FirstFreeResource(inputs.Resources, start, end) is { } resourceId
            ? SlotVerdict.Offerable(resourceId)
            : SlotVerdict.Refused(BookingRefusal.ResourceUnavailable);
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
    /// The room this slot would use, or null if every one of them is taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// First free in the order the caller supplied, which makes the caller's ordering the
    /// assignment policy and keeps the choice out of here. Deterministic rather than arbitrary,
    /// which matters both for a test asserting an id and for the booking path, which assigns
    /// exactly what this returns (domain-model F2) rather than trusting a room a caller named.
    /// </para>
    /// <para>
    /// A resource's occupied interval extends past the appointment by its type's turnaround buffer
    /// (02-domain-model.md, decision F1), so a room being cleaned is not offered. Note the
    /// asymmetry with the professional check above, which uses no buffer: turnaround is a property
    /// of the room, not of the person walking out of it.
    /// </para>
    /// <para>
    /// The consequence worth knowing, and already recorded as a conscious trade-off (P-4): the
    /// buffer lives only here, while the database's exclusion constraint operates on the raw
    /// appointment interval. Two exactly-abutting bookings in one room therefore stay
    /// theoretically race-possible — the loser of a race can land in the cleaning window.
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
