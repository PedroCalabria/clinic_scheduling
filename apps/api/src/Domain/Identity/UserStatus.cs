namespace Clinic.Domain.Identity;

/// <summary>
/// Whether an account may hold a session.
/// </summary>
/// <remarks>
/// Distinct from soft-deletion (I10): a deleted account is gone from the product's point
/// of view, whereas <see cref="Locked"/> and <see cref="Disabled"/> are states an operator
/// or the brute-force guard can put a live account into and out of.
/// </remarks>
public enum UserStatus
{
    /// <summary>May sign in.</summary>
    Active = 1,

    /// <summary>Turned off by an administrator.</summary>
    Disabled = 2,

    /// <summary>Locked by the failed-attempt guard (design A10).</summary>
    Locked = 3,

    /// <summary>Created by an administrator, awaiting the Google sign-in that claims it.</summary>
    PendingClaim = 4,
}
