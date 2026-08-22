using Clinic.Domain;
using Clinic.Domain.Configuration;
using NodaTime;

namespace Clinic.Domain.UnitTests.Configuration;

/// <summary>
/// The qualification gate and the working-hours rules (design E2, E5).
/// </summary>
/// <remarks>
/// Unit tests, because every rule here is a decision made from facts the caller supplies: does
/// the professional hold the specialty, what segments already exist. The integration tier proves
/// the slices <em>gather</em> those facts correctly, which is the half that actually breaks.
/// </remarks>
public sealed class ProfessionalConfigurationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AProfessional = Guid.NewGuid();

    private static LocalTime At(int hour, int minute = 0) => new(hour, minute);

    private static LocalDate On(int year, int month, int day) => new(year, month, day);

    // --- The qualification gate (E2) -------------------------------------------------

    [Fact]
    public void A_duration_is_accepted_for_a_held_specialty()
    {
        var duration = ProfessionalAppointmentType.Set(
            AProfessional, Guid.NewGuid(), 40, professionalHoldsSpecialty: true, Now);

        Assert.Equal(40, duration.DurationMinutes);
        Assert.True(duration.IsActive);
    }

    [Fact]
    public void A_duration_is_refused_outside_the_held_specialties()
    {
        var refusal = Assert.Throws<CatalogRuleViolationException>(() =>
            ProfessionalAppointmentType.Set(
                AProfessional, Guid.NewGuid(), 40, professionalHoldsSpecialty: false, Now));

        Assert.Equal(CatalogRefusal.SpecialtyNotHeld, refusal.Reason);
    }

    [Fact]
    public void The_gate_is_checked_before_the_duration_length()
    {
        // An unqualified professional with a nonsense duration should be told they are not
        // qualified — reporting a length problem for a visit they may not perform at all sends
        // them to fix the wrong thing.
        var refusal = Assert.Throws<CatalogRuleViolationException>(() =>
            ProfessionalAppointmentType.Set(
                AProfessional, Guid.NewGuid(), -5, professionalHoldsSpecialty: false, Now));

        Assert.Equal(CatalogRefusal.SpecialtyNotHeld, refusal.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_duration_must_be_positive(int minutes)
    {
        // Unlike a turnaround buffer, where zero means "no turnaround needed", a visit of no
        // length is not a visit.
        Assert.Throws<DomainRuleViolationException>(() =>
            ProfessionalAppointmentType.Set(
                AProfessional, Guid.NewGuid(), minutes, professionalHoldsSpecialty: true, Now));
    }

    [Fact]
    public void Restoring_a_duration_re_checks_the_gate()
    {
        var duration = ProfessionalAppointmentType.Set(
            AProfessional, Guid.NewGuid(), 40, professionalHoldsSpecialty: true, Now);

        duration.Clear(Now);

        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => duration.Restore(professionalHoldsSpecialty: false));

        Assert.Equal(CatalogRefusal.SpecialtyNotHeld, refusal.Reason);
        Assert.False(duration.IsActive);
    }

    [Fact]
    public void Revoking_a_qualification_is_refused_while_durations_depend_on_it()
    {
        var qualification = ProfessionalSpecialty.Grant(AProfessional, Guid.NewGuid(), Now);

        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => qualification.Revoke(activeDependentDurations: 3, Now));

        Assert.Equal(CatalogRefusal.InUse, refusal.Reason);
        Assert.Equal(3, refusal.BlockingRecords);
        Assert.True(qualification.IsActive);
    }

    [Fact]
    public void Revoking_a_qualification_nothing_depends_on_succeeds()
    {
        var qualification = ProfessionalSpecialty.Grant(AProfessional, Guid.NewGuid(), Now);

        qualification.Revoke(activeDependentDurations: 0, Now);

        Assert.False(qualification.IsActive);
    }

    // --- Span validity: one predicate, two refusals (E5) -----------------------------

    [Fact]
    public void An_ordinary_morning_span_is_accepted()
    {
        var span = WorkingHoursSpan.Between(At(8), At(12));

        Assert.Equal(At(8), span.Start);
        Assert.Equal(At(12), span.End);
    }

    [Fact]
    public void A_span_crossing_midnight_is_refused()
    {
        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => WorkingHoursSpan.Between(At(22), At(2)));

        Assert.Equal(CatalogRefusal.WorkingHoursInvalid, refusal.Reason);
    }

    [Fact]
    public void A_zero_length_span_is_refused()
    {
        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => WorkingHoursSpan.Between(At(9), At(9)));

        Assert.Equal(CatalogRefusal.WorkingHoursInvalid, refusal.Reason);
    }

    [Fact]
    public void Adjacent_spans_do_not_overlap()
    {
        // A clinic working straight through with no gap is ordinary, so touching endpoints must
        // not be treated as a conflict.
        var morning = WorkingHoursSpan.Between(At(8), At(12));
        var afternoon = WorkingHoursSpan.Between(At(12), At(17));

        Assert.False(morning.Overlaps(afternoon));
        Assert.False(afternoon.Overlaps(morning));
    }

    // --- The two-dimensional overlap rule (E5) --------------------------------------
    //
    // The three cases from the design's table. Two of them must be ALLOWED — a rule with only
    // refusal tests is indistinguishable from one that always refuses.

    [Fact]
    public void Same_weekday_and_period_but_disjoint_times_is_ALLOWED()
    {
        var existing = new[]
        {
            Segment(IsoDayOfWeek.Monday, At(8), At(12), On(2026, 1, 1), On(2026, 6, 30)),
        };

        // Morning and afternoon on the same Monday: the most common real schedule there is.
        var afternoon = WorkingHoursTemplate.Define(
            AProfessional, IsoDayOfWeek.Monday, At(13), At(17),
            On(2026, 1, 1), On(2026, 6, 30), existing, Now);

        Assert.Equal(At(13), afternoon.StartTime);
    }

    [Fact]
    public void Same_weekday_and_times_but_disjoint_periods_is_ALLOWED()
    {
        var existing = new[]
        {
            Segment(IsoDayOfWeek.Monday, At(8), At(12), On(2026, 1, 1), On(2026, 3, 31)),
        };

        // A schedule change that takes effect in April, keeping the same hours.
        var later = WorkingHoursTemplate.Define(
            AProfessional, IsoDayOfWeek.Monday, At(8), At(12),
            On(2026, 4, 1), On(2026, 12, 31), existing, Now);

        Assert.Equal(On(2026, 4, 1), later.EffectiveFrom);
    }

    [Fact]
    public void Same_weekday_with_both_periods_and_times_overlapping_is_REFUSED()
    {
        var existing = new[]
        {
            Segment(IsoDayOfWeek.Monday, At(8), At(12), On(2026, 1, 1), On(2026, 6, 30)),
        };

        var refusal = Assert.Throws<CatalogRuleViolationException>(() =>
            WorkingHoursTemplate.Define(
                AProfessional, IsoDayOfWeek.Monday, At(10), At(14),
                On(2026, 4, 1), On(2026, 12, 31), existing, Now));

        Assert.Equal(CatalogRefusal.WorkingHoursOverlap, refusal.Reason);
    }

    [Fact]
    public void A_different_weekday_never_conflicts()
    {
        var existing = new[]
        {
            Segment(IsoDayOfWeek.Monday, At(8), At(12), On(2026, 1, 1), null),
        };

        var tuesday = WorkingHoursTemplate.Define(
            AProfessional, IsoDayOfWeek.Tuesday, At(8), At(12),
            On(2026, 1, 1), null, existing, Now);

        Assert.Equal(IsoDayOfWeek.Tuesday, tuesday.DayOfWeek);
    }

    [Fact]
    public void A_retired_segment_stops_blocking_new_ones()
    {
        var retired = Segment(IsoDayOfWeek.Monday, At(8), At(12), On(2026, 1, 1), null);
        retired.Retire(Now);

        var replacement = WorkingHoursTemplate.Define(
            AProfessional, IsoDayOfWeek.Monday, At(8), At(12),
            On(2026, 1, 1), null, [retired], Now);

        Assert.True(replacement.IsActive);
    }

    [Fact]
    public void An_open_ended_period_overlaps_everything_after_it_starts()
    {
        var existing = new[]
        {
            Segment(IsoDayOfWeek.Monday, At(8), At(12), On(2026, 1, 1), null),
        };

        var refusal = Assert.Throws<CatalogRuleViolationException>(() =>
            WorkingHoursTemplate.Define(
                AProfessional, IsoDayOfWeek.Monday, At(9), At(11),
                On(2030, 1, 1), null, existing, Now));

        Assert.Equal(CatalogRefusal.WorkingHoursOverlap, refusal.Reason);
    }

    [Fact]
    public void A_segment_does_not_conflict_with_its_own_stored_row_when_adjusted()
    {
        var segment = Segment(IsoDayOfWeek.Monday, At(8), At(12), On(2026, 1, 1), null);

        segment.Adjust(At(9), At(13), On(2026, 1, 1), null, [segment]);

        Assert.Equal(At(9), segment.StartTime);
    }

    [Fact]
    public void An_effective_period_ending_before_it_starts_is_refused()
    {
        var refusal = Assert.Throws<CatalogRuleViolationException>(
            () => EffectivePeriod.Between(On(2026, 6, 1), On(2026, 1, 1)));

        Assert.Equal(CatalogRefusal.WorkingHoursInvalid, refusal.Reason);
    }

    // --- Exceptions (E4) ------------------------------------------------------------

    [Fact]
    public void An_unavailable_day_carries_no_hours()
    {
        var exception = WorkingHoursException.Unavailable(AProfessional, On(2026, 12, 25), Now);

        Assert.True(exception.IsUnavailableAllDay);
        Assert.Null(exception.Span);
    }

    [Fact]
    public void Different_hours_on_a_date_carry_a_span()
    {
        var exception = WorkingHoursException.DifferentHours(
            AProfessional, On(2026, 12, 24), At(8), At(12), Now);

        Assert.False(exception.IsUnavailableAllDay);
        Assert.Equal(At(12), exception.Span!.Value.End);
    }

    [Fact]
    public void An_exceptions_hours_obey_the_same_validity_rule()
    {
        // The likelier place for a hurried overnight entry than a recurring segment.
        var refusal = Assert.Throws<CatalogRuleViolationException>(() =>
            WorkingHoursException.DifferentHours(
                AProfessional, On(2026, 12, 24), At(22), At(2), Now));

        Assert.Equal(CatalogRefusal.WorkingHoursInvalid, refusal.Reason);
    }

    [Fact]
    public void A_second_exception_on_the_same_date_is_refused()
    {
        var refusal = Assert.Throws<CatalogRuleViolationException>(() =>
            WorkingHoursException.EnsureNoneFor(
                AProfessional, On(2026, 12, 25), activeExceptionAlreadyExists: true));

        Assert.Equal(CatalogRefusal.WorkingHoursOverlap, refusal.Reason);
    }

    [Fact]
    public void A_date_with_no_active_exception_accepts_one()
    {
        WorkingHoursException.EnsureNoneFor(
            AProfessional, On(2026, 12, 25), activeExceptionAlreadyExists: false);
    }

    // --- Shape: nothing here is an instant (E3) -------------------------------------

    [Fact]
    public void No_working_hours_property_is_an_instant()
    {
        // The type is the documentation. If someone later changes StartTime to a DateTimeOffset
        // "for convenience", this fails and they have to argue for it.
        var offsetTyped = typeof(WorkingHoursTemplate)
            .GetProperties()
            .Where(property => property.Name is nameof(WorkingHoursTemplate.StartTime)
                or nameof(WorkingHoursTemplate.EndTime)
                or nameof(WorkingHoursTemplate.EffectiveFrom)
                or nameof(WorkingHoursTemplate.EffectiveTo))
            .Where(property => property.PropertyType == typeof(DateTimeOffset)
                || property.PropertyType == typeof(DateTimeOffset?)
                || property.PropertyType == typeof(DateTime)
                || property.PropertyType == typeof(DateTime?)
                || property.PropertyType == typeof(Instant)
                || property.PropertyType == typeof(Instant?))
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(offsetTyped);
    }

    // --- Professional (E1) ----------------------------------------------------------

    [Fact]
    public void A_configuration_record_requires_a_user()
    {
        Assert.Throws<DomainRuleViolationException>(() => Professional.ForUser(Guid.Empty, Now));
    }

    [Fact]
    public void A_configuration_record_holds_no_name_or_email()
    {
        // Those live on the User. Duplicating them here would create two answers to one
        // question, and the stale copy would be the one on screen.
        var properties = typeof(Professional).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("Email", properties);
        Assert.DoesNotContain("FullName", properties);
        Assert.DoesNotContain("Name", properties);
    }

    private static WorkingHoursTemplate Segment(
        IsoDayOfWeek day,
        LocalTime start,
        LocalTime end,
        LocalDate from,
        LocalDate? to) =>
        WorkingHoursTemplate.Define(AProfessional, day, start, end, from, to, [], Now);
}
