namespace Clinic.Domain.Identity;

/// <summary>
/// Identity and authentication (02-domain-model.md §Identity). The first real inhabitant
/// of the protected core.
/// </summary>
/// <remarks>
/// <para>
/// Two attributes are immutable after creation — <see cref="Role"/> and
/// <see cref="AuthProvider"/> (design A5) — and the type is shaped so that no caller can
/// change them: there is no public setter, and no method assigns either. That is why
/// creation goes through the intent-named factories below instead of a constructor. Each
/// one encodes which combination of provider, role, and credential is legitimate, so an
/// illegal user is not merely discouraged but unrepresentable.
/// </para>
/// <para>
/// Consequence worth stating: changing someone's role is not a feature. An administrator
/// disables one account and creates another, which keeps access-log history honest about
/// who held which role when.
/// </para>
/// </remarks>
public sealed class User
{
    /// <summary>EF materialization only.</summary>
    private User()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Normalized (see <see cref="EmailAddress"/>) — the only form ever compared.</summary>
    public string Email { get; private set; } = null!;

    /// <summary>Immutable after creation (design A5).</summary>
    public AuthProvider AuthProvider { get; private set; }

    /// <summary>Google's <c>sub</c>. Null until a federated account is claimed.</summary>
    public string? ExternalSubjectId { get; private set; }

    /// <summary>Internal accounts only; null for federated users.</summary>
    public string? PasswordHash { get; private set; }

    /// <summary>Immutable after creation (design A5).</summary>
    public Role Role { get; private set; }

    public UserStatus Status { get; private set; }

    /// <summary>
    /// Set for the bootstrapped administrator so a known credential cannot quietly become
    /// permanent (design A6).
    /// </summary>
    public bool MustChangePassword { get; private set; }

    public int FailedSignInCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Soft-delete marker (I10). Never hard-delete.</summary>
    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public bool IsDeleted => DeletedAtUtc is not null;

    /// <summary>True while this account is a professional invitation nobody has claimed.</summary>
    public bool AwaitsClaim => Status == UserStatus.PendingClaim && ExternalSubjectId is null;

    /// <summary>
    /// Whether this account may hold a session at all. Checked when a session is created
    /// and again whenever one is resolved, so disabling an account ends its access.
    /// </summary>
    public bool CanAuthenticate => !IsDeleted && Status == UserStatus.Active;

