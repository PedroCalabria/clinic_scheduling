using System.Security.Claims;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.Patients;

/// <summary>
/// <c>GET /api/patients/me</c> and <c>GET /api/patients/{patientId}</c> — a patient's profile
/// and consents (P7), and the staff read that <c>AccessLog</c> exists to record.
/// </summary>
/// <remarks>
/// <para>
/// Two routes, one handler, because the authorization question is identical and only the
/// lookup differs. <c>/me</c> is what the frontend calls: it never has to know its own
/// patient id, which is also why no id is exposed in the session response.
/// </para>
/// <para>
/// The by-id route is where the ownership rule earns its place. A patient reaching for
/// another patient's record is refused with <c>auth.ownership_denied</c> whether or not that
/// record exists, and a staff member reaching the same route produces an access record. Both
/// outcomes come from one evaluation of the domain rule (design A8, A9).
/// </para>
/// </remarks>
internal static class GetPatientProfile
{
    internal static IEndpointRouteBuilder MapGetPatientProfile(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/patients/me", (
                ClaimsPrincipal principal,
                ClinicDbContext database,
                PatientDataGuard guard,
                CancellationToken cancellationToken) =>
                HandleAsync(principal, null, database, guard, cancellationToken))
            .WithName("GetMyPatientProfile");

        endpoints.MapGet("/api/patients/{patientId:guid}", (
                Guid patientId,
                ClaimsPrincipal principal,
                ClinicDbContext database,
                PatientDataGuard guard,
                CancellationToken cancellationToken) =>
                HandleAsync(principal, patientId, database, guard, cancellationToken))
            .WithName("GetPatientProfile");

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
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
            principal, patient, PatientDataAction.Viewed, guard, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

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
}
