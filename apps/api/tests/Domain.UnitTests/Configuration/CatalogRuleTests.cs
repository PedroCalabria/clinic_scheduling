using Clinic.Domain;
using Clinic.Domain.Configuration;

namespace Clinic.Domain.UnitTests.Configuration;

/// <summary>
/// Covers the catalog's reference and uniqueness rules (design D1-D3, D5).
/// </summary>
/// <remarks>
/// Unit tests, because these are decisions the protected core makes from facts handed to it:
/// a dependent count, whether a name is held, whether a reference is active. The integration
/// tier proves the slices <em>obtain</em> those facts correctly — which is the half that
/// actually breaks, since "active" is a predicate on the dependent and not on the target.
/// </remarks>
public sealed class CatalogRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    // --- Deactivation: the dependent count (D2) --------------------------------------

    [Fact]
    public void A_specialty_with_no_active_appointment_types_deactivates()
    {
        var specialty = Specialty.Define("Cardiologia", Now);

        specialty.Deactivate(activeAppointmentTypes: 0, Now);

        Assert.False(specialty.IsActive);
        Assert.Equal(Now, specialty.DeactivatedAtUtc);
    }

    [Fact]
    public void A_specialty_with_an_active_appointment_type_is_refused_as_in_use()
    {
        var specialty = Specialty.Define("Cardiologia", Now);

        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => specialty.Deactivate(activeAppointmentTypes: 1, Now));

        Assert.Equal(CatalogRefusal.InUse, refusal.Reason);
        Assert.True(specialty.IsActive);
    }

    [Fact]
    public void A_resource_type_is_refused_when_only_resources_depend_on_it()
    {
        var type = ResourceType.Define("Consultório", 15, Now);

        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => type.Deactivate(activeResources: 2, activeAppointmentTypes: 0, Now));

        Assert.Equal(CatalogRefusal.InUse, refusal.Reason);
        Assert.True(type.IsActive);
    }

    [Fact]
    public void A_resource_type_is_refused_when_only_appointment_types_require_it()
    {
        // The half a single-count signature would have silently dropped: no resources exist
        // of this type, but a kind of visit still requires it.
        var type = ResourceType.Define("Sala de ultrassom", 20, Now);

        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => type.Deactivate(activeResources: 0, activeAppointmentTypes: 1, Now));

        Assert.Equal(CatalogRefusal.InUse, refusal.Reason);
        Assert.True(type.IsActive);
    }

    [Fact]
    public void A_resource_type_with_neither_kind_of_dependent_deactivates()
    {
        var type = ResourceType.Define("Sala de ultrassom", 20, Now);

        type.Deactivate(activeResources: 0, activeAppointmentTypes: 0, Now);

        Assert.False(type.IsActive);
    }

    [Fact]
    public void Deactivating_an_already_inactive_entity_is_a_no_op_not_an_error()
    {
        var specialty = Specialty.Define("Dermatologia", Now);
        specialty.Deactivate(0, Now);

        var later = Now.AddDays(1);
        specialty.Deactivate(0, later);

        // The original moment is kept: retirement happened once, and a repeated call must not
        // rewrite when.
        Assert.Equal(Now, specialty.DeactivatedAtUtc);
    }

    [Fact]
    public void A_negative_dependent_count_is_a_programming_error_not_a_refusal()
    {
        var specialty = Specialty.Define("Cardiologia", Now);

        Assert.Throws<DomainRuleViolationException>(() => specialty.Deactivate(-1, Now));
    }

    // --- Reactivation: outbound references (D5) --------------------------------------

    [Fact]
    public void A_resource_cannot_be_reactivated_while_its_type_is_inactive()
    {
        var resource = Resource.Define(Guid.NewGuid(), "Sala 2", Now);
        resource.Deactivate(Now);

        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => resource.Reactivate(resourceTypeIsActive: false));

        Assert.Equal(CatalogRefusal.ReferenceInactive, refusal.Reason);
        Assert.False(resource.IsActive);
    }

    [Fact]
    public void A_resource_reactivates_when_its_type_is_active()
    {
        var resource = Resource.Define(Guid.NewGuid(), "Sala 2", Now);
        resource.Deactivate(Now);

        resource.Reactivate(resourceTypeIsActive: true);

        Assert.True(resource.IsActive);
        Assert.Null(resource.DeactivatedAtUtc);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void An_appointment_type_needs_both_references_active_to_reactivate(
        bool specialtyActive,
        bool resourceTypeActive)
    {
        var type = AppointmentType.Define(Guid.NewGuid(), Guid.NewGuid(), "Consulta", Now);
        type.Deactivate(Now);

        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => type.Reactivate(specialtyActive, resourceTypeActive));

        Assert.Equal(CatalogRefusal.ReferenceInactive, refusal.Reason);
        Assert.False(type.IsActive);
    }

    [Fact]
    public void An_appointment_type_reactivates_when_both_references_are_active()
    {
        var type = AppointmentType.Define(Guid.NewGuid(), Guid.NewGuid(), "Consulta", Now);
        type.Deactivate(Now);

        type.Reactivate(specialtyIsActive: true, requiredResourceTypeIsActive: true);

        Assert.True(type.IsActive);
    }

    // --- The buffer (F1) ------------------------------------------------------------

    [Fact]
    public void A_negative_turnaround_buffer_is_refused_at_definition()
    {
        Assert.Throws<DomainRuleViolationException>(() => ResourceType.Define("Consultório", -1, Now));
    }

    [Fact]
    public void A_negative_turnaround_buffer_is_refused_on_edit()
    {
        var type = ResourceType.Define("Consultório", 15, Now);

        Assert.Throws<DomainRuleViolationException>(() => type.ChangeBuffer(-5));

        Assert.Equal(15, type.BufferMinutes);
    }

    [Fact]
    public void A_zero_turnaround_buffer_is_legitimate()
    {
        // A room that needs no turnaround is a real case, so zero must not be conflated with
        // "unset" and refused.
        var type = ResourceType.Define("Sala de espera", 0, Now);

        Assert.Equal(0, type.BufferMinutes);
    }

    // --- Names (D3) -----------------------------------------------------------------

    [Fact]
    public void A_name_is_trimmed_and_inner_whitespace_collapsed()
    {
        var resource = Resource.Define(Guid.NewGuid(), "  Sala   2  ", Now);

        Assert.Equal("Sala 2", resource.Name);
    }

    [Fact]
    public void A_names_comparison_key_ignores_case_and_surrounding_space()
    {
        Assert.Equal(
            CatalogName.ComparisonKey("Cardiologia"),
            CatalogName.ComparisonKey("  cardiOLOGIA "));
    }

    [Fact]
    public void Display_case_survives_normalization()
    {
        // The comparison is case-insensitive; what is stored is what a patient reads.
        var specialty = Specialty.Define("Cardiologia", Now);

        Assert.Equal("Cardiologia", specialty.Name);
    }

    [Fact]
    public void An_empty_name_is_refused()
    {
        Assert.Throws<DomainRuleViolationException>(() => Specialty.Define("   ", Now));
        Assert.Throws<DomainRuleViolationException>(() => Specialty.Define(null, Now));
    }

    [Fact]
    public void A_name_longer_than_the_column_is_refused()
    {
        var tooLong = new string('a', CatalogName.MaxLength + 1);

        Assert.Throws<DomainRuleViolationException>(() => Specialty.Define(tooLong, Now));
    }

    [Fact]
    public void A_name_held_by_another_active_record_is_unavailable()
    {
        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => CatalogName.EnsureAvailable(heldByAnotherActiveRecord: true));

        Assert.Equal(CatalogRefusal.DuplicateName, refusal.Reason);
    }

    [Fact]
    public void A_name_no_active_record_holds_is_available()
    {
        CatalogName.EnsureAvailable(heldByAnotherActiveRecord: false);
    }

    // --- Shape: duration is deliberately absent (Decision C) -------------------------

    [Fact]
    public void An_appointment_type_carries_no_duration()
    {
        // Duration is per professional × type and lives on change 3b's junction. A default
        // here would make the per-professional value look like an override of a clinic-wide
        // truth the clinic does not have.
        var properties = typeof(AppointmentType)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("DurationMinutes", properties);
        Assert.DoesNotContain("Duration", properties);
    }

    [Fact]
    public void An_appointment_type_requires_both_of_its_references()
    {
        Assert.Throws<DomainRuleViolationException>(
            () => AppointmentType.Define(Guid.Empty, Guid.NewGuid(), "Consulta", Now));

        Assert.Throws<DomainRuleViolationException>(
            () => AppointmentType.Define(Guid.NewGuid(), Guid.Empty, "Consulta", Now));
    }

    [Fact]
    public void A_resource_requires_a_type()
    {
        Assert.Throws<DomainRuleViolationException>(
            () => Resource.Define(Guid.Empty, "Sala 2", Now));
    }
}
