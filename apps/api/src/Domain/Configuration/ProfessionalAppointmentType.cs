namespace Clinic.Domain.Configuration;

/// <summary>
/// How long one professional takes for one kind of visit (Decision C, 02-domain-model.md §2).
/// </summary>
/// <remarks>
/// <para>
/// This is the entity that lets Dr. A run a cardiology visit in 40 minutes and Dr. B in 50, and
/// the reason <see cref="AppointmentType"/> carries no duration of its own. Change 4 slices
/// availability by this value; change 5 bakes it into the appointment's own interval at booking
/// time (invariant I1), so a later change to the number never moves an existing appointment.
/// </para>
/// <para>
/// The qualification gate lives on the factory (design E2): a duration cannot be created for an
/// appointment type whose specialty the professional does not hold. The caller performs that
/// lookup and passes the answer, the same split every rule in this capability uses — the rule
/// is domain, the query is infrastructure.
/// </para>
/// </remarks>
public sealed class ProfessionalAppointmentType
{
    /// <summary>EF materialization only.</summary>
    private ProfessionalAppointmentType()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProfessionalId { get; private set; }

    public Guid AppointmentTypeId { get; private set; }

    /// <summary>Minutes this professional takes for this kind of visit.</summary>
    public int DurationMinutes { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    /// <summary>
    /// Records a duration, refusing unless the professional holds the appointment type's
    /// specialty.
    /// </summary>
    /// <param name="professionalHoldsSpecialty">
    /// Whether an active <see cref="ProfessionalSpecialty"/> exists for the specialty this
    /// appointment type belongs to. The caller looks it up; this factory decides what the
    /// answer means.
    /// </param>
    /// <exception cref="CatalogRuleViolationException">The professional is not qualified.</exception>
    /// <exception cref="DomainRuleViolationException">A reference is missing, or the duration is unusable.</exception>
    public static ProfessionalAppointmentType Set(
        Guid professionalId,
        Guid appointmentTypeId,
        int durationMinutes,
        bool professionalHoldsSpecialty,
        DateTimeOffset createdAtUtc)
    {
        if (professionalId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A duration requires a professional.");
        }

        if (appointmentTypeId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A duration requires an appointment type.");
        }

        // The gate comes before the duration check on purpose: "you are not qualified for this"
        // is the more fundamental refusal, and reporting a length problem for a visit the
        // professional may not perform at all would be misleading.
        if (!professionalHoldsSpecialty)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.SpecialtyNotHeld,
                "This professional does not hold the specialty this appointment type belongs to.");
        }

        var duration = new ProfessionalAppointmentType
        {
            Id = Guid.NewGuid(),
            ProfessionalId = professionalId,
            AppointmentTypeId = appointmentTypeId,
            CreatedAtUtc = createdAtUtc,
        };

        duration.ChangeDuration(durationMinutes);

        return duration;
    }

    /// <summary>
    /// Changes the duration. Zero and negative are refused — unlike a turnaround buffer, where
    /// zero is a legitimate "no turnaround needed", a visit of no length is not a visit.
    /// </summary>
    /// <exception cref="DomainRuleViolationException">The duration is not a positive number of minutes.</exception>
    public void ChangeDuration(int durationMinutes)
    {
        if (durationMinutes <= 0)
        {
            throw new DomainRuleViolationException("A duration must be a positive number of minutes.");
        }

        DurationMinutes = durationMinutes;
    }

    /// <summary>Stops offering this kind of visit for this professional.</summary>
    public void Clear(DateTimeOffset clearedAtUtc) => DeactivatedAtUtc ??= clearedAtUtc;

    /// <summary>
    /// Offers it again, refusing if the qualification it depends on is no longer held
    /// (the same back door design D5 closed in 3a).
    /// </summary>
    /// <exception cref="CatalogRuleViolationException">The professional no longer holds the specialty.</exception>
    public void Restore(bool professionalHoldsSpecialty)
    {
        if (!professionalHoldsSpecialty)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.SpecialtyNotHeld,
                "This professional no longer holds the specialty this appointment type belongs to.");
        }

        DeactivatedAtUtc = null;
    }
}
