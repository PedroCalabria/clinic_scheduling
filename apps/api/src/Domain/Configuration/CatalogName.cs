namespace Clinic.Domain.Configuration;

/// <summary>
/// Normalization for a catalog entity's name — the only identifier the catalog has.
/// </summary>
/// <remarks>
/// The same reasoning as <see cref="Identity.EmailAddress"/>, for the same reason:
/// normalization happens once, on the way in, so the stored value is the only form the
/// system ever compares. An administrator typing " Cardiologia " and another typing
/// "Cardiologia" mean one specialty, and a dropdown that offers both is a booking split
/// across two entries that should have been one.
///
/// Case is deliberately preserved in what is stored — "Cardiologia" is what a patient
/// should read — while <see cref="ComparisonKey"/> is what uniqueness is decided on
/// (design D3). The database enforces the same rule through a partial unique index on
/// <c>lower(name)</c>, so the two must agree; they do, because both lower-case invariantly.
/// </remarks>
public static class CatalogName
{
    /// <summary>The longest a catalog name may be, matching the column width.</summary>
    public const int MaxLength = 120;

    /// <summary>Trims, collapses inner runs of whitespace, and refuses what is unusable.</summary>
    /// <exception cref="DomainRuleViolationException">Empty, or longer than <see cref="MaxLength"/>.</exception>
    public static string Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new DomainRuleViolationException("A catalog name must not be empty.");
        }

        // "Sala  2" and "Sala 2" are the same room to everyone who reads them, so they must
        // not be two rows that the uniqueness rule considers different.
        var collapsed = string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (collapsed.Length > MaxLength)
        {
            throw new DomainRuleViolationException($"A catalog name must be at most {MaxLength} characters.");
        }

        return collapsed;
    }

    /// <summary>The form uniqueness is decided on — case-insensitive (design D3).</summary>
    public static string ComparisonKey(string? value) => Normalize(value).ToLowerInvariant();

    /// <summary>
    /// States what a uniqueness lookup means: a name held by an active record of the same kind
    /// is unavailable.
    /// </summary>
    /// <remarks>
    /// Uniqueness is the one catalog rule an entity cannot enforce alone, because it is a
    /// property of the <em>set</em> rather than of the row — so the lookup happens in the slice
    /// and its meaning is stated here (design D2, D3). Scoped to active records on purpose: a
    /// name freed by retirement becomes available again, which is what makes retirement
    /// reversible without stranding a name forever.
    /// </remarks>
    /// <exception cref="CatalogRuleViolationException">An active record of the same kind holds the name.</exception>
    public static void EnsureAvailable(bool heldByAnotherActiveRecord)
    {
        if (heldByAnotherActiveRecord)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.DuplicateName,
                "An active catalog entity of this kind already holds that name.");
        }
    }
}
