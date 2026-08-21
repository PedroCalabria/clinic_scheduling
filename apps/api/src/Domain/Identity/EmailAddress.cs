namespace Clinic.Domain.Identity;

/// <summary>
/// Normalization for the one identifier that has to match across two identity providers.
/// </summary>
/// <remarks>
/// Email is load-bearing in the invite-claim rule (design A5): an administrator types the
/// address, and months later Google presents its own casing and spacing for the same
/// mailbox. Comparing them raw would silently fail to claim the prepared account and
/// provision a duplicate patient instead, so normalization happens once, on the way in,
/// and the stored value is the only form the system ever compares.
///
/// Deliberately not a full validator: the API layer answers a malformed address with
/// <c>validation.invalid_format</c>, and the domain only refuses what is structurally
/// unusable. RFC 5322 in a regex is a well-known way to be confidently wrong.
/// </remarks>
public static class EmailAddress
{
    /// <summary>Trims and lower-cases, then rejects anything that cannot be an address.</summary>
    /// <exception cref="DomainRuleViolationException">The value is empty or has no single @.</exception>
    public static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized.Length == 0)
        {
            throw new DomainRuleViolationException("Email must not be empty.");
        }

        var at = normalized.IndexOf('@');

        if (at <= 0 || at != normalized.LastIndexOf('@') || at == normalized.Length - 1)
        {
            throw new DomainRuleViolationException("Email must contain a single @ with text on both sides.");
        }

        return normalized;
    }
}
