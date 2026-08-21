using System.Security.Claims;
using System.Text.Json.Serialization;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.Patients;

/// <summary>
/// <c>PUT /api/patients/me</c> and <c>PUT /api/patients/{patientId}</c> — the patient
/// correcting their own details (P7).
/// </summary>
/// <remarks>
/// The by-id route exists for the same reason as on the read side: it is where "patient A
/// cannot modify patient B" is actually tested. Nothing in the body can widen access — the
/// ownership rule is evaluated against the session and the loaded record, so a caller who
/// substitutes an id simply gets refused.
/// </remarks>
internal static class UpdatePatientProfile
{
    internal static IEndpointRouteBuilder MapUpdatePatientProfile(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/api/patients/me", (
                UpdateProfileRequest request,
                ClaimsPrincipal principal,
                ClinicDbContext database,
                PatientDataGuard guard,
                CancellationToken cancellationToken) =>
                HandleAsync(request, principal, null, database, guard, cancellationToken))
            .WithName("UpdateMyPatientProfile");

        endpoints.MapPut("/api/patients/{patientId:guid}", (
                Guid patientId,
                UpdateProfileRequest request,
                ClaimsPrincipal principal,
                ClinicDbContext database,
                PatientDataGuard guard,
                CancellationToken cancellationToken) =>
                HandleAsync(request, principal, patientId, database, guard, cancellationToken))
            .WithName("UpdatePatientProfile");

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        UpdateProfileRequest request,
        ClaimsPrincipal principal,
        Guid? patientId,
        ClinicDbContext database,
        PatientDataGuard guard,
        CancellationToken cancellationToken)
    {
        var lookup = await PatientLookup.ResolveAsync(principal, patientId, database, cancellationToken);

        if (!lookup.Resolved)
        {
            return lookup.Refusal!;
        }

        var patient = lookup.Patient!;

        var refusal = await PatientLookup.AuthorizeAsync(
            principal, patient, PatientDataAction.Updated, guard, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        try
        {
            patient.UpdateContactDetails(request.FullName ?? string.Empty, request.ContactPhone);
        }
        catch (DomainRuleViolationException)
        {
            return ApiError.Result(
                ErrorCodes.ValidationRequired,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["field"] = "fullName" });
        }

        await database.SaveChangesAsync(cancellationToken);

        var consents = await database.Consents
            .AsNoTracking()
            .Where(consent => consent.UserId == patient.UserId)
            .OrderBy(consent => consent.GrantedAtUtc)
            .ToListAsync(cancellationToken);

        return Results.Ok(new PatientProfileResponse(
            patient.FullName,
            patient.ContactEmail,
            patient.ContactPhone,
            [.. consents.Select(ConsentResponse.From)]));
    }

    internal sealed record UpdateProfileRequest(
        [property: JsonPropertyName("fullName")] string? FullName,
        [property: JsonPropertyName("contactPhone")] string? ContactPhone);
}
