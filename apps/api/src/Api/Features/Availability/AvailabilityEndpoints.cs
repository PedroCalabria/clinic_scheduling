using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Time;
using Clinic.Domain.Scheduling;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace Clinic.Api.Features.Availability;

/// <summary>
/// The availability read (spec: availability; design F1, F13).
/// </summary>
/// <remarks>
/// <para>
/// One endpoint, and the professional is optional: supplying it is the specific query, omitting it
/// is any-professional. There is no second route because there is no second computation — the
/// specific case is the union over a one-element set (design F7).
/// </para>
/// <para>
/// A <c>GET</c> because it is a read, which keeps rate limiting, logging and the correlation id
/// ordinary. A POST body would buy nothing and would make an idempotent read look like a command.
/// </para>
/// <para>
/// <b>The professional is named by its configuration record id, not its user id</b>, which differs
/// from S7 on purpose. S7 is keyed by user because the configuration record may not exist yet;
/// availability has no such case — a professional with no record has no hours and no durations, so
/// they can never appear in an answer. Change 5's appointment references the same id.
/// </para>
/// <para>
/// The whole handler is one bounded read followed by one call into the domain. Nothing here
/// decides anything: the loading is infrastructure and the deciding is
/// <see cref="AvailabilitySolver"/>. That split is what makes the daylight-saving reasoning
/// unit-testable, and it is also the thing to protect if this file ever grows.
/// </para>
/// </remarks>
internal static class AvailabilityEndpoints
{
    internal static IEndpointRouteBuilder MapAvailabilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/availability", QueryAsync)
            // Authenticated, but no role policy: availability exposes free time, never patient
            // data, and every role has a legitimate reason to ask (design F11). Anonymous is
            // still refused — the app authenticates by default.
            .RequireAuthorization()
            .RequireRateLimiting(AvailabilityRateLimiting.PolicyName)
            .WithName("QueryAvailability");

