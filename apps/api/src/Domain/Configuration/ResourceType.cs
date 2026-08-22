namespace Clinic.Domain.Configuration;

/// <summary>
/// A kind of room or piece of equipment — consultation room, ultrasound room — carrying the
/// turnaround buffer (decision F1, 02-domain-model.md §2).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BufferMinutes"/> is the reason this entity is more than a label. Change 4's
/// availability computation treats a resource's occupied interval as
/// <c>[start, end + bufferMinutes)</c>, which keeps cleaning and prep time out of the
/// bookable window. Storing it here rather than on the appointment type is deliberate:
/// turnaround is a property of the room, not of the reason someone is in it.
/// </para>
/// <para>
/// It is also the entity with two kinds of dependent, which is why <see cref="Deactivate"/>
/// takes two counts. A resource type can be pointed at by concrete resources of that type
/// <em>and</em> by appointment types that require it, and either alone must block retirement —
/// a signature with one count would silently let half the rule go unchecked.
/// </para>
/// </remarks>
public sealed class ResourceType
{
    /// <summary>EF materialization only.</summary>
    private ResourceType()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>Turnaround minutes kept out of the bookable window (F1).</summary>
    public int BufferMinutes { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    public static ResourceType Define(string? name, int bufferMinutes, DateTimeOffset createdAtUtc)
    {
        var type = new ResourceType
        {
            Id = Guid.NewGuid(),
            Name = CatalogName.Normalize(name),
            CreatedAtUtc = createdAtUtc,
        };

        type.ChangeBuffer(bufferMinutes);

        return type;
    }

    public void Rename(string? name) => Name = CatalogName.Normalize(name);

    /// <summary>
    /// Sets the turnaround buffer. Zero is legitimate — a room that needs no turnaround.
    /// </summary>
    /// <remarks>
    /// No upper bound, deliberately. A cap would have to be invented rather than derived, and
    /// a wrong cap refuses a genuinely long equipment turnaround, which is worse than allowing
    /// an implausible one. The recorded consequence is that a fat-fingered value surfaces in
    /// change 4 as "no slots available" — see the design's risk list.
    /// </remarks>
    /// <exception cref="DomainRuleViolationException">The buffer is negative.</exception>
    public void ChangeBuffer(int bufferMinutes)
    {
        if (bufferMinutes < 0)
        {
            throw new DomainRuleViolationException("A turnaround buffer cannot be negative.");
        }

        BufferMinutes = bufferMinutes;
    }

    /// <summary>
    /// Retires the resource type, refusing while active resources are of it or active
    /// appointment types require it (design D2).
    /// </summary>
    /// <exception cref="CatalogRuleViolationException">Either kind of active dependent exists.</exception>
    public void Deactivate(
        int activeResources,
        int activeAppointmentTypes,
        DateTimeOffset deactivatedAtUtc)
    {
        if (activeResources < 0 || activeAppointmentTypes < 0)
        {
            throw new DomainRuleViolationException("A dependent count cannot be negative.");
        }

        if (activeResources > 0 || activeAppointmentTypes > 0)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.InUse,
                $"{activeResources} active resource(s) and {activeAppointmentTypes} active " +
                "appointment type(s) still depend on this resource type.",
                activeResources + activeAppointmentTypes);
        }

        DeactivatedAtUtc ??= deactivatedAtUtc;
    }

    /// <summary>Offers the resource type again. It points at nothing, so nothing to re-validate.</summary>
    public void Reactivate() => DeactivatedAtUtc = null;
}
