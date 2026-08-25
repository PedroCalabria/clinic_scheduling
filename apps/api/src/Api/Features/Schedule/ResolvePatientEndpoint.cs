using System.Security.Claims;
using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Features.Schedule;

/// <summary>
/// S5's first step — reception resolves a walk-in to a patient record (design N8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Exact contact email, and deliberately not a search.</b> A name-substring search over patients
/// is a patient-enumeration surface: type one letter and read the register. Every result would also
/// have to be recorded, which turns the access log into keystroke noise and buries the entries that
/// matter — the log is only useful if a row means somebody actually looked at somebody. Asking a
/// returning patient for their email is how a clinic identifies them in any case.
/// </para>
/// <para>
/// <b>Trade-off, recorded rather than hidden:</b> a patient who does not remember which address
/// they used is not findable from S5, and the remedy today is the administrator's user list. The
/// revisit trigger is in design N8 — if real reception work shows the address is routinely unknown,
/// the answer is a deliberate, logged, minimum-length search, not a "convenience" partial match
/// added under a screen.
/// </para>
/// <para>
/// One <c>AccessLog</c> row when a patient is returned and none when nothing matched, written
/// through the guard so this path and the day read cannot come to disagree about what recording
/// means.
/// </para>
/// </remarks>
internal static class ResolvePatientEndpoint
{
    internal static IEndpointRouteBuilder MapResolvePatientEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/patients/by-email", ResolveAsync)
            // Reception and administrators only. A professional is refused: they see the patients
            // on their own schedule, which is a different question with a different scope, and
            // looking somebody up by address is not it.
            .RequireAuthorization(AuthorizationPolicies.ClinicStaff)
            .WithName("ResolvePatientByEmail");

        return endpoints;
    }

    private static async Task<IResult> ResolveAsync(
        string? email,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        PatientDataGuard guard,
        IOptions<AuthOptions> auth,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return CatalogRefusals.Required(nameof(email));
        }

        // Normalised the same way the record was written, so "Jo@Example.test" finds the patient
        // stored as "jo@example.test". Case is not a second identity.
        //
        // The domain refuses what cannot be an address at all, and that refusal is a 400 rather
        // than an empty result: a receptionist who typed half an address has made a typing mistake,
        // and "no such patient" would send them looking for the wrong problem. It is also the
        // shape the catalogue already has for a malformed field.
        string normalized;

        try
        {
            normalized = EmailAddress.Normalize(email);
        }
        catch (DomainRuleViolationException)
        {
            return CatalogRefusals.Invalid(nameof(email));
        }

        var patient = await database.Patients.FirstOrDefaultAsync(
            candidate => candidate.ContactEmail == normalized && candidate.DeletedAtUtc == null,
            cancellationToken);

        if (patient is null)
        {
            // The plain 404 staff get everywhere, PatientLookup having already settled that staff
            // are entitled to distinguish absence from denial.
            return ApiError.Result(ErrorCodes.PatientNotFound, StatusCodes.Status404NotFound);
        }

        var decision = await guard.AuthorizeAsync(
            actor, patient, PatientDataAction.Viewed, cancellationToken);

        if (!decision.IsAllowed())
        {
            // Unreachable behind the policy above, and kept because the policy is the courtesy
            // while the ownership rule is the boundary — the same pairing every screen uses.
            return ApiError.Result(ErrorCodes.Forbidden, StatusCodes.Status403Forbidden);
        }

        // The same query the booking gate runs, so what this reports and what booking enforces
        // cannot disagree. Reported here so a receptionist learns it before taking a walk-in's
        // time rather than as a refusal after choosing a slot.
        var consented = await database.Consents.AnyAsync(
            consent => consent.UserId == patient.UserId
                && consent.Type == ConsentType.DataProcessing
                && consent.RevokedAtUtc == null
                && consent.Version == auth.Value.ConsentVersion,
            cancellationToken);

        return Results.Ok(new ResolvedPatient(
            patient.Id, patient.FullName, patient.ContactEmail, consented));
    }
}
