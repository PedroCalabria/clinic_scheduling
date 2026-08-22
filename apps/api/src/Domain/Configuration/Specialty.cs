namespace Clinic.Domain.Configuration;

/// <summary>
/// A discipline the clinic practises — Cardiology, Dermatology
/// (02-domain-model.md §2, reference/configuration group).
/// </summary>
/// <remarks>
/// <para>
/// The catalog's root noun: an <see cref="AppointmentType"/> belongs to a specialty, and a
/// professional will hold one or more of them in change 3b. That makes it the first thing an
/// administrator creates and the last thing they can retire.
/// </para>
/// <para>
/// One lifecycle flag, not two (design D1). <see cref="DeactivatedAtUtc"/> is simultaneously
/// the I10 soft-delete marker and the business answer to "does the clinic still offer this?",
/// because for a catalog entity those are the same question. A second <c>Status</c> field
/// would produce four states of which three mean the same thing, and the first query that
/// forgot one of them would offer a retired specialty to a patient.
/// </para>
/// </remarks>
public sealed class Specialty
{
    /// <summary>EF materialization only.</summary>
    private Specialty()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Normalized (see <see cref="CatalogName"/>); case preserved for display.</summary>
    public string Name { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Null while offered; set when retired. Reversible (design D1).</summary>
    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    public static Specialty Define(string? name, DateTimeOffset createdAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = CatalogName.Normalize(name),
            CreatedAtUtc = createdAtUtc,
        };

    public void Rename(string? name) => Name = CatalogName.Normalize(name);

    /// <summary>
    /// Retires the specialty, refusing while active appointment types still belong to it.
    /// </summary>
    /// <remarks>
    /// The count is a parameter because the rule is domain and the lookup is infrastructure
    /// (design D2) — the same split change 2 used for the lockout threshold. Note what the
    /// caller must count: appointment types that are themselves <em>active</em>. A reference
    /// held only by an already-retired appointment type does not block, and counting the wrong
    /// side of that predicate is the mistake this signature exists to make visible.
    /// </remarks>
    /// <exception cref="CatalogRuleViolationException">Active appointment types still belong to it.</exception>
    public void Deactivate(int activeAppointmentTypes, DateTimeOffset deactivatedAtUtc)
    {
        if (activeAppointmentTypes < 0)
        {
            throw new DomainRuleViolationException("A dependent count cannot be negative.");
        }

        if (activeAppointmentTypes > 0)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.InUse,
                $"{activeAppointmentTypes} active appointment type(s) still belong to this specialty.",
                activeAppointmentTypes);
        }

        // Idempotent: retiring what is already retired is not an error, it is a no-op.
        DeactivatedAtUtc ??= deactivatedAtUtc;
    }

    /// <summary>
    /// Offers the specialty again. Nothing to re-validate — a specialty points at nothing, so
    /// it has no reference that could have gone inactive beneath it (contrast design D5).
    /// </summary>
    public void Reactivate() => DeactivatedAtUtc = null;
}