    /// <summary>
    /// Creates an internal staff account. Staff only, by deliberate omission: patients and
    /// professionals have no password, so no factory exists that could give them one.
    /// </summary>
    public static User CreateInternalStaff(
        string email,
        string passwordHash,
        Role role,
        DateTimeOffset createdAtUtc,
        bool mustChangePassword = false)
    {
        if (role is not (Role.FrontDesk or Role.Administrator))
        {
            throw new DomainRuleViolationException(
                $"Internal accounts exist for clinic staff only; {role} authenticates through Google.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainRuleViolationException("An internal account requires a password hash.");
        }

        return new User
        {
            Id = Guid.NewGuid(),
            Email = EmailAddress.Normalize(email),
            AuthProvider = AuthProvider.Internal,
            PasswordHash = passwordHash,
            Role = role,
            Status = UserStatus.Active,
            MustChangePassword = mustChangePassword,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Provisions a patient just-in-time from a verified Google identity: the sign-in that
    /// matched no existing user (design A5).
    /// </summary>
    public static User RegisterGooglePatient(string email, string externalSubjectId, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(externalSubjectId))
        {
            throw new DomainRuleViolationException("A federated user requires the provider's subject id.");
        }

        return new User
        {
            Id = Guid.NewGuid(),
            Email = EmailAddress.Normalize(email),
            AuthProvider = AuthProvider.Google,
            ExternalSubjectId = externalSubjectId,
            Role = Role.Patient,
            Status = UserStatus.Active,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Prepares a professional to be claimed by their first Google sign-in — the
    /// invite-first rule an administrator drives from S11 (design A5).
    /// </summary>
    /// <remarks>
    /// The account exists with a role but no credential and no subject id, which is what
    /// makes the role deterministic: the system never has to guess from the provider
    /// whether an arriving stranger is a patient or a professional.
    /// </remarks>
    public static User InviteProfessional(string email, DateTimeOffset createdAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            Email = EmailAddress.Normalize(email),
            AuthProvider = AuthProvider.Google,
            Role = Role.Professional,
            Status = UserStatus.PendingClaim,
            CreatedAtUtc = createdAtUtc,
        };

    /// <summary>
    /// Binds a verified Google subject to a prepared account, leaving the role exactly as
    /// the administrator set it.
    /// </summary>
    /// <exception cref="DomainRuleViolationException">
    /// The account is not a federated one awaiting a claim. This refusal is what stops an
    /// internal staff account from being taken over by whoever controls its mailbox.
    /// </exception>
    public void ClaimWithGoogleIdentity(string externalSubjectId)
    {
        if (string.IsNullOrWhiteSpace(externalSubjectId))
        {
            throw new DomainRuleViolationException("A claim requires the provider's subject id.");
        }

        if (AuthProvider != AuthProvider.Google)
        {
            throw new DomainRuleViolationException(
                "Only a federated account can be claimed through Google; an internal account is reachable by password only.");
        }

        if (ExternalSubjectId is not null)
        {
            throw new DomainRuleViolationException("This account is already bound to a provider identity.");
        }

        if (IsDeleted)
        {
            throw new DomainRuleViolationException("A deleted account cannot be claimed.");
        }

        ExternalSubjectId = externalSubjectId;
        Status = UserStatus.Active;
    }

    /// <summary>Replaces the password and clears the forced-change marker.</summary>
    public void SetPassword(string passwordHash)
    {
        if (AuthProvider != AuthProvider.Internal)
        {
            throw new DomainRuleViolationException("Only an internal account has a password.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainRuleViolationException("A password hash is required.");
        }

        PasswordHash = passwordHash;
        MustChangePassword = false;
        FailedSignInCount = 0;

        if (Status == UserStatus.Locked)
        {
            Status = UserStatus.Active;
        }
    }

    /// <summary>
    /// Counts a failed attempt and locks the account once the configured threshold is
    /// reached (design A10).
    /// </summary>
    /// <remarks>
    /// The threshold is passed in rather than held here: the rule ("too many failures locks
    /// the account") is domain, the number is configuration.
    /// </remarks>
    public void RecordFailedSignIn(int lockoutThreshold)
    {
        if (lockoutThreshold < 1)
        {
            throw new DomainRuleViolationException("The lockout threshold must be at least one attempt.");
        }

        FailedSignInCount++;

        if (FailedSignInCount >= lockoutThreshold && Status == UserStatus.Active)
        {
            Status = UserStatus.Locked;
        }
    }

    /// <summary>Clears the failed-attempt streak after a successful sign-in.</summary>
    public void RecordSuccessfulSignIn() => FailedSignInCount = 0;

    /// <summary>Turns the account off. Existing sessions are revoked by the caller.</summary>
    public void Disable() => Status = UserStatus.Disabled;

    /// <summary>
    /// Turns the account back on, restoring the state it should hold rather than a fixed one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The state to return to is derived, not remembered.</b> Nothing records what the account
    /// was before it was disabled, and storing a previous-status column would be a second source
    /// of truth to keep correct. It is derivable exactly: a federated account with no provider
    /// subject is an invitation nobody has claimed, so it returns to
    /// <see cref="UserStatus.PendingClaim"/> and stays claimable; anything else returns to
    /// <see cref="UserStatus.Active"/>. Restoring an unclaimed invitation as active would
    /// produce an account that can hold a session but has no identity behind it.
    /// </para>
    /// <para>
    /// <b>The failed-attempt streak is cleared, and a lockout does not survive.</b> An
    /// administrator deliberately restoring access is a stronger and more recent signal than a
    /// stale streak of bad passwords, and leaving the count in place would let the account
    /// re-lock on the next attempt — a restore that looks broken. The same reasoning
    /// <see cref="SetPassword"/> already applies.
    /// </para>
    /// <para>
    /// <b>A deleted account is refused</b>, and this one is not a nicety: deactivation releases
    /// the address (<see cref="SoftDelete"/>), so it may already belong to a live account.
    /// Restoring would either produce two live accounts on one address or fail against the
    /// filtered unique index — an error from the database rather than from the rule that means
    /// it. Recovery from deactivation is inviting the address anew, which is what
    /// <c>00-context.md</c> §5 has always said.
    /// </para>
    /// <para>
    /// <b>What does NOT come back:</b> an external-calendar authorization withdrawn when the
    /// account was disabled (<c>calendar-connection</c> design K16). The grant was handed back to
    /// the provider and the credential destroyed, so there is nothing to resume; the professional
    /// reconnects, which is one click on S2. Restoring an account silently re-acquiring write
    /// access to somebody's personal calendar would be the wrong default even if it were possible.
    /// </para>
    /// </remarks>
    public void Enable()
    {
        if (IsDeleted)
        {
            throw new DomainRuleViolationException(
                "A deactivated account cannot be restored; its address may already belong to another account. Invite the address anew instead.");
        }

        Status = AuthProvider == AuthProvider.Google && ExternalSubjectId is null
            ? UserStatus.PendingClaim
            : UserStatus.Active;

        FailedSignInCount = 0;
    }

    /// <summary>Soft-delete (I10) — the row stays; the account stops existing to the product.</summary>
    public void SoftDelete(DateTimeOffset deletedAtUtc)
    {
        DeletedAtUtc = deletedAtUtc;
        Status = UserStatus.Disabled;
    }
}
