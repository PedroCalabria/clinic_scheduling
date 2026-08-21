namespace Clinic.Domain.Identity;

/// <summary>
/// What a user has consented to, and which version of it (02-domain-model.md §LGPD).
/// </summary>
public enum ConsentType
{
    /// <summary>Processing of the patient's personal data. Captured at registration.</summary>
    DataProcessing = 1,

    /// <summary>
    /// Bidirectional calendar synchronization. Captured when a professional connects their
    /// calendar — the grant belongs to change 6; the type exists here so the record shape
    /// does not change later.
    /// </summary>
    CalendarSync = 2,
}

/// <summary>
/// A versioned consent, granted at a moment and possibly revoked at a later one.
/// </summary>
/// <remarks>
/// Revocation is recorded rather than erasing the grant. The reason is the same one behind
/// soft-delete (I10): "this person consented on the 3rd and withdrew on the 9th" is a
/// materially different fact from "this person never consented", and only one of them can
/// be reconstructed from a deleted row.
/// </remarks>
public sealed class Consent
{
    /// <summary>EF materialization only.</summary>
    private Consent()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public ConsentType Type { get; private set; }

    /// <summary>The version of the consent text the user agreed to.</summary>
    public string Version { get; private set; } = null!;

    public DateTimeOffset GrantedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static Consent Grant(Guid userId, ConsentType type, string version, DateTimeOffset grantedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DomainRuleViolationException("A consent must record which version was agreed to.");
        }

        return new Consent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Version = version.Trim(),
            GrantedAtUtc = grantedAtUtc,
        };
    }

    /// <summary>Marks the consent withdrawn, keeping the grant on the record.</summary>
    public void Revoke(DateTimeOffset revokedAtUtc)
    {
        if (RevokedAtUtc is not null)
        {
            throw new DomainRuleViolationException("This consent has already been revoked.");
        }

        if (revokedAtUtc < GrantedAtUtc)
        {
            throw new DomainRuleViolationException("A consent cannot be revoked before it was granted.");
        }

        RevokedAtUtc = revokedAtUtc;
    }
}
