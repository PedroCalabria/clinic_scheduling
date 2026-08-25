using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Time;
using Clinic.Domain.Configuration;
using Clinic.Domain.Scheduling;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;

namespace Clinic.Api.Infrastructure.Scheduling;

/// <summary>
/// The solver's inputs, plus the two facts the booking aggregate has to be told.
/// </summary>
/// <param name="Inputs">What <see cref="AvailabilitySolver"/> consumes.</param>
/// <param name="DurationsByProfessional">
/// The active per-type duration for each eligible professional. Absence is I2's refusal, so the
/// booking path reads "not present" as "not qualified" rather than having to ask separately.
/// </param>
/// <param name="ResourceTypeByResource">
/// The resource type each candidate room actually is, read from the database rather than assumed
/// from the query that selected them. I3 is a comparison, and comparing a value against itself
/// would make the invariant vacuous.
/// </param>
/// <param name="ResourceNames">
/// What each candidate room is called. Added by <c>booking-desk</c> so the surfaces entitled to
/// show a room can do so from the read that already selected it, rather than from a second request
/// against a catalogue endpoint reception is not allowed to reach (design N5). The rooms query has
/// ordered by this name since change 4 — the ordering IS the assignment policy — so this costs
/// nothing but carrying it.
/// </param>
internal sealed record ScheduleInputs(
    AvailabilityInputs Inputs,
    IReadOnlyDictionary<Guid, int> DurationsByProfessional,
    IReadOnlyDictionary<Guid, Guid> ResourceTypeByResource,
    IReadOnlyDictionary<Guid, string> ResourceNames);

