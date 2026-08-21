namespace Clinic.Domain.Identity;

/// <summary>
/// How a user proves who they are. Both paths converge on the application's own session
/// (Decision J), so nothing downstream of authentication needs to know this value.
/// </summary>
/// <remarks>
/// Immutable after creation (design A5). The reason is a security one, not tidiness: the
/// invite-claim rule matches a Google sign-in to a prepared user BY EMAIL, so an internal
/// staff account that could be flipped to <see cref="Google"/> would become reachable by
/// whoever controls that mailbox at the provider.
/// </remarks>
public enum AuthProvider
{
    /// <summary>Email and password, created by an administrator. Staff only.</summary>
    Internal = 1,

    /// <summary>Federated Google identity (OIDC). Patients and professionals.</summary>
    Google = 2,
}
