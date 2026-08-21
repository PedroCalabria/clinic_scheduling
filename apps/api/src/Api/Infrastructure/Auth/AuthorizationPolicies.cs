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
}
