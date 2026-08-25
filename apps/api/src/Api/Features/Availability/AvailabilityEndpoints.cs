using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Scheduling;
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
        ScheduleReader reader,
        ClinicTimezone timezone,
        ClinicScheduling scheduling,
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

        // ONE loading step, shared with the booking path (design B11). Before this change the
        // read owned its own queries; now both callers use the same reader, so the availability
        // answer and the booking check cannot see different busy sets. That is the structural half
        // of "the read never offers what the write refuses" — the solver being shared is the other.
        var loaded = await reader.ReadAsync(
            appointmentType,
            fromDate,
            toDate,
            professionalId,
            cancellationToken);

        var slots = AvailabilitySolver.Solve(loaded.Inputs);

        // Decision S: availability is deliberately uncached, and a cached slot may already be
        // taken. Saying so on the wire is what stops an intermediary undoing that decision.
        response.Headers.CacheControl = "no-store";

        return Results.Ok(new AvailabilityResponse(
            appointmentType.Id,
            WallClockText.Format(fromDate),
            WallClockText.Format(toDate),
            timezone.Id,
            slots
                .Select(slot => new AvailabilitySlotResponse(
                    slot.ProfessionalId,
                    slot.ResourceId,
                    loaded.ResourceNames.TryGetValue(slot.ResourceId, out var room) ? room : string.Empty,
                    InstantPattern.ExtendedIso.Format(slot.Start),
                    InstantPattern.ExtendedIso.Format(slot.End)))
                .ToList()));
    }

    private static IResult WindowInvalid() =>
        ApiError.Result(ErrorCodes.AvailabilityWindowInvalid, StatusCodes.Status400BadRequest);
}
