namespace Clinic.Domain.Identity;

/// <summary>What may be done to a patient's personal data, for the access record.</summary>
public enum PatientDataAction
{
    Viewed = 1,
    Updated = 2,
}

/// <summary>
/// The outcome of the ownership rule — and, in the same value, whether the access has to be
/// recorded.
/// </summary>
/// <remarks>
/// Deliberately one decision rather than two: "may this actor touch this patient's data"
/// and "must this access be logged" are answered by the same facts (who is acting, and
/// whether the data is theirs). Splitting them into separate checks is how the two drift
/// apart, leaving a path that is authorized but unlogged.
/// </remarks>
public enum PatientDataAccessDecision
{
    /// <summary>Refused — the actor does not own this data and holds no role that overrides that.</summary>
    Denied = 0,

    /// <summary>Allowed because the data is the actor's own. Not recorded (02-domain-model.md).</summary>
    AllowedAsOwner = 1,

    /// <summary>Allowed by role, on someone else's data. Recorded.</summary>
    AllowedAsStaff = 2,
}

/// <summary>
/// The ownership half of the two-layer authorization model (03-nfr.md §2, design A8) —
/// one definition of "this actor may touch this patient's data", referenced by the API and
/// by its tests rather than restated in either.
/// </summary>
/// <remarks>
/// <para>
/// The rule is expressed over the acting user's <em>own</em> identity and the patient's
/// owning user id. It cannot be satisfied by anything the client sends, which is the point:
/// an identifier in a request may narrow what is being asked for, never widen who may see
/// it (03-nfr.md: never trust a client-supplied id for ownership).
/// </para>
/// <para>
/// Least privilege is the default for roles with no stated need. A professional is refused
/// here even though they will legitimately see their own patients from change 5 onward —
/// when that requirement exists, it arrives with the scoping ("patients this professional
/// has appointments with") that makes the access defensible, rather than a blanket allow
/// granted early just in case.
/// </para>
/// </remarks>
public static class PatientDataAccess
{
    /// <summary>Decides whether the actor may reach this patient's data, and how.</summary>
    /// <param name="actorRole">The acting user's role, from their session.</param>
    /// <param name="actorUserId">The acting user's id, from their session — never from the request.</param>
    /// <param name="patientUserId">The user the patient record belongs to.</param>
    public static PatientDataAccessDecision Evaluate(Role actorRole, Guid actorUserId, Guid patientUserId) =>
        actorRole switch
        {
            // Owning the data is the only way a patient reaches any of it.
            Role.Patient => actorUserId == patientUserId
                ? PatientDataAccessDecision.AllowedAsOwner
                : PatientDataAccessDecision.Denied,

            // Operational staff act on behalf of patients, so the access is allowed and recorded.
            Role.FrontDesk or Role.Administrator => PatientDataAccessDecision.AllowedAsStaff,

            _ => PatientDataAccessDecision.Denied,
        };

    /// <summary>True when this decision has to produce an access record (design A9).</summary>
    public static bool RequiresAccessRecord(this PatientDataAccessDecision decision) =>
        decision == PatientDataAccessDecision.AllowedAsStaff;

    /// <summary>True when the actor may proceed.</summary>
    public static bool IsAllowed(this PatientDataAccessDecision decision) =>
        decision != PatientDataAccessDecision.Denied;
}
