namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Names of the RBAC policies, applied at the endpoint (design A8). Registration lives in
/// <see cref="AuthRegistration"/> — one definition site for what each policy requires.
/// </summary>
/// <remarks>
/// Policies rather than role checks inside handlers, for the reason 04-architecture.md §1
/// already names as vertical slicing's honest cost: without shared primitives, slices
/// duplicate. A policy is declared once, is visible in the endpoint's declaration, and is
/// what the framework refuses on — so a forgotten check is a missing declaration rather than
/// a silently permissive handler.
///
/// Ownership is deliberately NOT here: it cannot be decided from the principal alone, since
/// it depends on the resource being reached. That is <see cref="PatientDataGuard"/>.
/// </remarks>
internal static class AuthorizationPolicies
{
    /// <summary>Structural configuration and user management (S7-S11).</summary>
    internal const string Administrator = "role:administrator";

    /// <summary>Running the day on behalf of patients (S4-S6).</summary>
    internal const string FrontDesk = "role:front-desk";

    /// <summary>Either operational staff role — the reception desk and its manager.</summary>
    internal const string ClinicStaff = "role:clinic-staff";

    /// <summary>A professional acting on their own schedule (S1-S3).</summary>
    internal const string Professional = "role:professional";

    /// <summary>A patient acting on their own data (P5-P7).</summary>
    internal const string Patient = "role:patient";

    /// <summary>
    /// A patient acting on their own appointment, or reception acting on somebody's behalf.
    /// </summary>
    /// <remarks>
    /// The booking write paths (design N2). Deliberately <em>not</em> "any signed-in caller": a
    /// professional is refused here, so the policy still says something. What it cannot say is
    /// which of the two admitted roles is acting, and that difference decides the patient, the
    /// source, the cutoff authority and the not-found code — so every one of these endpoints
    /// branches on the role immediately, through one shared helper rather than three copies.
    /// </remarks>
    internal const string PatientOrClinicStaff = "role:patient-or-clinic-staff";

    /// <summary>
    /// A professional reading their own schedule, or reception reading the day (S1, S4).
    /// </summary>
    /// <remarks>
    /// A patient is refused: this read names other people's appointments. The two admitted roles
    /// get the same payload with a different scope, and the scope is structural rather than
    /// filtered — see the schedule endpoint (design N9).
    /// </remarks>
    internal const string ScheduleReaders = "role:schedule-readers";
}
