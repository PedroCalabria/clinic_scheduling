using Clinic.Domain;
using Clinic.Domain.Scheduling;
using NodaTime;

namespace Clinic.Domain.UnitTests.Scheduling;

/// <summary>
/// The two transitions a patient owns — cancel and reschedule (02 §3, design C1–C5).
/// </summary>
/// <remarks>
/// <para>
/// A separate file from <c>AppointmentTests</c> deliberately. That one is about an appointment
/// being impossible to <em>construct</em> wrongly; this one is about it being impossible to
/// <em>change</em> wrongly. They share a type and almost nothing else, and the invariants there
/// are re-enforced here for free because <c>RescheduleTo</c> builds its replacement through the
/// same factory.
/// </para>
/// <para>
/// <b>The cutoff is exercised in both directions of its authority parameter, and only one of them
/// has a caller.</b> <c>cutoffApplies: false</c> is what <c>booking-desk</c> will pass when the
/// front desk acts inside the cutoff; testing it now is the specification of that behaviour,
/// written while the reasoning is fresh rather than inferred from a signature later (design C4).
/// </para>
/// </remarks>
public sealed class AppointmentLifecycleTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid PatientId = Guid.NewGuid();
    private static readonly Guid ProfessionalId = Guid.NewGuid();
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid VisitTypeId = Guid.NewGuid();
    private static readonly Guid ConsultationRoomType = Guid.NewGuid();
    private static readonly Guid UltrasoundRoomType = Guid.NewGuid();

    private static readonly Instant Now = Instant.FromUtc(2026, 8, 24, 12, 0);

    /// <summary>Well outside a 24-hour cutoff, so the cutoff is not in play unless asked for.</summary>
    private static readonly Instant NextWeek = Now + Duration.FromDays(7);

    private static SchedulingParameters Parameters(int leadMinutes = 60, int horizonDays = 60) =>
        SchedulingParameters.Of(15, leadMinutes, horizonDays);

    private static CancellationCutoffPolicy Cutoff(int hours = 24) => CancellationCutoffPolicy.Of(hours);

    private static AppointmentBooking Booking(
        Instant? startsAt = null,
        int durationMinutes = 40,
        bool qualified = true,
        Guid? patientId = null,
        Guid? professionalId = null,
        Guid? appointmentTypeId = null,
        Guid? resourceTypeId = null,
        Guid? requiredResourceTypeId = null) =>
        new(
            patientId ?? PatientId,
            professionalId ?? ProfessionalId,
            RoomId,
            appointmentTypeId ?? VisitTypeId,
            startsAt ?? NextWeek,
            durationMinutes,
            qualified,
            resourceTypeId ?? ConsultationRoomType,
            requiredResourceTypeId ?? ConsultationRoomType,
            AppointmentSource.SelfService);

    private static Appointment Book(AppointmentBooking? booking = null, SchedulingParameters? parameters = null) =>
        Appointment.Book(booking ?? Booking(), parameters ?? Parameters(), Now, Recorded);

    private static BookingRefusal RefusalFrom(Action act) =>
        Assert.Throws<BookingRuleViolationException>(act).Reason;

    // --- 4.1 The two transitions from Scheduled --------------------------------------

    [Fact]
    public void Cancelling_moves_to_Cancelled_and_leaves_the_row_intact()
    {
        var appointment = Book();
        var range = appointment.Range;

        appointment.Cancel(Cutoff(), Now, cutoffApplies: true);

        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.False(appointment.IsLive);

        // I10 in the only form an appointment has: the row is not deleted and the range it held
        // is still the range it held. "When was my 09:00?" stays answerable.
        Assert.Equal(range, appointment.Range);
        Assert.Null(appointment.RescheduledFromId);
    }

    [Fact]
    public void Rescheduling_terminates_the_original_and_returns_a_linked_replacement()
    {
        var original = Book();
        var originalRange = original.Range;
        var newStart = NextWeek + Duration.FromHours(3);

        var replacement = original.RescheduleTo(
            Booking(newStart), Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true);

        Assert.Equal(AppointmentStatus.Rescheduled, original.Status);
        Assert.Equal(originalRange, original.Range);

        Assert.Equal(AppointmentStatus.Scheduled, replacement.Status);
        Assert.True(replacement.IsLive);
        Assert.Equal(newStart, replacement.StartsAt);
        Assert.Equal(original.Id, replacement.RescheduledFromId);

        // Two appointments, not one moved. The distinction is what the audit trail is made of.
        Assert.NotEqual(original.Id, replacement.Id);
    }

    [Fact]
    public void A_reschedule_keeps_the_patient_professional_and_type()
    {
        var original = Book();

        var replacement = original.RescheduleTo(
            Booking(NextWeek + Duration.FromHours(3)), Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true);

        Assert.Equal(original.PatientId, replacement.PatientId);
        Assert.Equal(original.ProfessionalId, replacement.ProfessionalId);
        Assert.Equal(original.AppointmentTypeId, replacement.AppointmentTypeId);
    }

    [Theory]
    [InlineData("patient")]
    [InlineData("professional")]
    [InlineData("appointment type")]
    public void A_reschedule_cannot_change_who_or_what_the_appointment_is_for(string field)
    {
        // Not a BookingRefusal, because the wire contract carries an instant and nothing else —
        // no request can express this, so there is no message a patient could act on (design C3).
        var original = Book();
        var other = Guid.NewGuid();

        var replacement = field switch
        {
            "patient" => Booking(NextWeek + Duration.FromHours(3), patientId: other),
            "professional" => Booking(NextWeek + Duration.FromHours(3), professionalId: other),
            _ => Booking(NextWeek + Duration.FromHours(3), appointmentTypeId: other),
        };

        Assert.Throws<DomainRuleViolationException>(() =>
            original.RescheduleTo(replacement, Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true));

        // And the original is untouched — a refusal must not half-apply.
        Assert.Equal(AppointmentStatus.Scheduled, original.Status);
    }

    // --- 4.2 Refused from every terminal state ---------------------------------------

    [Fact]
    public void An_appointment_cannot_be_cancelled_twice()
    {
        var appointment = Book();
        appointment.Cancel(Cutoff(), Now, cutoffApplies: true);

        Assert.Equal(
            BookingRefusal.AppointmentNotChangeable,
            RefusalFrom(() => appointment.Cancel(Cutoff(), Now, cutoffApplies: true)));

        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
    }

    [Fact]
    public void A_rescheduled_appointment_cannot_then_be_cancelled()
    {
        var original = Book();
        original.RescheduleTo(
            Booking(NextWeek + Duration.FromHours(3)), Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true);

        // The patient's live appointment is the replacement; cancelling the husk would free
        // nothing and would misreport what happened to it.
        Assert.Equal(
            BookingRefusal.AppointmentNotChangeable,
            RefusalFrom(() => original.Cancel(Cutoff(), Now, cutoffApplies: true)));

        Assert.Equal(AppointmentStatus.Rescheduled, original.Status);
    }

    [Fact]
    public void A_cancelled_appointment_cannot_be_rescheduled_and_spawns_nothing()
    {
        var appointment = Book();
        appointment.Cancel(Cutoff(), Now, cutoffApplies: true);

        Assert.Equal(
            BookingRefusal.AppointmentNotChangeable,
            RefusalFrom(() => appointment.RescheduleTo(
                Booking(NextWeek + Duration.FromHours(3)), Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true)));
    }

    [Fact]
    public void Neither_transition_is_reachable_from_any_terminal_state()
    {
        // Completed and NoShow have no producer in this change, so they are reached the only way
        // available — through the state machine's own values — to prove the guard is about
        // liveness rather than about the two states this change happens to write.
        foreach (var terminal in new[]
                 {
                     AppointmentStatus.Completed,
                     AppointmentStatus.NoShow,
                     AppointmentStatus.Cancelled,
                     AppointmentStatus.Rescheduled,
                 })
        {
            var appointment = Book();
            Force(appointment, terminal);

            Assert.False(appointment.IsLive);

            Assert.Equal(
                BookingRefusal.AppointmentNotChangeable,
                RefusalFrom(() => appointment.Cancel(Cutoff(), Now, cutoffApplies: true)));

            Assert.Equal(
                BookingRefusal.AppointmentNotChangeable,
                RefusalFrom(() => appointment.RescheduleTo(
                    Booking(NextWeek + Duration.FromHours(3)), Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true)));
        }
    }

    // --- 4.3 / 4.4 The cutoff, in both directions of its authority -------------------

    [Fact]
    public void Inside_the_cutoff_a_bound_caller_is_refused()
    {
        var soon = Now + Duration.FromHours(5);
        var appointment = Book(Booking(soon));

        Assert.Equal(
            BookingRefusal.CutoffPassed,
            RefusalFrom(() => appointment.Cancel(Cutoff(24), Now, cutoffApplies: true)));

        Assert.Equal(
            BookingRefusal.CutoffPassed,
            RefusalFrom(() => appointment.RescheduleTo(
                Booking(NextWeek), Parameters(), Cutoff(24), Now, Recorded, cutoffApplies: true)));

        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
    }

    [Fact]
    public void Outside_the_cutoff_a_bound_caller_is_admitted()
    {
        var appointment = Book(Booking(Now + Duration.FromHours(25)));

        appointment.Cancel(Cutoff(24), Now, cutoffApplies: true);

        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
    }

    [Fact]
    public void Inside_the_cutoff_a_caller_it_does_not_apply_to_is_admitted()
    {
        // THE SPECIFICATION OF booking-desk's FRONT-DESK OVERRIDE. Nothing passes `false` today —
        // this test is what says what happens when 5c does, and it is written now so that the
        // signature 5c inherits was designed rather than discovered (design C4).
        var soon = Now + Duration.FromHours(5);

        var cancelling = Book(Booking(soon));
        cancelling.Cancel(Cutoff(24), Now, cutoffApplies: false);
        Assert.Equal(AppointmentStatus.Cancelled, cancelling.Status);

        var rescheduling = Book(Booking(soon));
        var replacement = rescheduling.RescheduleTo(
            Booking(NextWeek), Parameters(), Cutoff(24), Now, Recorded, cutoffApplies: false);

        Assert.Equal(AppointmentStatus.Rescheduled, rescheduling.Status);
        Assert.Equal(AppointmentStatus.Scheduled, replacement.Status);
    }

    [Fact]
    public void An_appointment_exactly_the_cutoff_away_is_still_changeable()
    {
        // A cutoff is a MINIMUM NOTICE. Refusing at the boundary would quietly turn "24 hours'
        // notice" into "more than 24 hours' notice" — the off-by-one nobody notices until a
        // patient is refused at 09:00 for tomorrow's 09:00.
        var appointment = Book(Booking(Now + Duration.FromHours(24)));

        appointment.Cancel(Cutoff(24), Now, cutoffApplies: true);

        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
    }

    [Fact]
    public void One_minute_inside_the_boundary_is_refused()
    {
        var appointment = Book(Booking(Now + Duration.FromHours(24) - Duration.FromMinutes(1)));

        Assert.Equal(
            BookingRefusal.CutoffPassed,
            RefusalFrom(() => appointment.Cancel(Cutoff(24), Now, cutoffApplies: true)));
    }

    [Fact]
    public void Terminal_is_reported_ahead_of_the_cutoff()
    {
        // An appointment both cancelled AND inside the cutoff is told what is actually true,
        // rather than being told about a deadline for a change that already happened.
        var appointment = Book(Booking(Now + Duration.FromHours(30)));
        appointment.Cancel(Cutoff(24), Now, cutoffApplies: true);

        var later = Now + Duration.FromHours(20);

        Assert.Equal(
            BookingRefusal.AppointmentNotChangeable,
            RefusalFrom(() => appointment.Cancel(Cutoff(24), later, cutoffApplies: true)));
    }

    [Fact]
    public void A_cutoff_of_nothing_is_a_typo_rather_than_a_lenient_clinic()
    {
        Assert.Throws<DomainRuleViolationException>(() => CancellationCutoffPolicy.Of(0));
        Assert.Throws<DomainRuleViolationException>(() => CancellationCutoffPolicy.Of(-1));
    }

    // --- 4.5 The replacement is checked against the NEW time -------------------------

    [Fact]
    public void The_replacement_respects_the_lead_time_from_the_reschedules_now()
    {
        var appointment = Book();

        Assert.Equal(
            BookingRefusal.LeadTimeViolation,
            RefusalFrom(() => appointment.RescheduleTo(
                Booking(Now + Duration.FromMinutes(30)), Parameters(leadMinutes: 60), Cutoff(), Now, Recorded, cutoffApplies: true)));

        // Refused means refused: the original must not have been consumed on the way.
        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
    }

    [Fact]
    public void The_replacement_respects_the_horizon()
    {
        var appointment = Book();

        Assert.Equal(
            BookingRefusal.HorizonExceeded,
            RefusalFrom(() => appointment.RescheduleTo(
                Booking(Now + Duration.FromDays(90)), Parameters(horizonDays: 60), Cutoff(), Now, Recorded, cutoffApplies: true)));

        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
    }

    [Fact]
    public void The_replacement_requires_the_professional_to_still_be_qualified()
    {
        // I2 rechecked against the reschedule rather than inherited from the original booking:
        // a qualification cleared in between must stop the move, not ride along with it.
        var appointment = Book();

        Assert.Equal(
            BookingRefusal.SpecialtyMismatch,
            RefusalFrom(() => appointment.RescheduleTo(
                Booking(NextWeek + Duration.FromHours(3), qualified: false), Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true)));

        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
    }

    [Fact]
    public void The_replacement_requires_a_room_of_the_required_type()
    {
        var appointment = Book();

        Assert.Throws<DomainRuleViolationException>(() => appointment.RescheduleTo(
            Booking(NextWeek + Duration.FromHours(3), resourceTypeId: UltrasoundRoomType),
            Parameters(),
            Cutoff(),
            Now,
            Recorded,
            cutoffApplies: true));

        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
    }

    // --- 4.6 The duration is re-baked, not inherited ---------------------------------

    [Fact]
    public void The_replacement_bakes_the_duration_in_force_now()
    {
        // I1 in the one situation that tells the two readings apart. A moved RANGE would have
        // carried the old 40 minutes; a new appointment takes the 55 in force at the reschedule,
        // and the original still says 40 forever (design C5).
        var original = Book(Booking(durationMinutes: 40));

        Assert.Equal(Duration.FromMinutes(40), original.Range.Length);

        var replacement = original.RescheduleTo(
            Booking(NextWeek + Duration.FromHours(3), durationMinutes: 55),
            Parameters(),
            Cutoff(),
            Now,
            Recorded,
            cutoffApplies: true);

        Assert.Equal(Duration.FromMinutes(55), replacement.Range.Length);
        Assert.Equal(Duration.FromMinutes(40), original.Range.Length);
    }

    // --- 4.7 The chain ----------------------------------------------------------------

    [Fact]
    public void A_replacement_can_itself_be_replaced_and_the_chain_does_not_collapse()
    {
        var first = Book();

        var second = first.RescheduleTo(
            Booking(NextWeek + Duration.FromHours(2)), Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true);

        var third = second.RescheduleTo(
            Booking(NextWeek + Duration.FromHours(5)), Parameters(), Cutoff(), Now, Recorded, cutoffApplies: true);

        // Each link names the appointment it DIRECTLY replaced, so the history reads in the order
        // it happened rather than as two rows both pointing at the original.
        Assert.Null(first.RescheduledFromId);
        Assert.Equal(first.Id, second.RescheduledFromId);
        Assert.Equal(second.Id, third.RescheduledFromId);

        Assert.Equal(AppointmentStatus.Rescheduled, first.Status);
        Assert.Equal(AppointmentStatus.Rescheduled, second.Status);
        Assert.Equal(AppointmentStatus.Scheduled, third.Status);

        // Exactly one of the three holds time, which is what the exclusion constraints will see.
        Assert.Single(Appointment.BusyIntervalsOf([first, second, third]));
    }

    /// <summary>
    /// Puts an appointment into a state this change has no producer for.
    /// </summary>
    /// <remarks>
    /// Reflection, and only in a test, and only for <c>Completed</c> / <c>NoShow</c> — which have
    /// no transition by design (C1). The alternative is leaving the liveness guard asserted for
    /// two states out of four, and then <c>booking-desk</c> discovers whether it also holds for
    /// the other two.
    /// </remarks>
    private static void Force(Appointment appointment, AppointmentStatus status) =>
        typeof(Appointment)
            .GetProperty(nameof(Appointment.Status))!
            .SetValue(appointment, status);
}