        return endpoints;
    }

    private static async Task<IResult> QueryAsync(
        Guid? appointmentTypeId,
        string? from,
        string? to,
        Guid? professionalId,
        ClinicDbContext database,
        ClinicTimezone timezone,
        ClinicScheduling scheduling,
        TimeProvider clock,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (appointmentTypeId is null || appointmentTypeId == Guid.Empty)
        {
            return CatalogRefusals.Required(nameof(appointmentTypeId));
        }

        if (WallClockText.ParseDate(from) is not { } fromDate
            || WallClockText.ParseDate(to) is not { } toDate)
        {
            // A malformed date is a malformed window, which is the code the catalogue already has
            // for this. A separate validation.invalid_format would split one user-visible failure
            // across two codes for no gain.
            return WindowInvalid();
        }

        if (toDate < fromDate)
        {
            return WindowInvalid();
        }

        var days = Period.Between(fromDate, toDate, PeriodUnits.Days).Days + 1;

        if (days > scheduling.MaxWindowDays)
        {
            // Refused before any computation is attempted, so an oversized window costs a
            // parameter check rather than a query.
            return WindowInvalid();
        }

        var appointmentType = await database.AppointmentTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                type => type.Id == appointmentTypeId && type.DeactivatedAtUtc == null,
                cancellationToken);

        if (appointmentType is null)
        {
            return CatalogRefusals.NotFound();
        }

        if (professionalId is { } named)
        {
            var known = await database.Professionals.AnyAsync(
                professional => professional.Id == named && professional.DeactivatedAtUtc == null,
                cancellationToken);

            if (!known)
            {
                // Distinct from "named a professional who is not qualified for this type", which
                // is an empty answer rather than an error. This is a reference that does not
                // resolve at all.
                return CatalogRefusals.NotFound();
            }
        }

        // Every room of the required type, with its type's turnaround buffer, ordered by name so
        // the solver's "first free one" is a stable and explicable choice rather than whatever
        // the database happened to return. Occupancy is empty until change 5 books something into
        // one of them (design F6).
        var resources = await database.Resources
            .AsNoTracking()
            .Where(resource => resource.ResourceTypeId == appointmentType.RequiredResourceTypeId
                && resource.DeactivatedAtUtc == null)
            .Join(
                database.ResourceTypes,
                resource => resource.ResourceTypeId,
                type => type.Id,
                (resource, type) => new { resource.Id, resource.Name, type.BufferMinutes })
            .OrderBy(entry => entry.Name)
            .Select(entry => new ResourceCandidate(entry.Id, entry.BufferMinutes, new List<BusyInterval>()))
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

        var response_ = await BuildAsync(
            database,
            timezone,
            scheduling,
            clock,
            appointmentType.Id,
            fromDate,
            toDate,
            resources,
            eligible.Select(entry => (entry.Id, entry.DurationMinutes)).ToList(),
            cancellationToken);

        // Decision S: availability is deliberately uncached, and a cached slot may already be
        // taken. Saying so on the wire is what stops an intermediary undoing that decision.
        response.Headers.CacheControl = "no-store";

        return Results.Ok(response_);
    }

    /// <summary>
    /// The bounded input read, and the one call into the domain.
    /// </summary>
    /// <remarks>
    /// One place, as design F1 requires. An over-fetch here is merely slow; an under-fetch is
    /// <em>wrong</em>, and a solver handed an incomplete busy set cheerfully offers a slot that is
    /// already taken. That asymmetry is why this is not spread across the handler.
    /// </remarks>
    private static async Task<AvailabilityResponse> BuildAsync(
        ClinicDbContext database,
        ClinicTimezone timezone,
        ClinicScheduling scheduling,
        TimeProvider clock,
        Guid appointmentTypeId,
        LocalDate fromDate,
        LocalDate toDate,
        IReadOnlyList<ResourceCandidate> resources,
        IReadOnlyList<(Guid ProfessionalId, int DurationMinutes)> eligible,
        CancellationToken cancellationToken)
    {
        var ids = eligible.Select(entry => entry.ProfessionalId).ToList();

        var segments = await database.WorkingHoursTemplates
            .AsNoTracking()
            .Where(segment => ids.Contains(segment.ProfessionalId) && segment.DeactivatedAtUtc == null)
            .ToListAsync(cancellationToken);

        var exceptions = await database.WorkingHoursExceptions
            .AsNoTracking()
            .Where(exception => ids.Contains(exception.ProfessionalId)
                && exception.DeactivatedAtUtc == null
                && exception.Date >= fromDate
                && exception.Date <= toDate)
            .ToListAsync(cancellationToken);

        // The window as instants, so blocks can be filtered in the database rather than loaded
        // wholesale. AtStartOfDay rather than midnight-plus-conversion, because on a
        // spring-forward date midnight itself can be the thing that does not exist.
        var windowStart = timezone.Zone.AtStartOfDay(fromDate).ToInstant();
        var windowEnd = timezone.Zone.AtStartOfDay(toDate.PlusDays(1)).ToInstant();

        var blocks = await database.TimeBlocks
            .AsNoTracking()
            .Where(block => ids.Contains(block.ProfessionalId)
                && block.DeactivatedAtUtc == null
                && block.EndsAt > windowStart
                && block.StartsAt < windowEnd)
            .ToListAsync(cancellationToken);

        var schedules = eligible
            .Select(entry => new ProfessionalSchedule(
                entry.ProfessionalId,
                entry.DurationMinutes,
                segments.Where(segment => segment.ProfessionalId == entry.ProfessionalId).ToList(),
                exceptions.Where(exception => exception.ProfessionalId == entry.ProfessionalId).ToList(),
                // One list, whatever the cause. Change 5 appends appointments here and change 7
                // external blocks, and the subtraction does not change (design F5).
                TimeBlock.BusyIntervalsOf(
                    blocks.Where(block => block.ProfessionalId == entry.ProfessionalId))))
            .ToList();

        var slots = AvailabilitySolver.Solve(new AvailabilityInputs(
            appointmentTypeId,
            fromDate,
            toDate,
            timezone.Zone,
            Instant.FromDateTimeOffset(clock.GetUtcNow()),
            resources,
            scheduling.Parameters,
            schedules));

        return new AvailabilityResponse(
            appointmentTypeId,
            WallClockText.Format(fromDate),
            WallClockText.Format(toDate),
            timezone.Id,
            slots
                .Select(slot => new AvailabilitySlotResponse(
                    slot.ProfessionalId,
                    slot.ResourceId,
                    InstantPattern.ExtendedIso.Format(slot.Start),
                    InstantPattern.ExtendedIso.Format(slot.End)))
                .ToList());
    }

    private static IResult WindowInvalid() =>
        ApiError.Result(ErrorCodes.AvailabilityWindowInvalid, StatusCodes.Status400BadRequest);
}
