using System.Security.Claims;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.Patients;

/// <summary>
/// Resolving "which patient is this request about", with the non-disclosure rule applied in
/// one place.
/// </summary>
/// <remarks>
/// The subtle part is what happens when the record does not exist. A patient asking for
/// somebody else's record must get the SAME answer whether or not that record exists —
/// otherwise the endpoint becomes a way to enumerate patients. Staff, who are permitted to
/// know, get a plain <c>404</c>. Keeping both answers in one helper is what stops a future
/// slice from getting the pair subtly wrong.
/// </remarks>
internal static class PatientLookup
{
    /// <summary>The outcome of resolving a patient for a caller: either a record, or the refusal to return.</summary>
    internal readonly record struct Result(Patient? Patient, IResult? Refusal)
    {
        internal bool Resolved => Patient is not null;
    }

    /// <summary>
    /// Loads the patient the request is about — the caller's own when
    /// <paramref name="patientId"/> is null.
    /// </summary>
    internal static async Task<Result> ResolveAsync(
        ClaimsPrincipal principal,
        Guid? patientId,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var patient = patientId is null
            ? await database.Patients.SingleOrDefaultAsync(
                candidate => candidate.UserId == principal.UserId() && candidate.DeletedAtUtc == null,
                cancellationToken)
            : await database.Patients.SingleOrDefaultAsync(
                candidate => candidate.Id == patientId && candidate.DeletedAtUtc == null,
                cancellationToken);

        if (patient is not null)
        {
            return new Result(patient, null);
        }

        // No record. What the caller is told depends on whether they are allowed to know.
        var refusal = principal.Role() == Role.Patient
            ? ApiError.Result(ErrorCodes.OwnershipDenied, StatusCodes.Status403Forbidden)
            : ApiError.Result(ErrorCodes.PatientNotFound, StatusCodes.Status404NotFound);

        return new Result(null, refusal);
    }

    /// <summary>
    /// Runs the ownership rule and returns the refusal to answer with, or null to proceed.
    /// </summary>
    internal static async Task<IResult?> AuthorizeAsync(
        ClaimsPrincipal principal,
        Patient patient,
        PatientDataAction action,
        PatientDataGuard guard,
        CancellationToken cancellationToken)
    {
        var decision = await guard.AuthorizeAsync(principal, patient, action, cancellationToken);

        return decision.IsAllowed()
            ? null
            : ApiError.Result(ErrorCodes.OwnershipDenied, StatusCodes.Status403Forbidden);
    }
}
