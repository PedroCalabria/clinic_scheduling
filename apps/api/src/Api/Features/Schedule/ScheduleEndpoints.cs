using System.Security.Claims;
using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Infrastructure;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Scheduling;
using Clinic.Api.Infrastructure.Time;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Dapper;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace Clinic.Api.Features.Schedule;

/// <summary>
/// S1 and S4 — the clinic day (spec: booking; design N7, N9).
/// </summary>
/// <remarks>
/// <para>
/// <b>One endpoint for two screens</b>, because they are one question asked with two scopes. A
/// professional gets their own day; reception gets everybody's, optionally narrowed. The payload is
/// identical, and two routes would have meant two places to write the <c>AccessLog</c> row — the
/// duplication in this change with the worst failure mode, because a missing audit row breaks an
/// LGPD claim silently while everything on screen still works.
/// </para>
/// <para>
/// <b>A professional's scope is structural, not filtered</b> (design N9). Any <c>professionalId</c>
/// in the request is disregarded for them rather than refused — the same shape as
/// <c>SaveTimeBlockRequest</c> carrying no professional, which is what makes "a professional cannot
/// aim this at somebody else" a property of the code rather than a check somebody remembers. A
/// refusal would also be a worse answer: it would confirm that the named professional exists.
/// </para>
/// <para>
/// <b>This is the first read in the product that discloses patients to somebody who is not
/// them</b>, and therefore the first that must record the access. The <c>TimeBlock</c> path
/// documented writing no row because a block names nobody; here every appointment names a person.
/// The rows are written through <see cref="PatientDataGuard"/> before the payload is built, so
/// "this staff member looked" is true the moment they looked.
/// </para>
/// </remarks>
internal static class ScheduleEndpoints
{
    /// <summary>One row of the day, as Dapper reads it.</summary>
    /// <remarks>
    /// Flat and stringly-typed where the database is: <c>status</c> and <c>source</c> are stored as
    /// their enum names, and re-parsing them here to format them again would buy nothing. The two
    /// instants arrive as <c>DateTime</c> from <c>lower()</c>/<c>upper()</c> and are re-kinded on
    /// the way out, exactly as the occupancy read does it.
    /// </remarks>
    private sealed record ScheduledRow
    {
        public Guid Id { get; init; }

        public Guid ProfessionalId { get; init; }

        public Guid PatientId { get; init; }

        public Guid PatientUserId { get; init; }

        public Guid AppointmentTypeId { get; init; }

        public Guid ResourceId { get; init; }

        public string Status { get; init; } = string.Empty;

        public string Source { get; init; } = string.Empty;

        public DateTime StartsAt { get; init; }

        public DateTime EndsAt { get; init; }

        public string PatientName { get; init; } = string.Empty;

        public string AppointmentTypeName { get; init; } = string.Empty;

        public string ResourceName { get; init; } = string.Empty;

        public string? ProfessionalFullName { get; init; }

        public string ProfessionalEmail { get; init; } = string.Empty;
    }

    /// <summary>A <c>timestamptz</c> Npgsql handed back unkinded, as the instant it is.</summary>
    private static Instant Utc(DateTime value) =>
        Instant.FromDateTimeUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    internal static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/schedule", DayAsync)
            // A patient is refused: this read names other people's appointments. Their own list is
            // /api/appointments, which stays patient-only for the same reason in reverse.
            .RequireAuthorization(AuthorizationPolicies.ScheduleReaders)
            .WithName("ReadScheduleDay");

