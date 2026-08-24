using Clinic.Domain.Scheduling;
using NodaTime;

namespace Clinic.Domain.UnitTests.Scheduling;

/// <summary>
/// The <c>Appointment</c> aggregate's invariants — I1, I2, I3 and I8 (design B2).
/// </summary>
/// <remarks>
/// <para>
/// These are the tests for the layer that makes an invalid appointment <b>impossible to
/// construct</b>. That is a different job from the solver's <c>Explain</c>, which names a cause
/// for a friendly message, and from the database's exclusion constraints, which make the
/// guarantee hold under a race. This layer is what stops <c>booking-lifecycle</c>'s second write
/// path from reintroducing a bug this change fixes, and it is the only one of the three reachable
/// without a database.
/// </para>
/// <para>
/// Note what is asserted about the state machine: that all five values exist and that only one is
/// reachable. Both halves matter — the enum is declared whole so I9 describes the real shape, and
/// nothing here may transition, because those guards belong to 5b.
/// </para>
/// </remarks>
public sealed class AppointmentTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid PatientId = Guid.NewGuid();
    private static readonly Guid ProfessionalId = Guid.NewGuid();
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid VisitTypeId = Guid.NewGuid();
    private static readonly Guid ConsultationRoomType = Guid.NewGuid();
    private static readonly Guid UltrasoundRoomType = Guid.NewGuid();

    private static readonly Instant Now = Instant.FromUtc(2026, 8, 24, 12, 0);

    /// <summary>Tomorrow, so neither the lead time nor the horizon is in play by default.</summary>
    private static readonly Instant Tomorrow = Now + Duration.FromDays(1);

    /// <summary>An hour's lead time and a sixty-day horizon — the configured defaults.</summary>
    private static SchedulingParameters Parameters(int leadMinutes = 60, int horizonDays = 60) =>
        SchedulingParameters.Of(15, leadMinutes, horizonDays);

    private static AppointmentBooking Booking(
        Instant? startsAt = null,
        int durationMinutes = 40,
        bool qualified = true,
        Guid? resourceTypeId = null,
        Guid? requiredResourceTypeId = null,
        Guid? patientId = null) =>
        new(
            patientId ?? PatientId,
            ProfessionalId,
            RoomId,
            VisitTypeId,
            startsAt ?? Tomorrow,
            durationMinutes,
            qualified,
            resourceTypeId ?? ConsultationRoomType,
            requiredResourceTypeId ?? ConsultationRoomType,
            AppointmentSource.SelfService);

    private static Appointment Book(AppointmentBooking booking, SchedulingParameters? parameters = null) =>
        Appointment.Book(booking, parameters ?? Parameters(), Now, Recorded);

    // --- I1: the duration is baked in ------------------------------------------------

    [Fact]
    public void The_range_is_exactly_the_supplied_duration()
    {
        var appointment = Book(Booking(durationMinutes: 40));

        Assert.Equal(Tomorrow, appointment.StartsAt);
        Assert.Equal(Tomorrow + Duration.FromMinutes(40), appointment.EndsAt);
    }

    [Fact]
    public void Two_professionals_durations_produce_two_different_lengths()
    {
        var shorter = Book(Booking(durationMinutes: 40));
        var longer = Book(Booking(durationMinutes: 50));

        // Decision C's whole point, arriving at the write path: the appointment is as long as
        // this professional takes, not as long as the clinic nominally allows.
        Assert.Equal(Duration.FromMinutes(40), shorter.EndsAt - shorter.StartsAt);
        Assert.Equal(Duration.FromMinutes(50), longer.EndsAt - longer.StartsAt);
    }

    [Fact]
    public void The_appointment_holds_its_own_range_so_a_later_duration_change_cannot_reach_it()
    {
        var appointment = Book(Booking(durationMinutes: 40));

        var bookedRange = (appointment.StartsAt, appointment.EndsAt);

        // The "change" a configuration edit would make: booking the same slot again now yields
        // a different length, and the row above is untouched by it. There is deliberately no
        // API on the aggregate through which a duration could be re-applied — I1 holds because
        // the range is stored, not because a rule is remembered (02 §6).
        var rebooked = Book(Booking(durationMinutes: 60));

        Assert.Equal(bookedRange, (appointment.StartsAt, appointment.EndsAt));
        Assert.NotEqual(appointment.EndsAt - appointment.StartsAt, rebooked.EndsAt - rebooked.StartsAt);
    }

    [Fact]
    public void A_non_positive_duration_is_refused_as_structurally_impossible()
    {
        // Not a BookingRefusal: no request can express it — configuration refuses a zero
        // duration — so there is no message a patient could act on.
        Assert.Throws<DomainRuleViolationException>(() => Book(Booking(durationMinutes: 0)));
        Assert.Throws<DomainRuleViolationException>(() => Book(Booking(durationMinutes: -30)));
    }

    // --- I2: the professional must be qualified --------------------------------------

    [Fact]
    public void A_professional_without_a_duration_for_the_type_is_refused_as_a_specialty_mismatch()
    {
        var refusal = Assert.Throws<BookingRuleViolationException>(
            () => Book(Booking(qualified: false)));

        Assert.Equal(BookingRefusal.SpecialtyMismatch, refusal.Reason);
    }

    [Fact]
    public void The_qualification_check_precedes_the_lead_time_check()
    {
        // Both broken at once. The refusal names the one that will not change by waiting, which
        // is the more useful sentence.
        var refusal = Assert.Throws<BookingRuleViolationException>(
            () => Book(Booking(startsAt: Now, qualified: false)));

        Assert.Equal(BookingRefusal.SpecialtyMismatch, refusal.Reason);
    }

    // --- I3: the room must be of the required type -----------------------------------

    [Fact]
    public void A_room_of_the_wrong_type_cannot_be_persisted_by_any_path()
    {
        // Structurally impossible rather than a refusal with a code: the server chooses the room
        // itself (domain-model F2), so a mismatch means the server chose wrongly. That is a bug,
        // and answering a patient with a business-rule code would misdescribe it.
        Assert.Throws<DomainRuleViolationException>(() => Book(Booking(
            resourceTypeId: UltrasoundRoomType,
            requiredResourceTypeId: ConsultationRoomType)));
    }

    [Fact]
    public void A_room_of_the_required_type_is_accepted()
    {
        var appointment = Book(Booking(
            resourceTypeId: UltrasoundRoomType,
            requiredResourceTypeId: UltrasoundRoomType));

        Assert.Equal(RoomId, appointment.ResourceId);
    }

    // --- I8: lead time and horizon ---------------------------------------------------

    [Fact]
    public void A_start_inside_the_lead_time_is_refused()
    {
        var refusal = Assert.Throws<BookingRuleViolationException>(
            () => Book(Booking(startsAt: Now + Duration.FromMinutes(30)), Parameters(leadMinutes: 60)));

        Assert.Equal(BookingRefusal.LeadTimeViolation, refusal.Reason);
    }

    [Fact]
    public void A_start_exactly_at_the_lead_time_boundary_is_accepted()
    {
        // The boundary is inclusive on the permitted side, matching the solver's comparison. If
        // these two disagreed, the read would offer a slot the write refuses — the drift this
        // whole change is arranged to make impossible.
        var appointment = Book(
            Booking(startsAt: Now + Duration.FromMinutes(60)),
            Parameters(leadMinutes: 60));

        Assert.Equal(Now + Duration.FromMinutes(60), appointment.StartsAt);
    }

    [Fact]
    public void A_start_beyond_the_horizon_is_refused()
    {
        var refusal = Assert.Throws<BookingRuleViolationException>(
            () => Book(Booking(startsAt: Now + Duration.FromDays(61)), Parameters(horizonDays: 60)));

        Assert.Equal(BookingRefusal.HorizonExceeded, refusal.Reason);
    }

    [Fact]
    public void A_start_exactly_at_the_horizon_is_accepted()
    {
        var appointment = Book(
            Booking(startsAt: Now + Duration.FromDays(60)),
            Parameters(horizonDays: 60));

        Assert.Equal(Now + Duration.FromDays(60), appointment.StartsAt);
    }

    [Fact]
    public void A_zero_lead_time_permits_booking_from_now()
    {
        // Zero is a legitimate configuration — some clinics take walk-ins — so it must not be
        // treated as "unset" anywhere.
        var appointment = Book(Booking(startsAt: Now), Parameters(leadMinutes: 0));

        Assert.Equal(Now, appointment.StartsAt);
    }

    // --- References ------------------------------------------------------------------

    [Theory]
    [InlineData("patient")]
    [InlineData("professional")]
    [InlineData("resource")]
    [InlineData("appointmentType")]
    public void A_missing_reference_is_refused(string missing)
    {
        var booking = Booking() with
        {
            PatientId = missing == "patient" ? Guid.Empty : PatientId,
            ProfessionalId = missing == "professional" ? Guid.Empty : ProfessionalId,
            ResourceId = missing == "resource" ? Guid.Empty : RoomId,
            AppointmentTypeId = missing == "appointmentType" ? Guid.Empty : VisitTypeId,
        };

        Assert.Throws<DomainRuleViolationException>(() => Book(booking));
    }

    // --- The state machine (B10) -----------------------------------------------------

    [Fact]
    public void Booking_produces_the_scheduled_state()
    {
        Assert.Equal(AppointmentStatus.Scheduled, Book(Booking()).Status);
    }

    [Fact]
    public void The_state_machine_declares_all_five_states()
    {
        // The enum is whole because I9 is a statement about a shape, and a two-value enum would
        // misdescribe it while making 5b a column rewrite instead of new code.
        Assert.Equal(
            [
                AppointmentStatus.Scheduled,
                AppointmentStatus.Completed,
                AppointmentStatus.NoShow,
                AppointmentStatus.Cancelled,
                AppointmentStatus.Rescheduled,
            ],
            Enum.GetValues<AppointmentStatus>());
    }

    [Fact]
    public void Only_the_scheduled_state_is_live()
    {
        var appointment = Book(Booking());

        Assert.True(appointment.IsLive);

        // The other four are terminal, and "live" is the predicate the database's exclusion
        // constraints express as WHERE status = 'Scheduled'. One notion of live, named the same
        // way in both places, is why 5b frees a slot with no migration.
        var terminal = Enum.GetValues<AppointmentStatus>()
            .Where(status => status != AppointmentStatus.Scheduled)
            .ToArray();

        Assert.Equal(4, terminal.Length);
    }

    [Fact]
    public void The_aggregate_offers_exactly_two_transitions_and_no_way_to_move_a_range()
    {
        // `booking-core` asserted this list was EMPTY. `booking-lifecycle` adds two members to it
        // and no more — so `Completed` and `NoShow` stay unreachable by inspection rather than by
        // reading a transition table, and `booking-desk` adding a third has to come here and say
        // so. Asserted structurally rather than by trying to call something: an unplanned public
        // mutator appearing is the regression to catch.
        var mutators = typeof(Appointment)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(method => !method.IsSpecialName && method.DeclaringType == typeof(Appointment))
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal([nameof(Appointment.Cancel), nameof(Appointment.RescheduleTo)], mutators);

        // Still empty, and this half has not moved: a reschedule CREATES. Nothing may edit an
        // existing appointment's range, which is what keeps the audit trail honest (design C5).
        var settable = typeof(Appointment)
            .GetProperties()
            .Where(property => property.SetMethod?.IsPublic == true)
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(settable);
    }

    // --- The busy-interval contribution (B11) ----------------------------------------

    [Fact]
    public void A_live_appointment_contributes_a_busy_interval_naming_its_cause()
    {
        var appointment = Book(Booking());

        var interval = appointment.Interval;

        Assert.Equal(appointment.StartsAt, interval.Start);
        Assert.Equal(appointment.EndsAt, interval.End);

        // The cause is what lets the booking path answer `slot_taken` rather than
        // `slot_blocked`. The subtraction itself ignores it (design F5, still true).
        Assert.Equal(BusyCause.Appointment, interval.Cause);
    }

    [Fact]
    public void Only_live_appointments_reach_the_busy_set()
    {
        var live = Book(Booking());

        Assert.Single(Appointment.BusyIntervalsOf([live]));

        // 5a could only say this at the integration tier, by writing a terminal row directly.
        // The transitions exist now, so the claim is reachable from here — which is the same
        // shift the whole change is about.
        var cancelled = Book(Booking());
        cancelled.Cancel(Cutoff(), Now, cutoffApplies: true);

        var rescheduled = Book(Booking());
        rescheduled.RescheduleTo(Booking(Tomorrow + Duration.FromHours(2)), Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true);

        Assert.Empty(Appointment.BusyIntervalsOf([cancelled, rescheduled]));
        Assert.Single(Appointment.BusyIntervalsOf([live, cancelled, rescheduled]));

        Assert.Empty(Appointment.BusyIntervalsOf([]));
    }

    /// <summary>The configured default — 24 hours (domain-model F3).</summary>
    private static CancellationCutoffPolicy Cutoff(int hours = 24) => CancellationCutoffPolicy.Of(hours);
}
