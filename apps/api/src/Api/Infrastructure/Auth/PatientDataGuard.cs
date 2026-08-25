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
        CancellationToken cancellationToken,
        bool actorIsThisPatientsProfessional = false)
    {
        var decision = PatientDataAccess.Evaluate(
            actor.Role(), actor.UserId(), patient.UserId, actorIsThisPatientsProfessional);

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

    /// <summary>
    /// The same decision over a set of patients, recording every access it permits in one save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added by <c>booking-desk</c> for the reads that disclose a whole day</b> (design N7). A
    /// day view naming thirty patients through the single-patient method above would open thirty
    /// transactions to write thirty rows — so the shape changes and the rule does not. Each patient
    /// is evaluated by the same domain rule, and the records are added together and saved once.
    /// </para>
    /// <para>
    /// <paramref name="patients"/> is expected to be distinct: one disclosure of one patient is one
    /// row, whether they appear on the day once or four times. The caller owns that de-duplication
    /// because the caller knows what its list means.
    /// </para>
    /// <para>
    /// Returns the patients the actor may reach. A partial answer is the honest one for a set —
    /// a professional's day contains only their own patients by construction, and for staff the
    /// set is never partial, so in practice this either returns everything or (for a role with no
    /// business here) nothing. The refusal of the <em>request</em> is the endpoint's policy; this
    /// is the second layer, and it filters rather than throws.
    /// </para>
    /// <para>
    /// <b>The save happens before the caller renders anything</b>, for the same reason as above.
    /// A read that failed after disclosing is still a read that disclosed.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Patient>> AuthorizeManyAsync(
        ClaimsPrincipal actor,
        IReadOnlyCollection<Patient> patients,
        PatientDataAction action,
        CancellationToken cancellationToken,
        Func<Patient, bool>? isActorsOwnPatient = null)
    {
        var permitted = new List<Patient>(patients.Count);
        var recorded = false;

        foreach (var patient in patients)
        {
            var decision = PatientDataAccess.Evaluate(
                actor.Role(),
                actor.UserId(),
                patient.UserId,
                isActorsOwnPatient?.Invoke(patient) ?? false);

            if (!decision.IsAllowed())
            {
                continue;
            }

            permitted.Add(patient);

            if (decision.RequiresAccessRecord())
            {
                database.AccessLog.Add(
                    AccessLog.Record(actor.UserId(), patient.Id, action, clock.GetUtcNow()));

                recorded = true;
            }
        }

        // One save for the whole set, and none at all when nothing had to be recorded — an empty
        // day writes no rows, and neither does a patient reading their own.
        if (recorded)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        return permitted;
    }
}
