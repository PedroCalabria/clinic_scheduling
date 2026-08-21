namespace Clinic.Domain.Identity;

/// <summary>
/// The patient side of a user — 1:1 with <see cref="User"/>, holding the minimal personal
/// data the product needs (02-domain-model.md §Identity).
/// </summary>
/// <remarks>
/// LGPD minimization is the design constraint, not a footnote: no clinical data (anti-scope),
/// and a field is captured when something needs it rather than because a form had room. That
/// is why <see cref="ContactPhone"/> starts empty — Google supplies a name and an email at
/// sign-in, and a phone number has no purpose until an appointment exists (change 5).
/// </remarks>
public sealed class Patient
{
    /// <summary>EF materialization only.</summary>
    private Patient()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The owning user. This is what every ownership check resolves against.</summary>
    public Guid UserId { get; private set; }

    public string FullName { get; private set; } = null!;

    public string ContactEmail { get; private set; } = null!;

    public string? ContactPhone { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Soft-delete marker (I10).</summary>
    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public bool IsDeleted => DeletedAtUtc is not null;

    /// <summary>
    /// Creates the patient record for a user, from whatever the identity provider supplied.
    /// </summary>
    /// <remarks>
    /// The provider may not give a display name, so the email local part is the fallback —
    /// an empty name would be worse than an imperfect one, and the patient can correct it
    /// on P7.
    /// </remarks>
    public static Patient Register(Guid userId, string? fullName, string contactEmail, DateTimeOffset createdAtUtc)
    {
        var email = EmailAddress.Normalize(contactEmail);
        var name = string.IsNullOrWhiteSpace(fullName)
            ? email[..email.IndexOf('@')]
            : fullName.Trim();

        return new Patient
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FullName = name,
            ContactEmail = email,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>Updates what the patient is allowed to change about themselves (P7).</summary>
    public void UpdateContactDetails(string fullName, string? contactPhone)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new DomainRuleViolationException("A patient must have a name.");
        }

        FullName = fullName.Trim();

        // An empty submission clears the field rather than storing whitespace — minimization
        // includes letting someone withdraw data they volunteered.
        ContactPhone = string.IsNullOrWhiteSpace(contactPhone) ? null : contactPhone.Trim();
    }

    /// <summary>Soft-delete (I10).</summary>
    public void SoftDelete(DateTimeOffset deletedAtUtc) => DeletedAtUtc = deletedAtUtc;
}
