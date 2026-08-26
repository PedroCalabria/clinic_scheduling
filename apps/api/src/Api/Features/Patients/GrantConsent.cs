using System.Security.Claims;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Features.Patients;

/// <summary>
/// <c>POST /api/patients/me/consents/{type}/grant</c> — the patient granting a consent again
/// (P3, P7; design B12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Added by <c>booking-core</c>, and it exists because this change made a consent
/// load-bearing.</b> Change 2 granted the data-processing consent at just-in-time provisioning and
/// P7 let a patient withdraw it, so the only path was one-way: once withdrawn, nothing could put it
/// back and nothing checked it either. Booking now requires it, which turns that dead end into a
/// patient who cannot use the product and has no way out. This is the way out.
/// </para>
/// <para>
/// <b>A new row rather than clearing the old one's revocation.</b> "Consented on the 3rd, withdrew
/// on the 9th, consented again on the 11th" is three facts, and un-revoking would erase the middle
/// one — the same reasoning that made revocation a record rather than a delete. It also means the
/// new grant carries today's configured version, which is what makes the version comparison in the
/// booking gate meaningful.
/// </para>
/// <para>
/// Idempotent: granting a consent that is already active at the current version returns it
/// unchanged rather than stacking rows, because P3 may well submit it twice and a double-click
/// should not be a second legal fact.
/// </para>
/// </remarks>
internal static class GrantConsent
{
    internal static RouteHandlerBuilder MapGrantConsent(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/patients/me/consents/{type}/grant", HandleAsync)
            // Own consents only, like revoking. Nobody grants a consent on somebody else's
            // behalf — that is the one thing a consent cannot be.
            .RequireAuthorization(AuthorizationPolicies.Patient)
            .WithName("GrantConsent");

    private static async Task<IResult> HandleAsync(
        string type,
        ClaimsPrincipal principal,
        ClinicDbContext database,
        IOptions<AuthOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ConsentType>(type, ignoreCase: true, out var consentType))
        {
            return ApiError.Result(
                ErrorCodes.ValidationInvalidFormat,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["field"] = "type" });
        }

        // Narrowed by calendar-connection (6a, design K12). This endpoint accepted ANY consent
        // type, which was harmless only while DataProcessing was the sole one a screen could
        // reach: a patient granting themselves a CalendarSync consent recorded a permission
        // nobody had asked them for and nothing acted on. It now corresponds to a real Google
        // authorization, so a consent must only ever be recorded at a moment that particular
        // permission was actually requested — and the calendar's one producer is completing the
        // connect flow, which a patient cannot start.
        if (consentType != ConsentType.DataProcessing)
        {
            return ApiError.Result(
                ErrorCodes.ConsentRequired,
                StatusCodes.Status422UnprocessableEntity,
                new Dictionary<string, object?> { ["type"] = consentType.ToString() });
        }

        var userId = principal.UserId();
        var version = options.Value.ConsentVersion;

        var current = await database.Consents
            .Where(candidate => candidate.UserId == userId
                && candidate.Type == consentType
                && candidate.RevokedAtUtc == null
                && candidate.Version == version)
            .OrderByDescending(candidate => candidate.GrantedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (current is not null)
        {
            return Results.Ok(ConsentResponse.From(current));
        }

        var granted = Consent.Grant(userId, consentType, version, clock.GetUtcNow());

        database.Consents.Add(granted);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(ConsentResponse.From(granted));
    }
}