/// <summary>
/// The one bounded read that feeds both the availability answer and the booking check
/// (design F1, B5, B11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a shared class and not two queries.</b> The availability read and the booking
/// check must never see different busy sets — a read that offers a slot the write refuses is the
/// exact failure this product exists to prevent. Sharing the solver alone would not be enough:
/// two loading steps could feed the same solver two different worlds. So there is one loading
/// step, and both callers use it.
/// </para>
/// <para>
/// <b>Where Dapper lands, precisely (Decision L, design B5).</b> EF reads the configuration —
/// working hours, exceptions, blocks, rooms — because those are ordinary equality-and-range-on-a
/// scalar queries it expresses well. Dapper reads the appointments, because that query is
/// <c>time_range &amp;&amp; tstzrange(...)</c> over a GiST index, which is genuinely hand-written
/// SQL over the range type Decision L was justified by. EF still performs the appointment
/// <em>insert</em>: the aggregate is the write model, and the exclusion constraints protect the row
/// whichever client issues it.
/// </para>
/// <para>
/// An over-fetch here is merely slow; an under-fetch is <em>wrong</em>, and a solver handed an
/// incomplete busy set cheerfully offers a slot that is already taken. That asymmetry is why the
/// window is deliberately widened below rather than trimmed.
/// </para>
/// </remarks>
internal sealed class ScheduleReader(
    ClinicDbContext database,
    ClinicTimezone timezone,
    ClinicScheduling scheduling,
    TimeProvider clock)
{
    /// <summary>
    /// Loads everything needed to answer for one appointment type over one date window.
    /// </summary>
    /// <param name="professionalId">
    /// One professional, or null for every professional qualified for the type. The booking path
    /// always names one; the availability read may not (design F7).
    /// </param>
    /// <param name="excludingAppointmentId">
    /// An appointment to leave out of the busy set — the one being rescheduled (design C7).
    /// </param>
    /// <remarks>
    /// <b>Why the exclusion is a parameter here rather than a filter the caller applies
    /// afterwards.</b> <c>booking-lifecycle</c>'s design said the reschedule handler would strip
    /// its own appointment from this step's <em>output</em>, leaving this class untouched. That
    /// turned out to be impossible: a <see cref="BusyInterval"/> carries a start, an end and a
    /// cause, and deliberately no identity — which is the whole reason blocks and appointments can
    /// share one list. Matching on the range instead would be guessing, so the row is excluded
    /// where it can be named, in the query.
    /// <para>
    /// The booking path passes nothing and is behaviourally untouched, which was the actual point
    /// of that design note.
    /// </para>
    /// <para>
    /// Without it, a near reschedule refuses itself: at load time the appointment being moved is
    /// still <c>Scheduled</c>, so the patient would be told their own outgoing appointment blocks
    /// their new one.
    /// </para>
    /// </remarks>
    internal async Task<ScheduleInputs> ReadAsync(
        AppointmentType appointmentType,
        LocalDate fromDate,
        LocalDate toDate,
        Guid? professionalId,
        CancellationToken cancellationToken,
        Guid? excludingAppointmentId = null)
    {
        // Every room of the required type, with its type's turnaround buffer, ordered by name so
        // the solver's "first free one" is a stable and explicable choice rather than whatever the
        // database happened to return. The ordering IS the assignment policy (domain-model F2).
        var rooms = await database.Resources
            .AsNoTracking()
            .Where(resource => resource.ResourceTypeId == appointmentType.RequiredResourceTypeId
                && resource.DeactivatedAtUtc == null)
            .Join(
                database.ResourceTypes,
                resource => resource.ResourceTypeId,
                type => type.Id,
                (resource, type) => new
                {
                    resource.Id,
                    resource.Name,
                    type.BufferMinutes,
                    ResourceTypeId = type.Id,
                })
            .OrderBy(entry => entry.Name)
            .ToListAsync(cancellationToken);

        // Eligibility in one join, because of what 3b built: a duration may only exist for a type
        // whose specialty the professional holds (the I2 gate), so "qualified for this kind of
        // visit" IS "has an active duration for it". The specialty check comes along for free
        // rather than being re-derived here (design F7).
        var durationQuery = database.ProfessionalAppointmentTypes
            .AsNoTracking()
            .Where(duration => duration.AppointmentTypeId == appointmentType.Id
                && duration.DeactivatedAtUtc == null);

        if (professionalId is { } only)
        {
            durationQuery = durationQuery.Where(duration => duration.ProfessionalId == only);
        }

        var eligible = await durationQuery
            .Join(
                database.Professionals.Where(professional => professional.DeactivatedAtUtc == null),
                duration => duration.ProfessionalId,
                professional => professional.Id,
                (duration, professional) => new { professional.Id, duration.DurationMinutes })
            .ToListAsync(cancellationToken);

        var professionalIds = eligible.Select(entry => entry.Id).ToList();

        var segments = await database.WorkingHoursTemplates
            .AsNoTracking()
            .Where(segment => professionalIds.Contains(segment.ProfessionalId)
                && segment.DeactivatedAtUtc == null)
            .ToListAsync(cancellationToken);

        var exceptions = await database.WorkingHoursExceptions
            .AsNoTracking()
            .Where(exception => professionalIds.Contains(exception.ProfessionalId)
                && exception.DeactivatedAtUtc == null
                && exception.Date >= fromDate
                && exception.Date <= toDate)
            .ToListAsync(cancellationToken);

        // The window as instants, so blocks and appointments can be filtered in the database
        // rather than loaded wholesale. AtStartOfDay rather than midnight-plus-conversion, because
        // on a spring-forward date midnight itself can be the thing that does not exist.
        var windowStart = timezone.Zone.AtStartOfDay(fromDate).ToInstant();
        var windowEnd = timezone.Zone.AtStartOfDay(toDate.PlusDays(1)).ToInstant();

        var blocks = await database.TimeBlocks
            .AsNoTracking()
            .Where(block => professionalIds.Contains(block.ProfessionalId)
                && block.DeactivatedAtUtc == null
                && block.EndsAt > windowStart
                && block.StartsAt < windowEnd)
            .ToListAsync(cancellationToken);

        var occupied = await ReadOccupancyAsync(
            professionalIds,
            rooms.Select(room => room.Id).ToList(),
            windowStart,
            windowEnd,
            rooms.Count == 0 ? 0 : rooms.Max(room => room.BufferMinutes),
            excludingAppointmentId,
            cancellationToken);

        // The seam fill (design B11). An appointment contributes to TWO lists — its professional's
        // and its room's — where a block contributes to one, because a block occupies nobody's
        // room. The second list existed only because change 4 reversed its own first cut and made
        // the resource half a candidate set instead of a boolean, so this is a list to populate
        // rather than a rule to implement.
        var resources = rooms
            .Select(room => new ResourceCandidate(
                room.Id,
                room.BufferMinutes,
                occupied.ByResource.TryGetValue(room.Id, out var busy) ? busy : []))
            .ToList();

        var schedules = eligible
            .Select(entry => new ProfessionalSchedule(
                entry.Id,
                entry.DurationMinutes,
                segments.Where(segment => segment.ProfessionalId == entry.Id).ToList(),
                exceptions.Where(exception => exception.ProfessionalId == entry.Id).ToList(),

                // One list, whatever the cause (design F5). Blocks and appointments are unioned
                // here and subtracted identically; only the booking refusal reads which was which.
                [
                    .. TimeBlock.BusyIntervalsOf(blocks.Where(block => block.ProfessionalId == entry.Id)),
                    .. occupied.ByProfessional.TryGetValue(entry.Id, out var theirs) ? theirs : [],
                ]))
            .ToList();

        var inputs = new AvailabilityInputs(
            appointmentType.Id,
            fromDate,
            toDate,
            timezone.Zone,
            Instant.FromDateTimeOffset(clock.GetUtcNow()),
            resources,
            scheduling.Parameters,
            schedules);

        return new ScheduleInputs(
            inputs,
            eligible.ToDictionary(entry => entry.Id, entry => entry.DurationMinutes),
            rooms.ToDictionary(room => room.Id, room => room.ResourceTypeId),
            rooms.ToDictionary(room => room.Id, room => room.Name));
    }

    /// <summary>
    /// Whether this patient already holds a live appointment overlapping the range (I6).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ReadAsync"/>, and deliberately not part of the solver's inputs:
    /// the solver answers "when is this professional free", and folding a patient's other
    /// appointments into it would make an availability read depend on who is asking. The
    /// exclusion constraint is the floor; this is what turns the floor into a sentence.
    /// </remarks>
    internal async Task<bool> PatientIsBusyAsync(
        Guid patientId,
        TimeRange range,
        CancellationToken cancellationToken,
        Guid? excludingAppointmentId = null)
    {
        var (connection, transaction) = await ScheduleMutation.EnlistAsync(database, cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"""
            select exists (
                select 1
                from appointments
                where patient_id = @patientId
                  and status = @live
                  and time_range && tstzrange(@from, @to, '[)')
                  and (@excluding::uuid is null or id <> @excluding)
            )
            """,
            new
            {
                patientId,
                live = nameof(AppointmentStatus.Scheduled),
                from = range.Start.ToDateTimeUtc(),
                to = range.End.ToDateTimeUtc(),

                // The reschedule's own appointment. I6 is about the patient being in two places
                // at once, and an appointment they are in the act of vacating is not a second
                // place.
                excluding = excludingAppointmentId,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Whether this professional already holds a live appointment overlapping the range (I7, the
    /// block direction).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retrofit into change 4's block path. That change shipped block creation with no
    /// appointment check and no lock, on the stated grounds that there was nothing to race; this
    /// change created the racer, so the check became reachable as planned rather than as a repair.
    /// </para>
    /// <para>
    /// Half-open, matching everything else: a block beginning at the exact instant an appointment
    /// ends is accepted, because a professional stepping out the moment a visit finishes is
    /// ordinary. Scoped to the block's own professional — a block over a time when somebody
    /// <em>else</em> has an appointment is nobody's conflict.
    /// </para>
    /// </remarks>
    internal async Task<bool> ProfessionalHasAppointmentAsync(
        Guid professionalId,
        TimeRange range,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = await ScheduleMutation.EnlistAsync(database, cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1
                from appointments
                where professional_id = @professionalId
                  and status = @live
                  and time_range && tstzrange(@from, @to, '[)')
            )
            """,
            new
            {
                professionalId,
                live = nameof(AppointmentStatus.Scheduled),
                from = range.Start.ToDateTimeUtc(),
                to = range.End.ToDateTimeUtc(),
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The live appointments touching the window, grouped by professional and by room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The Dapper query Decision L was justified by.</b> <c>time_range &amp;&amp;
    /// tstzrange(...)</c> is a range-overlap predicate served by the GiST index the professional
    /// exclusion constraint creates. EF has no natural way to express it, and expressing it badly
    /// would mean loading a professional's whole history to filter in memory.
    /// </para>
    /// <para>
    /// <b>The lower bound is widened by the largest turnaround buffer</b>, and this is the subtle
    /// half. A room's occupied period extends past its appointment by the buffer, so an appointment
    /// that ends just BEFORE the window can still make the window's first slot unofferable. A
    /// plain window filter would drop that row, and the read would offer a room that is still
    /// being cleaned. The professional side needs no widening — turnaround belongs to the room, not
    /// to the person walking out of it.
    /// </para>
    /// <para>
    /// One query for both groupings, with the OR, rather than two: a room is occupied by whoever
    /// booked it, including professionals outside this answer's eligible set, so the two halves
    /// genuinely need different filters over the same rows.
    /// </para>
    /// </remarks>
    private async Task<Occupancy> ReadOccupancyAsync(
        IReadOnlyList<Guid> professionalIds,
        IReadOnlyList<Guid> resourceIds,
        Instant windowStart,
        Instant windowEnd,
        int maxBufferMinutes,
        Guid? excludingAppointmentId,
        CancellationToken cancellationToken)
    {
        if (professionalIds.Count == 0 && resourceIds.Count == 0)
        {
            return new Occupancy(new Dictionary<Guid, List<BusyInterval>>(), new Dictionary<Guid, List<BusyInterval>>());
        }

        // Deliberately NOT ScheduleMutation.EnlistAsync, which requires a transaction. This read
        // serves two callers with different needs: the booking path has already begun one and
        // holds the professional lock, so the query must run inside it to see the same snapshot;
        // the availability read has none and needs none, because it decides nothing. Dapper opens
        // and closes the connection itself in that second case.
        var connection = database.Database.GetDbConnection();
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction();

        var rows = await connection.QueryAsync<OccupiedRow>(new CommandDefinition(
            """
            select professional_id as ProfessionalId,
                   resource_id     as ResourceId,
                   lower(time_range) as StartsAt,
                   upper(time_range) as EndsAt
            from appointments
            where status = @live
              and time_range && tstzrange(@from, @to, '[)')
              and (professional_id = any(@professionalIds) or resource_id = any(@resourceIds))
              and (@excluding::uuid is null or id <> @excluding)
            """,
            new
            {
                live = nameof(AppointmentStatus.Scheduled),
                from = (windowStart - Duration.FromMinutes(maxBufferMinutes)).ToDateTimeUtc(),
                to = windowEnd.ToDateTimeUtc(),
                professionalIds = professionalIds.ToArray(),
                resourceIds = resourceIds.ToArray(),

                // Null on every path but the reschedule. Named in SQL rather than branched on in
                // C#, so there is one query with one plan instead of two that could drift.
                excluding = excludingAppointmentId,
            },
            transaction,
            cancellationToken: cancellationToken));

        var byProfessional = new Dictionary<Guid, List<BusyInterval>>();
        var byResource = new Dictionary<Guid, List<BusyInterval>>();

        var wanted = professionalIds.ToHashSet();
        var wantedRooms = resourceIds.ToHashSet();

        foreach (var row in rows)
        {
            var interval = BusyInterval.Between(
                Instant.FromDateTimeUtc(DateTime.SpecifyKind(row.StartsAt, DateTimeKind.Utc)),
                Instant.FromDateTimeUtc(DateTime.SpecifyKind(row.EndsAt, DateTimeKind.Utc)),
                BusyCause.Appointment);

            if (wanted.Contains(row.ProfessionalId))
            {
                Add(byProfessional, row.ProfessionalId, interval);
            }

            if (wantedRooms.Contains(row.ResourceId))
            {
                Add(byResource, row.ResourceId, interval);
            }
        }

        return new Occupancy(byProfessional, byResource);

        static void Add(Dictionary<Guid, List<BusyInterval>> into, Guid key, BusyInterval interval)
        {
            if (!into.TryGetValue(key, out var list))
            {
                list = [];
                into[key] = list;
            }

            list.Add(interval);
        }
    }

    private sealed record Occupancy(
        IReadOnlyDictionary<Guid, List<BusyInterval>> ByProfessional,
        IReadOnlyDictionary<Guid, List<BusyInterval>> ByResource);

    /// <summary>One appointment row, as the overlap query returns it.</summary>
    private sealed record OccupiedRow
    {
        public Guid ProfessionalId { get; init; }

        public Guid ResourceId { get; init; }

        public DateTime StartsAt { get; init; }

        public DateTime EndsAt { get; init; }
    }
}
