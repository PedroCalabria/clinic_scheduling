namespace Clinic.Domain.Configuration;

/// <summary>
/// A concrete room or piece of equipment of one <see cref="ResourceType"/> — "Sala 2",
/// "Ultrassom 1" (02-domain-model.md §2).
/// </summary>
/// <remarks>
/// The third of the three scheduling constraints lives here: change 4 asks whether a free
/// resource of the required type exists at a candidate time, and change 5's F2 rule assigns
/// one automatically, so a patient never picks a room.
///
/// Nothing in this change references a resource, so retiring one is always permitted. The
/// interesting direction is inward: a resource points at its type, and design D5's rule is
/// that reactivation must not resurrect a row whose type has since been retired.
/// </remarks>
public sealed class Resource
{
    /// <summary>EF materialization only.</summary>
    private Resource()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The kind of thing this is, and therefore whose turnaround buffer applies.</summary>
    public Guid ResourceTypeId { get; private set; }

    public string Name { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    public static Resource Define(Guid resourceTypeId, string? name, DateTimeOffset createdAtUtc)
    {
        if (resourceTypeId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A resource requires a resource type.");
        }

        return new Resource
        {
            Id = Guid.NewGuid(),
            ResourceTypeId = resourceTypeId,
            Name = CatalogName.Normalize(name),
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void Rename(string? name) => Name = CatalogName.Normalize(name);

    /// <summary>Moves the resource to a different type.</summary>
    public void Retype(Guid resourceTypeId)
    {
        if (resourceTypeId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A resource requires a resource type.");
        }

        ResourceTypeId = resourceTypeId;
    }

    /// <summary>Retires the resource. Nothing in this change references one, so nothing blocks.</summary>
    public void Deactivate(DateTimeOffset deactivatedAtUtc) => DeactivatedAtUtc ??= deactivatedAtUtc;

    /// <summary>
    /// Offers the resource again, refusing if its type has since been retired (design D5).
    /// </summary>
    /// <remarks>
    /// Without this guard, retiring a resource and then its now-unreferenced type, then
    /// restoring the resource, produces an active row pointing at an inactive one — exactly
    /// the state the in-use rule exists to prevent, reached through the back door.
    /// </remarks>
    /// <exception cref="CatalogRuleViolationException">The resource type is not active.</exception>
    public void Reactivate(bool resourceTypeIsActive)
    {
        if (!resourceTypeIsActive)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.ReferenceInactive,
                "This resource's type is not active; reactivate the resource type first.");
        }

        DeactivatedAtUtc = null;
    }
}
