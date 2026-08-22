namespace Clinic.Domain.Configuration;

/// <summary>
/// A professional's clinical configuration — 1:1 with the <see cref="Identity.User"/> that
/// identifies them (02-domain-model.md §2).
/// </summary>
/// <remarks>
/// <para>
/// This record deliberately does not exist until an administrator configures someone (design
/// E1). Change 2 creates the <c>User</c> with <c>Role=Professional</c> when the administrator
/// invites them; this row appears on the first save in S7. The split is the capability
/// boundary: <c>identity-session</c> owns who somebody is, <c>clinic-configuration</c> owns
/// what they do clinically.
/// </para>
/// <para>
/// Consequence worth stating: "a professional" is a join, not a row. A user with the
/// professional role and no record here is a real and ordinary state — invited, not yet
/// configured — and any query about professionals has to decide whether that counts. S7 says
/// it does, and lists them as unconfigured.
/// </para>
/// <para>
/// It holds no name and no email. Those live on the <c>User</c>, and duplicating them here
/// would create two answers to one question.
/// </para>
/// </remarks>
public sealed class Professional
{
    /// <summary>EF materialization only.</summary>
    private Professional()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The user this configuration belongs to. Immutable — it is the identity.</summary>
    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Null while practising; set when retired. Reversible, as in 3a (design D1).</summary>
    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    /// <summary>
    /// Creates the configuration record for an invited professional.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for having established that the user exists and holds the
    /// professional role — that is a lookup, and the domain has no database. What this factory
    /// guarantees is that a record cannot exist without a user to belong to.
    /// </remarks>
    public static Professional ForUser(Guid userId, DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A professional's configuration requires a user.");
        }

        return new Professional
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>Retires the professional from the schedule.</summary>
    /// <remarks>
    /// No dependent count yet: nothing in this change references a professional. Change 5's
    /// appointments will, and this signature is where that count arrives — the same shape
    /// <see cref="Specialty.Deactivate"/> uses.
    /// </remarks>
    public void Deactivate(DateTimeOffset deactivatedAtUtc) => DeactivatedAtUtc ??= deactivatedAtUtc;

    /// <summary>Returns the professional to the schedule.</summary>
    public void Reactivate() => DeactivatedAtUtc = null;
}
