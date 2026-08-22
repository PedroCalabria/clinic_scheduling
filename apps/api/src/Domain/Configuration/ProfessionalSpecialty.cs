namespace Clinic.Domain.Configuration;

/// <summary>
/// A specialty a professional is qualified in — the credentialing fact
/// (02-domain-model.md §2, §6).
/// </summary>
/// <remarks>
/// <para>
/// An explicit entity rather than a collection on <see cref="Professional"/>, because it is
/// the **qualification gate** and the data source for invariant I2 (design E2). Change 5 needs
/// to answer "is this professional qualified for this appointment type's specialty?" as one
/// indexed lookup, not by walking a graph.
/// </para>
/// <para>
/// The obvious objection is that this looks derivable: an <see cref="AppointmentType"/> already
/// belongs to a <see cref="Specialty"/>, so a professional holding a duration for a cardiology
/// visit evidently does cardiology. It is not derivable, for two reasons. A professional
/// legitimately holds a specialty with no durations configured yet — the normal state right
/// after being invited — and a derived model cannot represent it. And the two answer different
/// questions: what someone is qualified for is asserted by an administrator, while how long
/// they take is operational configuration. Collapsing them would make deleting operational
/// data the only way to revoke a qualification.
/// </para>
/// </remarks>
public sealed class ProfessionalSpecialty
{
    /// <summary>EF materialization only.</summary>
    private ProfessionalSpecialty()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProfessionalId { get; private set; }

    public Guid SpecialtyId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    public static ProfessionalSpecialty Grant(
        Guid professionalId,
        Guid specialtyId,
        DateTimeOffset createdAtUtc)
    {
        if (professionalId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A qualification requires a professional.");
        }

        if (specialtyId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A qualification requires a specialty.");
        }

        return new ProfessionalSpecialty
        {
            Id = Guid.NewGuid(),
            ProfessionalId = professionalId,
            SpecialtyId = specialtyId,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Revokes the qualification, refusing while active durations depend on it.
    /// </summary>
    /// <remarks>
    /// The count is a parameter for the same reason as everywhere else in this capability: the
    /// rule is domain, the lookup is infrastructure (3a's design D2). Note which side the
    /// caller must count — durations that are themselves <em>active</em>, for appointment types
    /// belonging to <em>this</em> specialty.
    ///
    /// Cascading the durations away instead was rejected on the same ground 3a rejected
    /// cascade-reactivation: silently deleting operational configuration because someone
    /// adjusted a credential is the system making a decision that belongs to the administrator.
    /// </remarks>
    /// <exception cref="CatalogRuleViolationException">Active durations still depend on it.</exception>
    public void Revoke(int activeDependentDurations, DateTimeOffset revokedAtUtc)
    {
        if (activeDependentDurations < 0)
        {
            throw new DomainRuleViolationException("A dependent count cannot be negative.");
        }

        if (activeDependentDurations > 0)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.InUse,
                $"{activeDependentDurations} active duration(s) depend on this qualification.",
                activeDependentDurations);
        }

        DeactivatedAtUtc ??= revokedAtUtc;
    }

    /// <summary>Restores the qualification.</summary>
    public void Restore() => DeactivatedAtUtc = null;
}
