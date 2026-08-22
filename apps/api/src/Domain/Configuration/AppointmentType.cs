namespace Clinic.Domain.Configuration;

/// <summary>
/// A kind of visit the clinic offers — belonging to one <see cref="Specialty"/> and requiring
/// one <see cref="ResourceType"/> (02-domain-model.md §2).
/// </summary>
/// <remarks>
/// <para>
/// The entity that ties the constraints together: given an appointment type, change 4 can
/// derive which professionals are eligible (they must hold its specialty, invariant I2) and
/// which resources qualify (they must be of its required type, I3). That is two of the three
/// scheduling constraints from one reference, which is why this is the last catalog entity to
/// be created and the one whose references must always be resolvable.
/// </para>
/// <para>
/// It deliberately carries <em>no duration</em>. Duration varies per professional × type
/// (Decision C) and lives on the <c>ProfessionalAppointmentType</c> junction in change 3b.
/// Putting a default here would be the obvious convenience and the wrong model: it would make
/// the per-professional value look like an override of a clinic-wide truth, when the clinic
/// has no such truth — Dr. A genuinely takes 40 minutes and Dr. B genuinely takes 50.
/// </para>
/// </remarks>
public sealed class AppointmentType
{
    /// <summary>EF materialization only.</summary>
    private AppointmentType()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The specialty this kind of visit belongs to (feeds I2).</summary>
    public Guid SpecialtyId { get; private set; }

    /// <summary>The kind of room or equipment this visit needs (feeds I3).</summary>
    public Guid RequiredResourceTypeId { get; private set; }

    public string Name { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    public static AppointmentType Define(
        Guid specialtyId,
        Guid requiredResourceTypeId,
        string? name,
        DateTimeOffset createdAtUtc)
    {
        var type = new AppointmentType
        {
            Id = Guid.NewGuid(),
            Name = CatalogName.Normalize(name),
            CreatedAtUtc = createdAtUtc,
        };

        type.Reassign(specialtyId, requiredResourceTypeId);

        return type;
    }

    public void Rename(string? name) => Name = CatalogName.Normalize(name);

    /// <summary>Points the appointment type at a different specialty or required resource type.</summary>
    public void Reassign(Guid specialtyId, Guid requiredResourceTypeId)
    {
        if (specialtyId == Guid.Empty)
        {
            throw new DomainRuleViolationException("An appointment type requires a specialty.");
        }

        if (requiredResourceTypeId == Guid.Empty)
        {
            throw new DomainRuleViolationException("An appointment type requires a resource type.");
        }

        SpecialtyId = specialtyId;
        RequiredResourceTypeId = requiredResourceTypeId;
    }

    /// <summary>
    /// Retires the kind of visit. Nothing in this change references one — in change 5 an
    /// appointment will, and this signature is where that count arrives.
    /// </summary>
    public void Deactivate(DateTimeOffset deactivatedAtUtc) => DeactivatedAtUtc ??= deactivatedAtUtc;

    /// <summary>
    /// Offers the kind of visit again, refusing if either of its references has since been
    /// retired (design D5).
    /// </summary>
    /// <exception cref="CatalogRuleViolationException">The specialty or the resource type is not active.</exception>
    public void Reactivate(bool specialtyIsActive, bool requiredResourceTypeIsActive)
    {
        if (!specialtyIsActive || !requiredResourceTypeIsActive)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.ReferenceInactive,
                "This appointment type's specialty or required resource type is not active; " +
                "reactivate it first.");
        }

        DeactivatedAtUtc = null;
    }
}
