namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Which of the two frontends a sign-in was started from (design D1).
/// </summary>
/// <remarks>
/// This exists because provisioning diverges by door: an unknown Google address is a new
/// patient on the portal and an un-invited stranger on the staff console. Nothing else in the
/// system needs to know which surface a request came from, so the concept stays this small.
/// </remarks>
internal enum SignInSurface
{
    /// <summary>The public portal at the root — P1.</summary>
    PatientPortal = 1,

    /// <summary>The internal console under its own base path — S0.</summary>
    Staff = 2,
}

/// <summary>
/// Classifies a sign-in by the local path it will return to.
/// </summary>
/// <remarks>
/// <para>
/// The API's half of the base-path contract that <c>00-context.md</c> §"Base paths" pins. Its
/// counterparts are <c>apps/staff/src/config/basePath.ts</c> and the matcher in
/// <c>infra/Caddyfile</c>; all three name the same segment, and this one is asserted by test so
/// a drift fails rather than silently misclassifying a sign-in.
/// </para>
/// <para>
/// Deriving the surface from the return path rather than from a <c>surface=</c> parameter is
/// deliberate (design D1): a parameter needs a default, and defaulting to the patient portal
/// would let a staff entry point that forgets it silently regain just-in-time provisioning —
/// which is the exact bug this change closes. A staff screen cannot forget its own base path,
/// because every staff route lives under it by construction.
/// </para>
/// </remarks>
internal static class SignInSurfaces
{
    /// <summary>Where the staff console is served (no trailing slash).</summary>
    internal const string StaffBasePath = "/staff";

    /// <summary>
    /// Classifies an <em>already sanitized</em> local return path.
    /// </summary>
    /// <remarks>
    /// Expects the output of <see cref="Google.GoogleOAuthState.SafeReturnPath"/>: anything
    /// unusable has by then been reduced to <c>/</c>, which lands on the patient portal. That
    /// is the correct answer for junk — the portal is the surface with no privileges attached.
    /// <para>
    /// Compared case-insensitively so that a hand-typed <c>/Staff</c> is treated as the staff
    /// surface. It would not match the staff router either way, so the only effect of the
    /// looser comparison is that the restrictive branch is harder to slip past.
    /// </para>
    /// </remarks>
    internal static SignInSurface FromReturnPath(string returnPath) =>
        IsUnderStaffBasePath(returnPath) ? SignInSurface.Staff : SignInSurface.PatientPortal;

    private static bool IsUnderStaffBasePath(string returnPath) =>
        // The whole prefix or nothing: `/staff` and `/staff/users` are the console,
        // `/staffroom` would be a route on the portal.
        string.Equals(returnPath, StaffBasePath, StringComparison.OrdinalIgnoreCase)
        || returnPath.StartsWith($"{StaffBasePath}/", StringComparison.OrdinalIgnoreCase);
}
