using System.Net;
using System.Text.Json;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The staff desk — booking on behalf, the cutoff override, the day read, and the access trail
/// (spec: booking, identity-session; design N1-N9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three assertions here carry the change</b>, and each has a failure mode that looks like
/// success:
/// </para>
/// <para>
/// <b>1. <see cref="The_front_desk_cancels_inside_the_cutoff_after_the_patient_was_refused"/></b> —
/// the override, asserted as the pair rather than as one call. Reception succeeding proves nothing
/// on its own; the patient being refused on the same appointment, moments earlier, is what makes it
/// an override rather than a rule that was never in force.
/// </para>
/// <para>
/// <b>2. <see cref="The_lead_time_is_not_overridden_for_the_front_desk"/></b> — design N1 asserted
/// rather than argued. The cutoff and the lead time are different rules and only one of them takes
/// an authority. A desk that could book past the lead time would be a desk booking what its own
/// availability view says is unavailable.
/// </para>
/// <para>
/// <b>3. <see cref="Reading_a_day_records_one_access_per_distinct_patient"/></b>, with its negative
/// half. A missing <c>AccessLog</c> row breaks an LGPD claim while every screen still works, so it
/// is the one thing in this change that cannot be caught by using the product.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class BookingDeskTests(ApiFixture fixture)
{
    private ClinicBuilder Clinic => new(fixture);

    // --- Booking on behalf (design N3) -----------------------------------------------

    [Theory]
    [InlineData(Role.FrontDesk)]
    [InlineData(Role.Administrator)]
    public async Task Staff_book_for_a_named_patient_and_are_told_the_room(Role role)
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(role);
        using var _staff = staff;

        var patientId = await Clinic.PatientIdAsync(patientUser.Id);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var response = await staff.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = clinic.ProfessionalId,
            startsAt = ClinicBuilder.Utc(start),
            patientId,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ClinicBuilder.Body(response);

        // The room reception has to send the patient to — the one ASSIGNED, read back from the
        // created appointment rather than the candidate a slot named (design N5).
        Assert.Equal(clinic.RoomId, body.GetProperty("resourceId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("resourceName").GetString()));
        Assert.Equal(patientId, body.GetProperty("patientId").GetGuid());

        var stored = await AppointmentAsync(body.GetProperty("id").GetGuid());

        Assert.Equal(patientId, stored.PatientId);

        // The first write of a value 5a shipped and left unused.
        Assert.Equal(AppointmentSource.FrontDesk, stored.Source);
    }

    [Fact]
    public async Task A_patient_booking_for_themselves_is_not_told_the_room()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var response = await patient.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = clinic.ProfessionalId,
            startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9)),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ClinicBuilder.Body(response);

        // D7 held at the level it was written about: the two responses are different shapes, so
        // the rule is in the type rather than in a branch somebody could forget.
        Assert.False(body.TryGetProperty("resourceName", out _));
        Assert.False(body.TryGetProperty("resourceId", out _));

        Assert.Equal(AppointmentSource.SelfService, (await AppointmentAsync(body.GetProperty("id").GetGuid())).Source);
    }

    [Fact]
    public async Task A_patient_naming_a_patient_is_refused_even_when_it_is_their_own_id()
    {
        // The field is refused BY ROLE rather than validated by value (design N3), so there is no
        // path on which a patient's request body influences whose appointment this is. Silently
        // substituting the session's patient would create a real appointment for the wrong person
        // and report success — nobody would find out until somebody arrived at the clinic.
        var clinic = await Clinic.BuildAsync();
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var own = await Clinic.PatientIdAsync(user.Id);

        foreach (var named in new[] { own, Guid.NewGuid() })
        {
            var refused = await patient.PostAsync("/api/appointments", new
            {
                appointmentTypeId = clinic.AppointmentTypeId,
                professionalId = clinic.ProfessionalId,
                startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9)),
                patientId = named,
            });

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("auth.forbidden", await ClinicBuilder.CodeOf(refused));
        }

        Assert.Equal(0, await LiveAppointmentCountAsync(clinic));
    }

    [Fact]
    public async Task Staff_naming_a_patient_that_does_not_exist_are_told_so()
    {
        var clinic = await Clinic.BuildAsync();
        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var refused = await staff.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = clinic.ProfessionalId,
            startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9)),
            patientId = Guid.NewGuid(),
        });

        // Staff are entitled to distinguish absence from denial, so this is the plain 404 rather
        // than the ownership answer a patient would get.
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("patient.not_found", await ClinicBuilder.CodeOf(refused));
    }

    [Fact]
    public async Task What_availability_will_not_offer_the_front_desk_cannot_book_either()
    {
        // DESIGN N1, ASSERTED. The desk's authority is over the CUTOFF — whether an appointment
        // that already exists may be undone. It confers nothing over what may be OFFERED, which is
        // governed by the numbers the read and the write share so that availability cannot offer
        // what booking refuses. A clinic that genuinely takes zero-notice walk-ins configures
        // `Scheduling__MinimumLeadTimeMinutes=0`, which the domain calls legitimate.
        //
        // Asserted as "the desk gets the same answer a patient does" rather than by naming one
        // code, because WHICH near-now rule fires first — the lead time or the working hours — is
        // the solver's walk order and depends on the clinic's configured hours. That is not what
        // this test is about; what it is about is that neither is relaxed for reception.
        var clinic = await Clinic.BuildAsync();
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var patientId = await Clinic.PatientIdAsync(patientUser.Id);
        var tooSoon = ClinicBuilder.Utc(SystemClock.Instance.GetCurrentInstant() + Duration.FromMinutes(5));

        var forStaff = await staff.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = clinic.ProfessionalId,
            startsAt = tooSoon,
            patientId,
        });

        var forPatient = await patient.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = clinic.ProfessionalId,
            startsAt = tooSoon,
        });

        var code = await ClinicBuilder.CodeOf(forStaff);

        Assert.Equal(forPatient.StatusCode, forStaff.StatusCode);
        Assert.Equal(await ClinicBuilder.CodeOf(forPatient), code);
        Assert.False(forStaff.IsSuccessStatusCode);

        // And specifically NOT the cutoff, which is the rule the desk does override. The cutoff
        // has never governed the creation of an appointment (design N1).
        Assert.NotEqual("booking.cutoff_passed", code);
        Assert.Equal(0, await LiveAppointmentCountAsync(clinic));
    }

    [Fact]
    public async Task The_consent_gate_binds_a_staff_booking_and_reads_the_patients_consent()
    {
        // Not relaxed for reception, and that is the decision rather than an oversight: exempting
        // the desk would let the clinic route around a patient's own withdrawal by telephone.
        var clinic = await Clinic.BuildAsync();
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            $"/api/patients/me/consents/{nameof(ConsentType.DataProcessing)}/revoke", new { }));

        var refused = await staff.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = clinic.ProfessionalId,
            startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9)),
            patientId = await Clinic.PatientIdAsync(patientUser.Id),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("auth.consent_required", await ClinicBuilder.CodeOf(refused));
    }

    // --- The override (design N1) ----------------------------------------------------

    [Fact]
    public async Task The_front_desk_cancels_inside_the_cutoff_after_the_patient_was_refused()
    {
        // THE CHANGE'S HEADLINE, and asserted as the pair. Reception succeeding proves nothing on
        // its own — the patient being refused on the SAME appointment is what makes this an
        // override rather than a rule that was never in force.
        var clinic = await Clinic.BuildAsync();
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var id = await SoonAppointmentAsync(clinic, await Clinic.PatientIdAsync(patientUser.Id));

        var refusedForPatient = await patient.PostAsync($"/api/appointments/{id}/cancel", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedForPatient.StatusCode);
        Assert.Equal("booking.cutoff_passed", await ClinicBuilder.CodeOf(refusedForPatient));
        Assert.Equal(AppointmentStatus.Scheduled, (await AppointmentAsync(id)).Status);

        var allowed = await staff.PostAsync($"/api/appointments/{id}/cancel", new { });

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(AppointmentStatus.Cancelled, (await AppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task The_front_desk_reschedules_inside_the_cutoff()
    {
        // The override on the other transition. The cutoff is evaluated against the appointment
        // BEING MOVED, so the target is an ordinary offerable slot — a near-now target would be
        // refused for a different reason entirely and prove nothing about the cutoff.
        var clinic = await Clinic.BuildAsync();
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var id = await SoonAppointmentAsync(clinic, await Clinic.PatientIdAsync(patientUser.Id));
        var original = await AppointmentAsync(id);
        var target = new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9)) };

        var refusedForPatient = await patient.PostAsync($"/api/appointments/{id}/reschedule", target);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedForPatient.StatusCode);
        Assert.Equal("booking.cutoff_passed", await ClinicBuilder.CodeOf(refusedForPatient));

        var moved = await staff.PostAsync($"/api/appointments/{id}/reschedule", target);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var replacement = await AppointmentAsync((await ClinicBuilder.Body(moved)).GetProperty("id").GetGuid());

        Assert.Equal(AppointmentStatus.Rescheduled, (await AppointmentAsync(id)).Status);
        Assert.Equal(AppointmentStatus.Scheduled, replacement.Status);

        // A reschedule does not change the professional, the appointment type or the patient — the
        // request carries none of them, so this is structural. Asserted rather than assumed.
        Assert.Equal(original.ProfessionalId, replacement.ProfessionalId);
        Assert.Equal(original.AppointmentTypeId, replacement.AppointmentTypeId);
        Assert.Equal(original.PatientId, replacement.PatientId);
    }

    [Fact]
    public async Task A_staff_reschedule_by_a_few_minutes_succeeds()
    {
        // THE NEAR MOVE, on the staff path. It proves two things at once: the appointment being
        // moved is excluded from its own busy set, and the UPDATE-before-INSERT statement ordering
        // still holds for this caller. A distant move passes either way, which is exactly why the
        // few-minute delta is the assertion — and why staff share this handler rather than getting
        // a second implementation of it to get subtly wrong (design N2).
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        var moved = await staff.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9, 15)) });

        await ClinicBuilder.Succeeds(Task.FromResult(moved));

        Assert.Equal(AppointmentStatus.Rescheduled, (await AppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task A_reschedule_carries_the_original_source_onto_its_replacement()
    {
        // "The reschedule preserved where it came from" is a real question with two possible
        // answers now that a second source can be written. One booked at the desk that reception
        // then moves is still an appointment the clinic made.
        var clinic = await Clinic.BuildAsync();
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var patientId = await Clinic.PatientIdAsync(patientUser.Id);

        var booked = await staff.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = clinic.ProfessionalId,
            startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9)),
            patientId,
        });

        var id = (await ClinicBuilder.Body(booked)).GetProperty("id").GetGuid();

        var moved = await staff.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 10)) });

        await ClinicBuilder.Succeeds(Task.FromResult(moved));

        var replacement = await AppointmentAsync((await ClinicBuilder.Body(moved)).GetProperty("id").GetGuid());

        Assert.Equal(AppointmentSource.FrontDesk, replacement.Source);
    }

    [Fact]
    public async Task Staff_are_told_when_an_appointment_does_not_exist_and_a_patient_is_not()
    {
        // The two answers the same route gives to its two callers. A patient cannot tell absence
        // from denial, so the endpoint cannot be used to enumerate appointment ids; staff can,
        // because there is no appointment reception may not reach.
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var unknown = Guid.NewGuid();

        var forStaff = await staff.PostAsync($"/api/appointments/{unknown}/cancel", new { });
        var forPatient = await patient.PostAsync($"/api/appointments/{unknown}/cancel", new { });

        Assert.Equal(HttpStatusCode.NotFound, forStaff.StatusCode);
        Assert.Equal("booking.appointment_not_found", await ClinicBuilder.CodeOf(forStaff));

        Assert.Equal(HttpStatusCode.Forbidden, forPatient.StatusCode);
        Assert.Equal("auth.ownership_denied", await ClinicBuilder.CodeOf(forPatient));
    }

    [Fact]
    public async Task The_front_desk_changes_an_appointment_belonging_to_any_patient()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        await ClinicBuilder.Succeeds(staff.PostAsync($"/api/appointments/{id}/cancel", new { }));

        Assert.Equal(AppointmentStatus.Cancelled, (await AppointmentAsync(id)).Status);
    }

    // --- The day read (design N9) ----------------------------------------------------

    [Fact]
    public async Task Reception_sees_the_day_across_professionals_with_the_room_and_the_patient()
    {
        var clinic = await Clinic.BuildAsync(professionals: 2, rooms: 2);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (other, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _other = other;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9), clinic.Professionals[0]);
        await BookAsync(other, clinic, ClinicBuilder.At(clinic.Date, 10), clinic.Professionals[1]);

        var body = await DayAsync(staff, clinic.Date);

        // Scoped to THIS clinic's two professionals rather than asserted as a total. The day read
        // is deliberately clinic-wide — that is what S4 is — so a count would be an assertion about
        // whatever else the suite has booked on the same date.
        var mine = OursOnly(body, clinic);

        Assert.Equal(2, mine.Count);
        Assert.Equal(2, mine.Select(a => a.GetProperty("professionalId").GetGuid()).Distinct().Count());

        foreach (var appointment in mine)
        {
            Assert.False(string.IsNullOrWhiteSpace(appointment.GetProperty("patientName").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(appointment.GetProperty("resourceName").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(appointment.GetProperty("professionalName").GetString()));
        }

        Assert.Equal(ApiFixture.ClinicTimezoneId, body.GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task Reception_narrows_the_day_to_one_professional()
    {
        var clinic = await Clinic.BuildAsync(professionals: 2, rooms: 2);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (other, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _other = other;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9), clinic.Professionals[0]);
        await BookAsync(other, clinic, ClinicBuilder.At(clinic.Date, 10), clinic.Professionals[1]);

        var body = await DayAsync(staff, clinic.Date, clinic.Professionals[0]);

        var appointment = Assert.Single(body.GetProperty("appointments").EnumerateArray());

        Assert.Equal(clinic.Professionals[0], appointment.GetProperty("professionalId").GetGuid());
    }

    [Fact]
    public async Task A_professional_naming_another_professional_still_gets_their_own_day()
    {
        // THE SCOPE, and it is structural rather than filtered (design N9): the parameter is
        // disregarded, not refused. A refusal would also be a worse answer — it would confirm that
        // the named professional exists.
        var clinic = await Clinic.BuildAsync(professionals: 2, rooms: 2);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (other, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _other = other;

        await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9), clinic.Professionals[0]);
        await BookAsync(other, clinic, ClinicBuilder.At(clinic.Date, 10), clinic.Professionals[1]);

        using var first = await fixture.AsUserAsync(clinic.ProfessionalUsers[0]);

        var body = await DayAsync(first, clinic.Date, clinic.Professionals[1]);

        var appointment = Assert.Single(body.GetProperty("appointments").EnumerateArray());

        Assert.Equal(clinic.Professionals[0], appointment.GetProperty("professionalId").GetGuid());
    }

    [Fact]
    public async Task The_day_shows_blocks_beside_appointments_and_hides_terminal_ones()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var kept = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));
        var cancelled = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 10));

        await ClinicBuilder.Succeeds(patient.PostAsync($"/api/appointments/{cancelled}/cancel", new { }));

        using var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);

        await ClinicBuilder.Succeeds(professional.PostAsync("/api/blocks", new
        {
            startsAt = ClinicBuilder.Wall(clinic.Date, 11),
            endsAt = ClinicBuilder.Wall(clinic.Date, 12),
        }));

        var body = await DayAsync(staff, clinic.Date);

        // A cancelled appointment is not part of the day being run: the availability computation
        // already treats it as free time, and showing it would make the day read as busier.
        var appointment = Assert.Single(OursOnly(body, clinic));

        Assert.Equal(kept, appointment.GetProperty("id").GetGuid());

        // A day with a gap and a day with a declared block look identical otherwise, and a
        // receptionist deciding whether to offer 11:00 needs to know which one they are seeing.
        Assert.Single(
            body.GetProperty("blocks").EnumerateArray().ToList(),
            block => block.GetProperty("professionalId").GetGuid() == clinic.ProfessionalId);
    }

    [Fact]
    public async Task The_day_says_whether_the_patient_may_still_change_each_appointment()
    {
        // The sentence S4 exists to say: "the patient can no longer change this, and you can."
        // Computed by the server for 5b's C10 reason — a browser's clock is not the clinic's.
        var clinic = await Clinic.BuildAsync();
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var patientId = await Clinic.PatientIdAsync(patientUser.Id);
        var soon = await SoonAppointmentAsync(clinic, patientId);
        var far = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        // The day the SOON appointment actually falls on, not "today". Three hours from now is
        // tomorrow once it is past 21:00 in the clinic's zone, and asking for today then returns
        // a day the appointment is not on — a failure that only appears in the evening. Found
        // during `calendar-connection`, at 22:33.
        var soonDay = (await AppointmentAsync(soon)).StartsAt.InZone(ClinicBuilder.Clinic).Date;

        var soonBody = await DayAsync(staff, soonDay);
        var farBody = await DayAsync(staff, clinic.Date);

        Assert.False(Find(soonBody, soon).GetProperty("patientCanChange").GetBoolean());
        Assert.True(Find(farBody, far).GetProperty("patientCanChange").GetBoolean());

        // And reception can act on the one the patient cannot, which is the other half of it.
        await ClinicBuilder.Succeeds(staff.PostAsync($"/api/appointments/{soon}/cancel", new { }));
    }

    [Theory]
    [InlineData(Role.Patient)]
    public async Task The_day_is_refused_to_a_patient(Role role)
    {
        var (caller, _) = await fixture.AsRoleAsync(role);
        using var _caller = caller;

        var response = await caller.GetAsync(
            $"/api/schedule?date={ClinicBuilder.Iso(ClinicBuilder.TargetDate())}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_day_is_refused_to_an_anonymous_caller()
    {
        using var anonymous = fixture.CreateAnonymousClient();

        var response = await anonymous.GetAsync(
            $"/api/schedule?date={ClinicBuilder.Iso(ClinicBuilder.TargetDate())}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.session_expired", await ClinicBuilder.CodeOf(response));
    }

    // --- The access trail (design N7) ------------------------------------------------

    [Fact]
    public async Task Reading_a_day_records_one_access_per_distinct_patient()
    {
        // THE CHECK WHOSE FAILURE IS SILENT. Everything on screen works without these rows, and
        // an LGPD claim in 02-domain-model.md §8 is quietly false.
        var clinic = await Clinic.BuildAsync(rooms: 2);
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, staffUser) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var patientId = await Clinic.PatientIdAsync(patientUser.Id);

        // Two appointments, ONE patient — so "one row per disclosure" is distinguishable from
        // "one row per appointment", which is the mistake that inflates the trail into noise.
        await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));
        await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 10));

        var before = await AccessCountAsync(staffUser.Id, patientId);

        await DayAsync(staff, clinic.Date);

        Assert.Equal(before + 1, await AccessCountAsync(staffUser.Id, patientId));
    }

    [Fact]
    public async Task An_empty_day_records_nothing_and_neither_does_acting_on_an_appointment()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, staffUser) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var patientId = await Clinic.PatientIdAsync(patientUser.Id);
        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        // A day with none of this patient's appointments discloses nothing about them.
        await DayAsync(staff, clinic.Date.PlusDays(1));

        Assert.Equal(0, await AccessCountAsync(staffUser.Id, patientId));

        var afterRead = await ReadDayAndCountAsync(staff, staffUser.Id, patientId, clinic);

        await ClinicBuilder.Succeeds(staff.PostAsync($"/api/appointments/{id}/cancel", new { }));

        // Cancelling adds NO further row (design N7). An appointment is not the patient's personal
        // data, and the name reception read before acting is already recorded — logging the action
        // too would double-count one disclosure.
        Assert.Equal(afterRead, await AccessCountAsync(staffUser.Id, patientId));
    }

    [Fact]
    public async Task A_professional_reading_their_own_day_is_recorded()
    {
        // Recorded, not exempt: it is somebody else's personal data, and that a clinician is
        // entitled to see it is the reason for the record rather than an exemption from one.
        var clinic = await Clinic.BuildAsync();
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var patientId = await Clinic.PatientIdAsync(patientUser.Id);

        await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        using var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);

        await DayAsync(professional, clinic.Date);

        Assert.Equal(1, await AccessCountAsync(clinic.ProfessionalUser.Id, patientId));
    }

    // --- Resolving a patient (design N8) ---------------------------------------------

    [Fact]
    public async Task Reception_resolves_a_patient_by_their_exact_email_and_it_is_recorded()
    {
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, staffUser) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var patientId = await Clinic.PatientIdAsync(patientUser.Id);

        var body = await ClinicBuilder.Body(
            await staff.GetAsync($"/api/patients/by-email?email={Uri.EscapeDataString(patientUser.Email)}"));

        Assert.Equal(patientId, body.GetProperty("patientId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("fullName").GetString()));

        // Surfaced before the receptionist takes a walk-in's time, from the same query the booking
        // gate runs — so what this reports and what booking enforces cannot disagree.
        Assert.True(body.GetProperty("hasDataProcessingConsent").GetBoolean());

        Assert.Equal(1, await AccessCountAsync(staffUser.Id, patientId));
    }

    [Fact]
    public async Task A_partial_address_resolves_nobody_and_records_nothing()
    {
        // Exact, not a search (design N8). A name or prefix search over patients is an enumeration
        // surface, and logging every result would bury the entries that matter.
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, staffUser) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        var patientId = await Clinic.PatientIdAsync(patientUser.Id);
        var partial = patientUser.Email[..(patientUser.Email.IndexOf('@') - 1)];

        var response = await staff.GetAsync($"/api/patients/by-email?email={Uri.EscapeDataString(partial)}");

        // Half an address is a typing mistake rather than an absent patient, and saying so sends
        // the receptionist to the right problem. Either way no patient is returned and — the part
        // that matters — nothing is recorded.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.invalid_format", await ClinicBuilder.CodeOf(response));
        Assert.Equal(0, await AccessCountAsync(staffUser.Id, patientId));

        // And a whole address belonging to nobody is the plain 404, still recording nothing.
        var unknown = await staff.GetAsync("/api/patients/by-email?email=nobody-here@clinic.test");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("patient.not_found", await ClinicBuilder.CodeOf(unknown));
        Assert.Equal(0, await AccessCountAsync(staffUser.Id, patientId));
    }

    [Fact]
    public async Task A_patient_whose_consent_is_not_in_force_is_findable_and_flagged()
    {
        var (patient, patientUser) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (staff, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _staff = staff;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            $"/api/patients/me/consents/{nameof(ConsentType.DataProcessing)}/revoke", new { }));

        var body = await ClinicBuilder.Body(
            await staff.GetAsync($"/api/patients/by-email?email={Uri.EscapeDataString(patientUser.Email)}"));

        Assert.False(body.GetProperty("hasDataProcessingConsent").GetBoolean());
    }

    [Theory]
    [InlineData(Role.Patient)]
    [InlineData(Role.Professional)]
    public async Task Only_reception_looks_a_patient_up(Role role)
    {
        var (caller, _) = await fixture.AsRoleAsync(role);
        using var _caller = caller;

        var response = await caller.GetAsync("/api/patients/by-email?email=someone@example.test");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- The room on the wire, and the professional's name (design N5, N10) ----------

    [Fact]
    public async Task An_availability_slot_names_its_room_and_follows_a_rename()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var renamed = ClinicBuilder.Unique("Consulting");

        await ClinicBuilder.Succeeds(admin.PutAsync(
            $"/api/config/resources/{clinic.RoomId}",
            new { name = renamed, resourceTypeId = clinic.ResourceTypeId }));

        var body = await ClinicBuilder.Body(await patient.GetAsync(
            $"/api/availability?appointmentTypeId={clinic.AppointmentTypeId}" +
            $"&from={ClinicBuilder.Iso(clinic.Date)}&to={ClinicBuilder.Iso(clinic.Date)}"));

        var slot = body.GetProperty("slots").EnumerateArray().First();

        Assert.Equal(clinic.RoomId, slot.GetProperty("resourceId").GetGuid());
        Assert.Equal(renamed, slot.GetProperty("resourceName").GetString());
    }

    [Fact]
    public async Task A_professional_is_labelled_by_their_stored_name_and_falls_back_without_one()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        // Without a name the derived label stands — which is why the column may be null and why
        // the derivation was kept rather than deleted (design N10).
        var derived = LabelOf(await ClinicBuilder.Body(await patient.GetAsync("/api/booking/options")), clinic);

        Assert.False(string.IsNullOrWhiteSpace(derived));
        Assert.DoesNotContain('@', derived);

        var name = ClinicBuilder.Unique("Dra Helena");

        await ClinicBuilder.Succeeds(admin.PutAsync(
            $"/api/config/professionals/{clinic.ProfessionalUser.Id}/name", new { fullName = name }));

        Assert.Equal(name, LabelOf(await ClinicBuilder.Body(await patient.GetAsync("/api/booking/options")), clinic));

        // Clearing restores the fallback rather than leaving a blank where a name should be.
        await ClinicBuilder.Succeeds(admin.PutAsync(
            $"/api/config/professionals/{clinic.ProfessionalUser.Id}/name", new { fullName = "  " }));

        Assert.Equal(derived, LabelOf(await ClinicBuilder.Body(await patient.GetAsync("/api/booking/options")), clinic));
    }

    [Fact]
    public async Task Naming_a_professional_creates_the_configuration_record_they_lacked()
    {
        // Setting a name is a first save, so it goes through the same E1 seam every other write
        // does rather than being a special case added beside it.
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var invited = await fixture.SeedUserAsync(Role.Professional);

        var before = await ClinicBuilder.Body(await admin.GetAsync($"/api/config/professionals/{invited.Id}"));

        Assert.False(before.GetProperty("isConfigured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("fullName").ValueKind);

        await ClinicBuilder.Succeeds(admin.PutAsync(
            $"/api/config/professionals/{invited.Id}/name", new { fullName = "Dr Novo" }));

        var after = await ClinicBuilder.Body(await admin.GetAsync($"/api/config/professionals/{invited.Id}"));

        Assert.True(after.GetProperty("isConfigured").GetBoolean());
        Assert.Equal("Dr Novo", after.GetProperty("fullName").GetString());
    }

    // --- Helpers ---------------------------------------------------------------------

    private static string? LabelOf(JsonElement options, BookableClinic clinic) =>
        options.GetProperty("specialties").EnumerateArray()
            .SelectMany(specialty => specialty.GetProperty("appointmentTypes").EnumerateArray())
            .Where(type => type.GetProperty("appointmentTypeId").GetGuid() == clinic.AppointmentTypeId)
            .SelectMany(type => type.GetProperty("professionals").EnumerateArray())
            .Single(professional => professional.GetProperty("professionalId").GetGuid() == clinic.ProfessionalId)
            .GetProperty("displayName").GetString();

    /// <summary>
    /// The day, asserted to have been served rather than parsed out of whatever came back.
    /// </summary>
    private static async Task<JsonElement> DayAsync(TestClient client, LocalDate date, Guid? professionalId = null)
    {
        var url = $"/api/schedule?date={ClinicBuilder.Iso(date)}"
            + (professionalId is { } only ? $"&professionalId={only}" : string.Empty);

        var response = await client.GetAsync(url);

        await ClinicBuilder.Succeeds(Task.FromResult(response));

        return await ClinicBuilder.Body(response);
    }

    /// <summary>
    /// The rows belonging to this test's own clinic.
    /// </summary>
    /// <remarks>
    /// The day read is clinic-wide by design — that is what S4 is — and the suite books many
    /// clinics onto the same target date. So a total count would be an assertion about the rest of
    /// the suite. Narrowing by professional keeps the assertions about this test.
    /// </remarks>
    private static List<JsonElement> OursOnly(JsonElement day, BookableClinic clinic) =>
        day.GetProperty("appointments").EnumerateArray()
            .Where(appointment => clinic.Professionals.Contains(appointment.GetProperty("professionalId").GetGuid()))
            .ToList();

    private static JsonElement Find(JsonElement day, Guid appointmentId) =>
        day.GetProperty("appointments").EnumerateArray()
            .Single(appointment => appointment.GetProperty("id").GetGuid() == appointmentId);

    private async Task<int> ReadDayAndCountAsync(
        TestClient staff,
        Guid actorUserId,
        Guid patientId,
        BookableClinic clinic)
    {
        await DayAsync(staff, clinic.Date);

        return await AccessCountAsync(actorUserId, patientId);
    }

    private async Task<int> AccessCountAsync(Guid actorUserId, Guid patientId)
    {
        var count = 0;

        await fixture.WithDatabaseAsync(async database =>
            count = await database.AccessLog.CountAsync(
                entry => entry.ActorUserId == actorUserId && entry.PatientId == patientId));

        return count;
    }

    private static async Task<Guid> BookAsync(
        TestClient patient,
        BookableClinic clinic,
        Instant startsAt,
        Guid? professionalId = null)
    {
        var response = await patient.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = professionalId ?? clinic.ProfessionalId,
            startsAt = ClinicBuilder.Utc(startsAt),
        });

        await ClinicBuilder.Succeeds(Task.FromResult(response));

        return (await ClinicBuilder.Body(response)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// An appointment a few hours from now — inside the 24-hour cutoff, outside the lead time.
    /// </summary>
    /// <remarks>
    /// Written through the domain factory for the reason the lifecycle tests already record: a
    /// start three hours from now is almost never inside the fixture's working hours, and arranging
    /// for it to be would make the outcome depend on what time the suite happens to run at.
    /// </remarks>
    private async Task<Guid> SoonAppointmentAsync(BookableClinic clinic, Guid patientId)
    {
        var id = Guid.Empty;

        await fixture.WithDatabaseAsync(async database =>
        {
            var now = SystemClock.Instance.GetCurrentInstant();

            var appointment = Appointment.Book(
                new AppointmentBooking(
                    patientId,
                    clinic.ProfessionalId,
                    clinic.RoomId,
                    clinic.AppointmentTypeId,
                    now + Duration.FromHours(3),
                    clinic.DurationMinutes,
                    ProfessionalHoldsDurationForType: true,
                    clinic.ResourceTypeId,
                    clinic.ResourceTypeId,
                    AppointmentSource.SelfService),
                SchedulingParameters.Of(15, 60, 60),
                now,
                DateTimeOffset.UtcNow);

            database.Appointments.Add(appointment);

            await database.SaveChangesAsync();

            id = appointment.Id;
        });

        return id;
    }

    private async Task<Appointment> AppointmentAsync(Guid id)
    {
        Appointment? found = null;

        await fixture.WithDatabaseAsync(async database =>
            found = await database.Appointments.AsNoTracking().SingleAsync(a => a.Id == id));

        return found!;
    }

    private async Task<int> LiveAppointmentCountAsync(BookableClinic clinic)
    {
        var count = 0;

        await fixture.WithDatabaseAsync(async database =>
            count = await database.Appointments.CountAsync(
                appointment => appointment.AppointmentTypeId == clinic.AppointmentTypeId
                    && appointment.Status == AppointmentStatus.Scheduled));

        return count;
    }
}
