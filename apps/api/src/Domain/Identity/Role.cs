namespace Clinic.Domain.Identity;

/// <summary>
/// What a user is allowed to do — the RBAC half of the two-layer authorization model
/// (01-requirements.md §Roles, 03-nfr.md §2).
/// </summary>
/// <remarks>
/// A role is decided when the user comes into existence and never changes afterwards
/// (design A5). Front desk and administrator are deliberately separate: the permission
/// difference is real — front desk does not touch structural configuration.
/// </remarks>
public enum Role
{
    /// <summary>Books for themselves; may only ever reach their own data.</summary>
    Patient = 1,

    /// <summary>Owns a schedule; signs in through Google.</summary>
    Professional = 2,

    /// <summary>Runs the day on behalf of patients; no structural configuration.</summary>
    FrontDesk = 3,

    /// <summary>Structural configuration and user management.</summary>
    Administrator = 4,
}
