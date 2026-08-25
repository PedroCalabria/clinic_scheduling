using System.Net;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// Cancel and reschedule, end to end (spec: booking; design C2, C7, C8).
/// </summary>
/// <remarks>
/// <para>
/// Two assertions here justify the whole file, and both have failure modes that a reasonable test
/// suite misses completely:
/// </para>
/// <para>
/// <b>1. <see cref="A_reschedule_by_a_few_minutes_succeeds"/></b> — the near move, which proves
/// the busy-set filter: the appointment being moved must not count as an obstacle to its own
/// replacement. Only an overlapping move exercises it, so the few-minute delta is the assertion.
/// The <em>statement ordering</em> it was also meant to cover turned out to be invisible from
/// here — EF orders its own batch — so that rule is asserted against raw SQL in
/// <c>RescheduleOrderingTests</c> instead.
/// </para>
/// <para>
/// <b>2. <see cref="A_cancel_and_a_reschedule_racing_on_one_appointment_yield_one_outcome"/></b> —
/// the same-appointment race. Nothing in the schema prevents it: the three exclusion constraints
/// police overlap <em>between</em> rows, and this is about the lifecycle of <em>one</em>. Both
/// requests read a <c>Scheduled</c> row, both pass the aggregate's guard against that snapshot,
/// and the patient ends up cancelled and booked. Every single-threaded test passes.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AppointmentLifecycleTests(ApiFixture fixture)
{
    private ClinicBuilder Clinic => new(fixture);

    // --- Cancel, and the round trip the partial index was written for -----------------

    [Fact]
    public async Task A_patient_cancels_and_the_row_survives_with_its_range()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);
        var id = await BookAsync(patient, clinic, start);

        var response = await patient.PostAsync($"/api/appointments/{id}/cancel", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            nameof(AppointmentStatus.Cancelled),
            (await ClinicBuilder.Body(response)).GetProperty("status").GetString());

        var stored = await AppointmentAsync(id);

        // Not deleted, and its range untouched (I10). "When was my 09:00?" stays answerable, which
        // is the entire reason a cancellation is a status rather than a DELETE.
        Assert.Equal(AppointmentStatus.Cancelled, stored.Status);
        Assert.Equal(start, stored.StartsAt);
        Assert.Equal(await Clinic.PatientIdAsync(user.Id), stored.PatientId);
    }

    [Fact]
    public async Task A_cancelled_slot_and_its_neighbours_are_offered_again()
    {
        // THE round trip. booking-core wrote the exclusion predicate partial on the live state and
        // could only prove the freeing behaviour by writing a terminal row directly, bypassing the
        // handler. This is the same claim, through the product.
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var before = await OfferedStartsAsync(patient, clinic);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var id = await BookAsync(patient, clinic, start);

        var during = await OfferedStartsAsync(patient, clinic);

        Assert.DoesNotContain(ClinicBuilder.Utc(start), during);
        Assert.NotEqual(before.Count, during.Count);

        await ClinicBuilder.Succeeds(patient.PostAsync($"/api/appointments/{id}/cancel", new { }));

        var after = await OfferedStartsAsync(patient, clinic);

        // The slot itself, AND the overlapping neighbours the finer step had withheld. A
        // cancellation that returned only the exact start would mean the subtraction and the
        // release disagreed about what an appointment occupies.
        Assert.Equal(before, after);
        Assert.Contains(ClinicBuilder.Utc(start), after);
    }

    [Fact]
    public async Task A_cancellation_releases_the_room_for_another_professional()
    {
        // One room, two qualified professionals: the second one's availability is the only thing
        // that can show the ROOM being released rather than the diary.
        var clinic = await Clinic.BuildAsync(rooms: 1, professionals: 2);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);
        var id = await BookAsync(patient, clinic, start, clinic.Professionals[0]);

        var blocked = await OfferedStartsAsync(patient, clinic, clinic.Professionals[1]);

        Assert.DoesNotContain(ClinicBuilder.Utc(start), blocked);

        await ClinicBuilder.Succeeds(patient.PostAsync($"/api/appointments/{id}/cancel", new { }));

        var freed = await OfferedStartsAsync(patient, clinic, clinic.Professionals[1]);

        Assert.Contains(ClinicBuilder.Utc(start), freed);
    }

    [Fact]
    public async Task A_terminal_appointment_no_longer_blocks_an_internal_block()
    {
        // The I7 direction, against a state that is now reachable through the product. 5a proved
        // the predicate by writing a terminal row directly; this proves the path.
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);
        using var _professional = professional;

        var start = ClinicBuilder.At(clinic.Date, 9);
        var id = await BookAsync(patient, clinic, start);

        var refused = await professional.PostAsync("/api/blocks", BlockOver(clinic, 9, 10));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("booking.block_overlaps_appointment", await ClinicBuilder.CodeOf(refused));

        await ClinicBuilder.Succeeds(patient.PostAsync($"/api/appointments/{id}/cancel", new { }));

        await ClinicBuilder.Succeeds(professional.PostAsync("/api/blocks", BlockOver(clinic, 9, 10)));
    }

    // --- Reschedule: the near move, and why the delta is the assertion ----------------

    [Fact]
    public async Task A_reschedule_by_a_few_minutes_succeeds()
    {
        // ─────────────────────────────────────────────────────────────────────────────
        //  THE FIXTURE VALUE BELOW IS THE ASSERTION. DO NOT "TIDY" IT INTO NEXT WEEK.
        //
        //  09:00 → 09:15 with a 60-minute visit, so the old and new ranges OVERLAP. That
        //  overlap is the only thing that exercises the busy-set filter (design C7): if
        //  the appointment being moved is not excluded from the loaded busy set, the
        //  solver reports the patient's own outgoing appointment as an obstacle and this
        //  returns a refusal where a success belonged. A move to another day passes with
        //  that fault present — see the far-move test below, which exists so a failure
        //  here can be localised.
        //
        //  WHAT THIS TEST DOES *NOT* CATCH, contrary to what it was written believing:
        //  the statement ordering (design C2). Reversing the handler's two SaveChanges
        //  calls leaves every test in this file green, because EF Core orders its own
        //  command batch and happens to emit the UPDATE first regardless. That was found
        //  by reversing them deliberately, and it is why the ordering rule is asserted
        //  where it is genuinely true — against raw SQL, in RescheduleOrderingTests.
        // ─────────────────────────────────────────────────────────────────────────────
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);
        var nudged = ClinicBuilder.At(clinic.Date, 9, 15);

        var id = await BookAsync(patient, clinic, start);

        var response = await patient.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(nudged) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ClinicBuilder.Body(response);
        var replacementId = body.GetProperty("id").GetGuid();

        Assert.NotEqual(id, replacementId);
        Assert.Equal(ClinicBuilder.Utc(nudged), body.GetProperty("startsAt").GetString());
        Assert.Equal(nameof(AppointmentStatus.Scheduled), body.GetProperty("status").GetString());

        var original = await AppointmentAsync(id);
        var replacement = await AppointmentAsync(replacementId);

        Assert.Equal(AppointmentStatus.Rescheduled, original.Status);
        Assert.Equal(start, original.StartsAt);
        Assert.Equal(id, replacement.RescheduledFromId);
        Assert.Equal(clinic.ProfessionalId, replacement.ProfessionalId);
        Assert.Equal(original.PatientId, replacement.PatientId);
    }

    [Fact]
    public async Task A_reschedule_to_another_day_also_succeeds()
    {
        // The control for the test above. If this passes and the near move fails, the fault is the
        // statement ordering or the busy-set filter rather than the transition itself — which is
        // the whole reason to keep a test that would pass with the bug present.
        var clinic = await Clinic.BuildAsync();
        var later = await Clinic.BuildAsync(daysAhead: 14);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        // Same clinic, a different date, so nothing about the professional or the type changes.
        var response = await patient.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 11)) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        _ = later;
    }

    [Fact]
    public async Task The_vacated_time_is_offered_again_and_the_new_time_is_not()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);
        var moved = ClinicBuilder.At(clinic.Date, 11);

        var id = await BookAsync(patient, clinic, start);

        await ClinicBuilder.Succeeds(patient.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(moved) }));

        var offered = await OfferedStartsAsync(patient, clinic);

        Assert.Contains(ClinicBuilder.Utc(start), offered);
        Assert.DoesNotContain(ClinicBuilder.Utc(moved), offered);
    }

    [Fact]
    public async Task A_refused_reschedule_leaves_the_original_exactly_as_it_was()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);
        var id = await BookAsync(patient, clinic, start);

        // 20:00 is outside the 09:00-12:00 working hours the fixture builds.
        var refused = await patient.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 20)) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("booking.outside_working_hours", await ClinicBuilder.CodeOf(refused));

        var stored = await AppointmentAsync(id);

        Assert.Equal(AppointmentStatus.Scheduled, stored.Status);
        Assert.Equal(start, stored.StartsAt);
        Assert.Equal(1, await LiveAppointmentCountAsync(clinic));
    }

    [Fact]
    public async Task The_replacement_uses_the_duration_in_force_at_the_reschedule()
    {
        var clinic = await Clinic.BuildAsync(durationMinutes: 60);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var start = ClinicBuilder.At(clinic.Date, 9);
        var id = await BookAsync(patient, clinic, start);

        await ClinicBuilder.Succeeds(admin.PutAsync(
            $"/api/config/professionals/{clinic.ProfessionalUser.Id}/durations",
            new { appointmentTypeId = clinic.AppointmentTypeId, durationMinutes = 30 }));

        var response = await patient.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 11)) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var replacement = await AppointmentAsync((await ClinicBuilder.Body(response)).GetProperty("id").GetGuid());
        var original = await AppointmentAsync(id);

        // I1 in the one situation that distinguishes the two readings of it: a MOVED range would
        // have carried the old 60 minutes forward.
        Assert.Equal(Duration.FromMinutes(30), replacement.EndsAt - replacement.StartsAt);
        Assert.Equal(Duration.FromMinutes(60), original.EndsAt - original.StartsAt);
    }

    [Fact]
    public async Task The_reschedule_chain_keeps_every_link()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var first = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        var second = await RescheduleAsync(patient, first, ClinicBuilder.At(clinic.Date, 10));
        var third = await RescheduleAsync(patient, second, ClinicBuilder.At(clinic.Date, 11));

        Assert.Null((await AppointmentAsync(first)).RescheduledFromId);
        Assert.Equal(first, (await AppointmentAsync(second)).RescheduledFromId);
        Assert.Equal(second, (await AppointmentAsync(third)).RescheduledFromId);

        // Three rows, one live. Nothing collapses the chain, which is what makes the history
        // reconstructible for audit and LGPD.
        Assert.Equal(1, await LiveAppointmentCountAsync(clinic));
    }

    // --- The races -------------------------------------------------------------------

    [Fact]
    public async Task Two_concurrent_cancellations_yield_exactly_one_cancellation()
    {
        var clinic = await Clinic.BuildAsync();
        var (first, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _first = first;

        var second = await fixture.AsUserAsync(user);
        using var _second = second;

        var id = await BookAsync(first, clinic, ClinicBuilder.At(clinic.Date, 9));

        var results = await Task.WhenAll(
            first.PostAsync($"/api/appointments/{id}/cancel", new { }),
            second.PostAsync($"/api/appointments/{id}/cancel", new { }));

        Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.OK));

        var loser = results.Single(response => response.StatusCode != HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("booking.appointment_not_changeable", await ClinicBuilder.CodeOf(loser));
        Assert.Equal(AppointmentStatus.Cancelled, (await AppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task A_cancel_and_a_reschedule_racing_on_one_appointment_yield_one_outcome()
    {
        // The race no exclusion constraint can see, because it is about ONE row's lifecycle rather
        // than about overlap between rows (design C8). Without the FOR UPDATE both requests read
        // the same `Scheduled` snapshot, both pass the aggregate's guard, and the patient ends up
        // with a cancelled appointment AND a live replacement.
        var clinic = await Clinic.BuildAsync();
        var (canceller, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _canceller = canceller;

        var rescheduler = await fixture.AsUserAsync(user);
        using var _rescheduler = rescheduler;

        var id = await BookAsync(canceller, clinic, ClinicBuilder.At(clinic.Date, 9));

        var results = await Task.WhenAll(
            canceller.PostAsync($"/api/appointments/{id}/cancel", new { }),
            rescheduler.PostAsync(
                $"/api/appointments/{id}/reschedule",
                new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 11)) }));

        Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.OK));

        var stored = await AppointmentAsync(id);
        var live = await LiveAppointmentCountAsync(clinic);

        // Whichever won, the outcome is coherent: a cancellation leaves nothing live, and a
        // reschedule leaves exactly its replacement. What must never happen is both.
        if (stored.Status == AppointmentStatus.Cancelled)
        {
            Assert.Equal(0, live);
        }
        else
        {
            Assert.Equal(AppointmentStatus.Rescheduled, stored.Status);
            Assert.Equal(1, live);
        }
    }

    [Fact]
    public async Task A_reschedule_and_a_colliding_block_serialize_on_the_professional()
    {
        // The G1 lock, on the reschedule path. It INSERTS, so it races block creation exactly as a
        // booking does — which is why this path keeps the advisory lock while cancel does not.
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);
        using var _professional = professional;

        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        var results = await Task.WhenAll(
            patient.PostAsync(
                $"/api/appointments/{id}/reschedule",
                new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 11)) }),
            professional.PostAsync("/api/blocks", BlockOver(clinic, 11, 12)));

        // Exactly one: either the block landed first and the reschedule found the time blocked, or
        // the reschedule landed first and the block was refused as colliding.
        Assert.Equal(1, results.Count(response => response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created));
    }

    // --- The cutoff ------------------------------------------------------------------

    [Fact]
    public async Task A_patient_cannot_cancel_inside_the_cutoff()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var id = await SoonAppointmentAsync(clinic, await Clinic.PatientIdAsync(user.Id));

        var response = await patient.PostAsync($"/api/appointments/{id}/cancel", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("booking.cutoff_passed", await ClinicBuilder.CodeOf(response));
        Assert.Equal(AppointmentStatus.Scheduled, (await AppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task A_patient_cannot_reschedule_inside_the_cutoff_and_nothing_is_created()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var id = await SoonAppointmentAsync(clinic, await Clinic.PatientIdAsync(user.Id));

        var response = await patient.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 11)) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("booking.cutoff_passed", await ClinicBuilder.CodeOf(response));

        Assert.Equal(AppointmentStatus.Scheduled, (await AppointmentAsync(id)).Status);
        Assert.Equal(1, await LiveAppointmentCountAsync(clinic));
    }

    [Fact]
    public async Task Outside_the_cutoff_a_patient_may_still_act()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        // A week out, so the 24-hour cutoff is not in play — the control for the two above.
        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        await ClinicBuilder.Succeeds(patient.PostAsync($"/api/appointments/{id}/cancel", new { }));
    }

    // --- Ownership, and the non-enumerability of appointment ids ---------------------

    [Fact]
    public async Task Another_patients_appointment_and_an_unknown_id_answer_identically()
    {
        var clinic = await Clinic.BuildAsync();
        var (owner, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _owner = owner;

        var (stranger, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _stranger = stranger;

        var id = await BookAsync(owner, clinic, ClinicBuilder.At(clinic.Date, 9));

        var theirs = await stranger.PostAsync($"/api/appointments/{id}/cancel", new { });
        var imaginary = await stranger.PostAsync($"/api/appointments/{Guid.NewGuid()}/cancel", new { });

        // ASSERTED, not merely commented (design C6). Two different answers would let a patient
        // enumerate appointment ids and learn which are real — the same reasoning the catalogue
        // already applied to patient records.
        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, imaginary.StatusCode);
        Assert.Equal("auth.ownership_denied", await ClinicBuilder.CodeOf(theirs));
        Assert.Equal("auth.ownership_denied", await ClinicBuilder.CodeOf(imaginary));

        Assert.Equal(AppointmentStatus.Scheduled, (await AppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task A_professional_cannot_change_an_appointment()
    {
        // booking-desk admitted reception to these two writes and left the professional refused.
        // Changing an appointment is reception's work; a clinician who could would be a second
        // route to the same transition with nothing on this path expecting them.
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var (professional, _) = await fixture.AsRoleAsync(Role.Professional);
        using var _professional = professional;

        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        var cancel = await professional.PostAsync($"/api/appointments/{id}/cancel", new { });

        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        Assert.Equal("auth.forbidden", await ClinicBuilder.CodeOf(cancel));
        Assert.Equal(AppointmentStatus.Scheduled, (await AppointmentAsync(id)).Status);
    }

    [Theory]
    [InlineData(Role.Professional)]
    [InlineData(Role.FrontDesk)]
    [InlineData(Role.Administrator)]
    public async Task Only_a_patient_reads_the_my_appointments_list(Role role)
    {
        // P5's list is "my appointments" and no staff role has one. The day view (S1/S4) is the
        // staff reading of the same rows, with a different shape and its own access log — so this
        // route stayed patient-only when the two writes beside it widened.
        var (staff, _) = await fixture.AsRoleAsync(role);
        using var _staff = staff;

        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/appointments")).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_reaches_none_of_the_three()
    {
        using var anonymous = fixture.CreateAnonymousClient();

        var list = await anonymous.GetAsync("/api/appointments");
        var cancel = await anonymous.PostAsync($"/api/appointments/{Guid.NewGuid()}/cancel", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, cancel.StatusCode);
    }

    // --- Terminal guards -------------------------------------------------------------

    [Fact]
    public async Task A_cancelled_appointment_refuses_both_transitions()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        await ClinicBuilder.Succeeds(patient.PostAsync($"/api/appointments/{id}/cancel", new { }));

        var cancelAgain = await patient.PostAsync($"/api/appointments/{id}/cancel", new { });
        var reschedule = await patient.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 11)) });

        Assert.Equal("booking.appointment_not_changeable", await ClinicBuilder.CodeOf(cancelAgain));
        Assert.Equal("booking.appointment_not_changeable", await ClinicBuilder.CodeOf(reschedule));
        Assert.Equal(0, await LiveAppointmentCountAsync(clinic));
    }

    [Fact]
    public async Task A_rescheduled_appointment_cannot_be_cancelled()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        await RescheduleAsync(patient, id, ClinicBuilder.At(clinic.Date, 11));

        var response = await patient.PostAsync($"/api/appointments/{id}/cancel", new { });

        Assert.Equal("booking.appointment_not_changeable", await ClinicBuilder.CodeOf(response));

        // The replacement is still live: cancelling the husk must not have freed it.
        Assert.Equal(1, await LiveAppointmentCountAsync(clinic));
    }

    // --- The consent asymmetry -------------------------------------------------------

    [Fact]
    public async Task A_revoked_consent_refuses_a_reschedule_and_permits_a_cancel()
    {
        // The asymmetry is the point (design C11). A reschedule CREATES an appointment, so it goes
        // through the gate booking-core built. A cancel reduces what the clinic holds, and
        // refusing it would trap a patient in an appointment as a consequence of exercising a
        // right over their own data.
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var first = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        await ClinicBuilder.Succeeds(
            patient.PostAsync($"/api/patients/me/consents/{nameof(ConsentType.DataProcessing)}/revoke", new { }));

        var reschedule = await patient.PostAsync(
            $"/api/appointments/{first}/reschedule",
            new { startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 11)) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reschedule.StatusCode);
        Assert.Equal("auth.consent_required", await ClinicBuilder.CodeOf(reschedule));
        Assert.Equal(AppointmentStatus.Scheduled, (await AppointmentAsync(first)).Status);

        var cancel = await patient.PostAsync($"/api/appointments/{first}/cancel", new { });

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
    }

    // --- P5's payload ----------------------------------------------------------------

    [Fact]
    public async Task The_list_carries_only_the_callers_own_appointments()
    {
        var clinic = await Clinic.BuildAsync();
        var (owner, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _owner = owner;

        var (stranger, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _stranger = stranger;

        var mine = await BookAsync(owner, clinic, ClinicBuilder.At(clinic.Date, 9));

        await BookAsync(stranger, clinic, ClinicBuilder.At(clinic.Date, 11));

        var body = await ClinicBuilder.Body(await owner.GetAsync("/api/appointments"));

        var ids = body.GetProperty("upcoming").EnumerateArray()
            .Select(entry => entry.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(mine, ids);
        Assert.Single(ids);
    }

    [Fact]
    public async Task The_list_says_which_appointments_can_still_be_changed()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var patientId = await Clinic.PatientIdAsync(user.Id);

        var changeable = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));
        var locked = await SoonAppointmentAsync(clinic, patientId);

        var body = await ClinicBuilder.Body(await patient.GetAsync("/api/appointments"));
        var entries = body.GetProperty("upcoming").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("id").GetGuid(), entry => entry.GetProperty("canChange").GetBoolean());

        Assert.True(entries[changeable]);
        Assert.False(entries[locked]);

        // And the zone travels with the payload, so the screen never guesses (design C10).
        Assert.Equal(ApiFixture.ClinicTimezoneId, body.GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task A_terminal_appointment_is_listed_with_its_status_and_cannot_be_changed()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var id = await BookAsync(patient, clinic, ClinicBuilder.At(clinic.Date, 9));

        await ClinicBuilder.Succeeds(patient.PostAsync($"/api/appointments/{id}/cancel", new { }));

        var body = await ClinicBuilder.Body(await patient.GetAsync("/api/appointments"));

        var entry = body.GetProperty("upcoming").EnumerateArray()
            .Concat(body.GetProperty("past").EnumerateArray())
            .Single(candidate => candidate.GetProperty("id").GetGuid() == id);

        // Present rather than filtered away: "what happened to my 3pm?" is answerable only if the
        // row is still listed with what happened to it.
        Assert.Equal(nameof(AppointmentStatus.Cancelled), entry.GetProperty("status").GetString());
        Assert.False(entry.GetProperty("canChange").GetBoolean());
    }

    // --- Helpers ---------------------------------------------------------------------

    private static object BlockOver(BookableClinic clinic, int fromHour, int toHour) =>
        new
        {
            startsAt = ClinicBuilder.Wall(clinic.Date, fromHour),
            endsAt = ClinicBuilder.Wall(clinic.Date, toHour),
            reason = "lunch",
        };

    private static async Task<Guid> BookAsync(
        TestClient patient,
        BookableClinic clinic,
        Instant startsAt,
        Guid? professionalId = null)
    {
        var response = await patient.PostAsync(
            "/api/appointments",
            new
            {
                appointmentTypeId = clinic.AppointmentTypeId,
                professionalId = professionalId ?? clinic.ProfessionalId,
                startsAt = ClinicBuilder.Utc(startsAt),
            });

        await ClinicBuilder.Succeeds(Task.FromResult(response));

        return (await ClinicBuilder.Body(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> RescheduleAsync(TestClient patient, Guid id, Instant startsAt)
    {
        var response = await patient.PostAsync(
            $"/api/appointments/{id}/reschedule",
            new { startsAt = ClinicBuilder.Utc(startsAt) });

        await ClinicBuilder.Succeeds(Task.FromResult(response));

        return (await ClinicBuilder.Body(response)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// An appointment a few hours from now — inside the 24-hour cutoff, outside the lead time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written through the domain factory rather than booked through the API, and the reason is
    /// that it <em>cannot</em> be booked through the API: a start three hours from now is almost
    /// never inside the fixture's 09:00-12:00 working hours, and arranging for it to be would make
    /// the test's outcome depend on the wall-clock time the suite happens to run at.
    /// </para>
    /// <para>
    /// Legitimate arrangement rather than a bypass: the row goes through the same factory the
    /// handler uses, so every invariant still holds. What is skipped is the solver's opinion about
    /// whether that time is <em>offerable</em>, which is not what these tests are about.
    /// </para>
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

    private async Task<IReadOnlyList<string>> OfferedStartsAsync(
        TestClient client,
        BookableClinic clinic,
        Guid? professionalId = null)
    {
        var url = $"/api/availability?appointmentTypeId={clinic.AppointmentTypeId}"
            + $"&from={ClinicBuilder.Iso(clinic.Date)}&to={ClinicBuilder.Iso(clinic.Date)}"
            + $"&professionalId={professionalId ?? clinic.ProfessionalId}";

        var response = await client.GetAsync(url);

        await ClinicBuilder.Succeeds(Task.FromResult(response));

        return (await ClinicBuilder.Body(response))
            .GetProperty("slots")
            .EnumerateArray()
            .Select(slot => slot.GetProperty("start").GetString()!)
            .ToList();
    }
}