        return endpoints;
    }

    private static async Task<IResult> DayAsync(
        string? date,
        Guid? professionalId,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        PatientDataGuard guard,
        ClinicTimezone timezone,
        ClinicScheduling scheduling,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (WallClockText.ParseDate(date) is not { } day)
        {
            // A clinic date, not an instant: "which day am I running" is a wall-clock question, and
            // the same shape S3 uses for a block's date.
            return CatalogRefusals.Invalid(nameof(date));
        }

        var role = actor.Role();
        var actingProfessionalId = (Guid?)null;

        if (role == Role.Professional)
        {
            var own = await database.Professionals
                .AsNoTracking()
                .Where(professional => professional.UserId == actor.UserId()
                    && professional.DeactivatedAtUtc == null)
                .Select(professional => (Guid?)professional.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (own is null)
            {
                // An invited professional nobody has configured yet. An ordinary state (design E1),
                // and an empty day is the honest answer rather than an error: they have no working
                // hours, so they cannot have appointments.
                return Results.Ok(new ScheduleDayResponse(
                    WallClockText.Format(day), timezone.Id, [], []));
            }

            // THE SCOPE, and it ignores the parameter rather than refusing it (design N9).
            actingProfessionalId = own;
        }
        else
        {
            actingProfessionalId = professionalId;
        }

        // The day as instants. AtStartOfDay rather than midnight-plus-conversion, because on a
        // spring-forward date midnight itself can be the time that does not exist.
        var dayStart = timezone.Zone.AtStartOfDay(day).ToInstant();
        var dayEnd = timezone.Zone.AtStartOfDay(day.PlusDays(1)).ToInstant();

        // ─────────────────────────────────────────────────────────────────────────────────
        //  DAPPER, NOT EF, AND NOT BY PREFERENCE (Decision L, the read side of CQRS-lite).
        //
        //  `Appointment.StartsAt` and `EndsAt` are `Ignore`d in the EF mapping: the column is one
        //  `tstzrange`, because that range is what the three exclusion constraints operate on, and
        //  two nullable endpoints could drift out of step with it. So a day is not expressible as
        //  an EF predicate — `where StartsAt < x` has no column to compile to.
        //
        //  What a day IS, exactly, is `time_range && tstzrange(from, to)`, which is also the
        //  operator the GiST index was built for. The availability read reached the same
        //  conclusion for the same reason and this query is shaped after it.
        // ─────────────────────────────────────────────────────────────────────────────────
        var connection = database.Database.GetDbConnection();

        var rows = (await connection.QueryAsync<ScheduledRow>(new CommandDefinition(
            """
            select a.id                  as Id,
                   a.professional_id     as ProfessionalId,
                   a.patient_id          as PatientId,
                   a.appointment_type_id as AppointmentTypeId,
                   a.resource_id         as ResourceId,
                   a.status              as Status,
                   a.source              as Source,
                   lower(a.time_range)   as StartsAt,
                   upper(a.time_range)   as EndsAt,
                   pat.full_name         as PatientName,
                   pat.user_id           as PatientUserId,
                   t.name                as AppointmentTypeName,
                   r.name                as ResourceName,
                   prof.full_name        as ProfessionalFullName,
                   u.email               as ProfessionalEmail
            from appointments a
            join patients pat          on pat.id = a.patient_id
            join appointment_types t   on t.id = a.appointment_type_id
            join resources r           on r.id = a.resource_id
            join professionals prof    on prof.id = a.professional_id
            join users u               on u.id = prof.user_id
            where a.status = @live
              and a.time_range && tstzrange(@from, @to, '[)')
              and (@professionalId::uuid is null or a.professional_id = @professionalId)
            order by lower(a.time_range), r.name
            """,
            new
            {
                // Live only. A cancelled appointment is not part of the day being run, and showing
                // it would make the day read as busier than it is — availability already treats
                // that time as free.
                live = nameof(AppointmentStatus.Scheduled),
                from = dayStart.ToDateTimeUtc(),
                to = dayEnd.ToDateTimeUtc(),

                // Null is "every professional", which is reception's unnarrowed day. Named in SQL
                // rather than branched on in C#, so there is one query with one plan.
                professionalId = actingProfessionalId,
            },
            cancellationToken: cancellationToken))).ToList();

        // THE ACCESS RECORD (design N7). Distinct patients, so one disclosure of one patient is one
        // row however many times they appear on the day. Written and saved BEFORE the payload is
        // built — a read that fails after disclosing is still a read that disclosed.
        //
        // For a professional the relationship fact is free and exact: every row here is one of
        // their own appointments, because the scope above made it so. That is the fact
        // PatientDataAccess is handed rather than one it derives, which it has no way to do.
        var patientIds = rows.Select(row => row.PatientId).Distinct().ToList();

        var patients = await database.Patients
            .Where(patient => patientIds.Contains(patient.Id))
            .ToListAsync(cancellationToken);

        var permitted = await guard.AuthorizeManyAsync(
            actor,
            patients,
            PatientDataAction.Viewed,
            cancellationToken,
            isActorsOwnPatient: _ => role == Role.Professional);

        var permittedIds = permitted.Select(patient => patient.Id).ToHashSet();

        var now = Instant.FromDateTimeOffset(clock.GetUtcNow());

        var appointments = rows
            .Where(row => permittedIds.Contains(row.PatientId))
            .Select(row => new ScheduledAppointment(
                row.Id,
                row.ProfessionalId,
                ProfessionalLabel.For(row.ProfessionalFullName, row.ProfessionalEmail),
                row.PatientId,
                row.PatientName,
                row.AppointmentTypeId,
                row.AppointmentTypeName,
                row.ResourceId,
                row.ResourceName,
                InstantPattern.ExtendedIso.Format(Utc(row.StartsAt)),
                InstantPattern.ExtendedIso.Format(Utc(row.EndsAt)),
                row.Status,
                row.Source,

                // The PATIENT'S standing, computed here for 5b's C10 reason and named for the
                // sentence S4 exists to say. `cutoffApplies: true` is the patient's authority, not
                // the reader's — reception's own authority is the override, and it is unconditional.
                PatientCanChange: scheduling.CancellationCutoff.Permits(
                    Utc(row.StartsAt), now, cutoffApplies: true)))
            .ToList();

        var blockQuery = database.TimeBlocks
            .AsNoTracking()
            .Where(block => block.DeactivatedAtUtc == null
                && block.StartsAt < dayEnd
                && block.EndsAt > dayStart);

        if (actingProfessionalId is { } onlyBlocks)
        {
            blockQuery = blockQuery.Where(block => block.ProfessionalId == onlyBlocks);
        }

        var blocks = await blockQuery
            .Join(
                database.Professionals,
                block => block.ProfessionalId,
                professional => professional.Id,
                (block, professional) => new { block, professional.FullName, professional.UserId })
            .Join(
                database.Users,
                entry => entry.UserId,
                user => user.Id,
                (entry, user) => new { entry.block, entry.FullName, user.Email })
            .OrderBy(entry => entry.block.StartsAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(new ScheduleDayResponse(
            WallClockText.Format(day),
            timezone.Id,
            appointments,
            blocks
                .Select(entry => new ScheduledBlock(
                    entry.block.Id,
                    entry.block.ProfessionalId,
                    ProfessionalLabel.For(entry.FullName, entry.Email),
                    InstantPattern.ExtendedIso.Format(entry.block.StartsAt),
                    InstantPattern.ExtendedIso.Format(entry.block.EndsAt)))
                .ToList()));
    }
}
