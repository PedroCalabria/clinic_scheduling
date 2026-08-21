using System.Security.Claims;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.Patients;

/// <summary>
/// <c>POST /api/patients/me/consents/{type}/revoke</c> — the patient withdrawing a consent
/// (P7).
/// </summary>
/// <remarks>
/// <para>
/// Revoke, not delete: the grant stays on the record with the moment of withdrawal beside it
/// (02-domain-model.md §LGPD). The route says <c>revoke</c> rather than using <c>DELETE</c>
/// for the same reason — nothing is being removed.
/// </para>
/// <para>
/// Own consents only, and no by-id variant: nobody else has a reason to withdraw a consent on
/// someone's behalf. Staff acting for a patient is a different operation with a different
/// audit story, and it does not exist in this change.
/// </para>
/// </remarks>
internal static class RevokeConsent
{
    internal static RouteHandlerBuilder MapRevokeConsent(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/patients/me/consents/{type}/revoke", HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Patient)
            .WithName("RevokeConsent");

    private static async Task<IResult> HandleAsync(
        string type,
        ClaimsPrincipal principal,
        ClinicDbContext database,
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

        var userId = principal.UserId();

        var consent = await database.Consents
            .Where(candidate => candidate.UserId == userId
                && candidate.Type == consentType
                && candidate.RevokedAtUtc == null)
            .OrderByDescending(candidate => candidate.GrantedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (consent is null)
        {
            // Nothing active to withdraw. Reported as "the consent this action needs is not
            // in place" rather than inventing a code for an already-withdrawn consent.
            return ApiError.Result(
                ErrorCodes.ConsentRequired,
                StatusCodes.Status422UnprocessableEntity,
                new Dictionary<string, object?> { ["type"] = consentType.ToString() });
        }

        try
        {
            consent.Revoke(clock.GetUtcNow());
        }
        catch (DomainRuleViolationException)
        {
            return ApiError.Result(
                ErrorCodes.ConsentRequired,
                StatusCodes.Status422UnprocessableEntity,
                new Dictionary<string, object?> { ["type"] = consentType.ToString() });
        }

        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(ConsentResponse.From(consent));
    }
}
