using System.Security.Claims;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// The ownership half of authorization, in one place: it decides whether an actor may touch
/// a patient's data, and records the access when the rule says it must be recorded
/// (design A8, A9).
/// </summary>
/// <remarks>
/// <para>
/// One primitive rather than two mechanisms. The design first reached for the framework's
/// resource-based authorization (<c>IAuthorizationService.AuthorizeAsync(user, resource,
/// requirement)</c>), which is idiomatic — but it answers allow/deny only, and this system
/// needs a third fact from the same evaluation: whether the access has to be logged. Split
/// across two calls, the authorization check and the audit decision can drift, which is
/// precisely the failure <see cref="PatientDataAccessDecision"/> was shaped to prevent. So
/// the guard evaluates the domain rule once and does both.
/// </para>
/// <para>
/// The rule itself lives in <c>Domain</c> (<see cref="PatientDataAccess"/>); this type is
/// the infrastructure around it — the claims, the database, the clock. Change 5 protects
/// appointments by reusing this guard rather than re-deriving the rule.
/// </para>
/// </remarks>
internal sealed class PatientDataGuard(ClinicDbContext database, TimeProvider clock)
{
    /// <summary>
    /// Decides whether the actor may perform <paramref name="action"/> on this patient's
    /// data, writing an access record when the decision calls for one.
    /// </summary>
    /// <remarks>
    /// The actor's identity comes from <paramref name="actor"/> — the session — and the
    /// patient comes from a record already loaded by the caller. Nothing here reads an
    /// identifier off the request, which is what makes "a client-supplied id can narrow but
    /// never widen access" structurally true rather than a rule someone has to remember.
    /// </remarks>
    public async Task<PatientDataAccessDecision> AuthorizeAsync(
        ClaimsPrincipal actor,
        Patient patient,
        PatientDataAction action,
        CancellationToken cancellationToken)
    {
        var decision = PatientDataAccess.Evaluate(actor.Role(), actor.UserId(), patient.UserId);

        if (decision.RequiresAccessRecord())
        {
            database.AccessLog.Add(
                AccessLog.Record(actor.UserId(), patient.Id, action, clock.GetUtcNow()));

            // Saved here so the record exists even if the caller's own work fails afterwards:
            // "this staff member looked" is true the moment they looked.
            await database.SaveChangesAsync(cancellationToken);
        }

        return decision;
    }
}
