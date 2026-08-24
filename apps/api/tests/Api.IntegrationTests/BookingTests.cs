using System.Net;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// Booking, end to end (spec: booking).
/// </summary>
/// <remarks>
/// <para>
/// The unit tier proves the aggregate refuses what it should and that <c>Solve</c> and
/// <c>Explain</c> agree. What only this tier can prove is the part the whole change exists for:
/// that <b>two simultaneous bookings cannot both commit</b>, and that they cannot because of the
/// database rather than because of the code above it. Neither claim is expressible without a real
/// PostgreSQL and real concurrency.
/// </para>
/// <para>
/// It also carries the assertions about the loading step, every one of which has an
/// active-predicate, a window bound or an ordering that can be on the wrong side while every unit
/// test still passes.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class BookingTests(ApiFixture fixture)
{
    private ClinicBuilder Clinic => new(fixture);

    private static object Booking(BookableClinic clinic, Instant startsAt, Guid? professionalId = null) =>
        new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = professionalId ?? clinic.ProfessionalId,
            startsAt = ClinicBuilder.Utc(startsAt),
        };

    // --- The happy path --------------------------------------------------------------

    [Fact]
    public async Task A_patient_books_an_offered_slot_and_the_server_assigns_the_room()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);

        var response = await patient.PostAsync("/api/appointments", Booking(clinic, start));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ClinicBuilder.Body(response);

        Assert.Equal(clinic.ProfessionalId, body.GetProperty("professionalId").GetGuid());
        Assert.Equal(ClinicBuilder.Utc(start), body.GetProperty("startsAt").GetString());
        Assert.Equal(
            ClinicBuilder.Utc(start + Duration.FromMinutes(clinic.DurationMinutes)),
            body.GetProperty("endsAt").GetString());
        Assert.Equal(nameof(AppointmentStatus.Scheduled), body.GetProperty("status").GetString());

        // The response deliberately carries no room: the server assigned one and a patient does
        // not need to know which. What it assigned is asserted against the row instead.
        Assert.False(body.TryGetProperty("resourceId", out _));

        var stored = await SingleAppointmentAsync(clinic);

        Assert.Equal(clinic.RoomId, stored.ResourceId);
        Assert.Equal(await Clinic.PatientIdAsync(user.Id), stored.PatientId);
        Assert.Equal(AppointmentSource.SelfService, stored.Source);
    }

    [Fact]
    public async Task The_request_carries_no_room_and_a_supplied_one_changes_nothing()
    {
        // Two rooms; the first is free, so the server must pick it whatever the caller says.
        var clinic = await Clinic.BuildAsync(rooms: 2);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var response = await patient.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = clinic.ProfessionalId,
            startsAt = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9)),

            // The value change 4's risk register warned a client might echo back as authority.
            // It is not in the contract, so it is not bound and cannot influence anything —
            // domain-model F2 held structurally rather than by a check.
            resourceId = clinic.Rooms[1],
            patientId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await SingleAppointmentAsync(clinic);

        Assert.Equal(clinic.Rooms[0], stored.ResourceId);
    }

    [Fact]
    public async Task The_room_falls_through_when_the_first_is_taken()
    {
        var clinic = await Clinic.BuildAsync(rooms: 2, professionals: 2);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var (first, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _first = first;

        await ClinicBuilder.Succeeds(first.PostAsync(
            "/api/appointments", Booking(clinic, start, clinic.Professionals[0])));

        var (second, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _second = second;

        await ClinicBuilder.Succeeds(second.PostAsync(
            "/api/appointments", Booking(clinic, start, clinic.Professionals[1])));

        var rooms = await AppointmentRoomsAsync(clinic);

        // Two appointments at the same instant with different professionals, in different rooms —
        // the second fell through because the first room was occupied by the first booking. This
        // is the resource half of the seam, filled.
        //
        // Asserted as a set rather than a sequence: Guid ordering is not creation ordering, and
        // what matters is that the two bookings landed in two DIFFERENT rooms, both of them the
        // clinic's.
        Assert.Equal(2, rooms.Count);
        Assert.Equal(2, rooms.Distinct().Count());
        Assert.All(rooms, room => Assert.Contains(room, clinic.Rooms));
    }

    [Fact]
    public async Task Each_professionals_own_duration_is_baked_into_their_appointment()
    {
        var clinic = await Clinic.BuildAsync(durationMinutes: 40, endHour: 18);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);

        await ClinicBuilder.Succeeds(patient.PostAsync("/api/appointments", Booking(clinic, start)));

        var stored = await SingleAppointmentAsync(clinic);

        Assert.Equal(Duration.FromMinutes(40), stored.EndsAt - stored.StartsAt);
    }

    [Fact]
    public async Task Changing_the_duration_afterwards_leaves_the_appointment_alone()
    {
        var clinic = await Clinic.BuildAsync(durationMinutes: 60, endHour: 18);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9))));

        var booked = await SingleAppointmentAsync(clinic);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        await ClinicBuilder.Succeeds(admin.PutAsync(
            $"/api/config/professionals/{clinic.ProfessionalUser.Id}/durations",
            new { appointmentTypeId = clinic.AppointmentTypeId, durationMinutes = 90 }));

        var after = await SingleAppointmentAsync(clinic);

        // I1: the appointment holds its own range, so a later configuration edit moves future
        // searches and cannot reach this row.
        Assert.Equal(booked.EndsAt, after.EndsAt);

        // And the new duration IS in force for new searches, so the test is about baking rather
        // than about the edit having failed.
        var offered = await OfferedStartsAsync(patient, clinic);

        Assert.NotEmpty(offered);
    }

    // --- The floor: concurrent double-booking ----------------------------------------

    [Fact]
    public async Task Two_simultaneous_bookings_for_one_professionals_slot_cannot_both_succeed()
    {
        var clinic = await Clinic.BuildAsync(rooms: 3);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var (first, _) = await fixture.AsRoleAsync(Role.Patient);
        var (second, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _first = first;
        using var _second = second;

        // Two different patients, so the patient constraint cannot fire. Both transactions still
        // assign the same first-free room, so the professional and resource constraints are both
        // violated and either may be the one reported.
        var results = await Task.WhenAll(
            first.PostAsync("/api/appointments", Booking(clinic, start)),
            second.PostAsync("/api/appointments", Booking(clinic, start)));

        await AssertExactlyOneWonAsync(
            clinic, results, "booking.slot_taken", "booking.resource_unavailable");
    }

    [Fact]
    public async Task Two_simultaneous_bookings_cannot_share_the_last_free_room()
    {
        var clinic = await Clinic.BuildAsync(rooms: 1, professionals: 2);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var (first, _) = await fixture.AsRoleAsync(Role.Patient);
        var (second, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _first = first;
        using var _second = second;

        // Different professionals, so the professional lock does NOT serialize them and the
        // professional constraint cannot fire. Different patients, so the patient constraint
        // cannot either. One room: the resource constraint is the only thing that can say no.
        var results = await Task.WhenAll(
            first.PostAsync("/api/appointments", Booking(clinic, start, clinic.Professionals[0])),
            second.PostAsync("/api/appointments", Booking(clinic, start, clinic.Professionals[1])));

        // One room and two different patients with two different professionals: the resource
        // constraint is the ONLY one that can be violated, so this code is deterministic.
        await AssertExactlyOneWonAsync(clinic, results, "booking.resource_unavailable");
    }

    [Fact]
    public async Task One_patient_cannot_be_booked_into_two_places_at_once()
    {
        var clinic = await Clinic.BuildAsync(rooms: 3, professionals: 2);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        // Two clients for ONE user, rather than two concurrent posts on one client: an unsafe
        // request fetches the CSRF token from its own cookie container first, so two in flight on
        // one client race that handshake and one arrives without a token. Two sessions for the same
        // person is also the more realistic shape — a phone and a laptop.
        var second = await fixture.AsUserAsync(user);
        using var _second = second;

        // The SAME patient, two professionals, three rooms. Only the patient constraint applies.
        var results = await Task.WhenAll(
            patient.PostAsync("/api/appointments", Booking(clinic, start, clinic.Professionals[0])),
            second.PostAsync("/api/appointments", Booking(clinic, start, clinic.Professionals[1])));

        await AssertExactlyOneWonAsync(
            clinic, results, "booking.patient_busy", "booking.resource_unavailable");
    }

    [Fact]
    public async Task A_patient_already_booked_elsewhere_is_refused_when_booking_another_professional()
    {
        var clinic = await Clinic.BuildAsync(rooms: 2, professionals: 2);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            "/api/appointments", Booking(clinic, start, clinic.Professionals[0])));

        var refused = await patient.PostAsync(
            "/api/appointments", Booking(clinic, start, clinic.Professionals[1]));

        // Sequential, so the answer is deterministic and it is I6 that is named. The second request
        // sees the first appointment committed: the second professional is free, and the solver
        // falls through to the second room — so the ONLY thing wrong is that the patient is already
        // somewhere else. This is the scenario the spec describes, and the concurrent version cannot
        // assert it because a race collides on the room as well.
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("booking.patient_busy", await ClinicBuilder.CodeOf(refused));
        Assert.Equal(1, await LiveAppointmentCountAsync(clinic));
    }

    [Fact]
    public async Task A_repeated_confirmation_does_not_book_twice()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var request = Booking(clinic, ClinicBuilder.At(clinic.Date, 9));

        await ClinicBuilder.Succeeds(patient.PostAsync("/api/appointments", request));

        var again = await patient.PostAsync("/api/appointments", request);

        // A double-submitted confirm button, self-defending without an idempotency mechanism this
        // system does not have.
        //
        // Refused as `slot_taken` rather than `patient_busy`, and that is the honest answer: the
        // second request names the same professional at the same time, so the professional IS now
        // busy — with this patient's own appointment, a distinction the patient has no reason to
        // care about. `patient_busy` is the different-professional case, which has its own test.
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("booking.slot_taken", await ClinicBuilder.CodeOf(again));
        Assert.Equal(1, await AppointmentCountAsync(clinic));
    }

    [Fact]
    public async Task The_database_refuses_an_overlapping_appointment_written_past_the_application()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);

        await ClinicBuilder.Succeeds(patient.PostAsync("/api/appointments", Booking(clinic, start)));

        var patientId = await Clinic.PatientIdAsync(user.Id);

        // Written straight through the aggregate into the context, bypassing every handler check.
        // THIS is the assertion that the guarantee does not depend on the code above it — the
        // difference between "no double-booking by construction" and "by convention".
        var failure = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await fixture.WithDatabaseAsync(async database =>
            {
                database.Appointments.Add(Appointment.Book(
                    new AppointmentBooking(
                        patientId,
                        clinic.ProfessionalId,
                        clinic.RoomId,
                        clinic.AppointmentTypeId,
                        start + Duration.FromMinutes(15),
                        clinic.DurationMinutes,
                        ProfessionalHoldsDurationForType: true,
                        clinic.ResourceTypeId,
                        clinic.ResourceTypeId,
                        AppointmentSource.SelfService),
                    SchedulingParameters.Of(15, 0, 3650),
                    start - Duration.FromDays(1),
                    DateTimeOffset.UtcNow));

                await database.SaveChangesAsync();
            }));

        Assert.NotNull(failure);
        Assert.Equal(1, await AppointmentCountAsync(clinic));
    }

    [Fact]
    public async Task An_appointment_in_a_terminal_state_frees_the_time_it_held()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);

        await ClinicBuilder.Succeeds(patient.PostAsync("/api/appointments", Booking(clinic, start)));

        // Written directly, because booking-core deliberately offers no transition — 5b does. What
        // is being proved is that when 5b writes Cancelled, the slot frees itself with no migration
        // and no constraint change, because the predicate already reads status = 'Scheduled'.
        await fixture.WithDatabaseAsync(async database =>
        {
            await database.Database.ExecuteSqlAsync(
                $"UPDATE appointments SET status = 'Cancelled' WHERE status = 'Scheduled' AND appointment_type_id = {clinic.AppointmentTypeId}");
        });

        var offered = await OfferedStartsAsync(patient, clinic);

        Assert.Contains(ClinicBuilder.Utc(start), offered);

        var again = await patient.PostAsync("/api/appointments", Booking(clinic, start));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await LiveAppointmentCountAsync(clinic));
    }

    // --- I7 in both directions, and the G1 lock --------------------------------------

    [Fact]
    public async Task Booking_over_the_professionals_own_block_is_refused_as_blocked_not_as_taken()
    {
        var clinic = await Clinic.BuildAsync();

        var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);
        using var _professional = professional;

        await ClinicBuilder.Succeeds(professional.PostAsync("/api/blocks", new
        {
            startsAt = ClinicBuilder.Wall(clinic.Date, 9),
            endsAt = ClinicBuilder.Wall(clinic.Date, 10),
        }));

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var refused = await patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9)));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // The distinction the new code exists for: nobody took this slot, the professional
        // declared themselves unavailable. Telling the patient "someone just booked it" would send
        // them looking for a race that did not happen.
        Assert.Equal("booking.slot_blocked", await ClinicBuilder.CodeOf(refused));
        Assert.Equal(0, await AppointmentCountAsync(clinic));
    }

    [Fact]
    public async Task Booking_at_the_instant_a_block_ends_is_accepted()
    {
        var clinic = await Clinic.BuildAsync();

        var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);
        using var _professional = professional;

        await ClinicBuilder.Succeeds(professional.PostAsync("/api/blocks", new
        {
            startsAt = ClinicBuilder.Wall(clinic.Date, 9),
            endsAt = ClinicBuilder.Wall(clinic.Date, 10),
        }));

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        // Touching is not overlapping, on the write path as on the read path.
        var response = await patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 10)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Blocking_over_an_appointment_is_refused_and_nothing_is_stored()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9))));

        var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);
        using var _professional = professional;

        var refused = await professional.PostAsync("/api/blocks", new
        {
            startsAt = ClinicBuilder.Wall(clinic.Date, 9, 30),
            endsAt = ClinicBuilder.Wall(clinic.Date, 11),
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("booking.block_overlaps_appointment", await ClinicBuilder.CodeOf(refused));

        // The refusal is decided before any write, so nothing is stored — the property change 4's
        // screen already relies on for its invalid-range refusal.
        Assert.Equal(0, await BlockCountAsync(clinic));
    }

    [Fact]
    public async Task Blocking_at_the_instant_an_appointment_ends_is_accepted()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9))));

        var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);
        using var _professional = professional;

        var response = await professional.PostAsync("/api/blocks", new
        {
            startsAt = ClinicBuilder.Wall(clinic.Date, 10),
            endsAt = ClinicBuilder.Wall(clinic.Date, 11),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await BlockCountAsync(clinic));
    }

    [Fact]
    public async Task Moving_a_block_onto_an_appointment_is_refused_and_the_stored_range_is_untouched()
    {
        var clinic = await Clinic.BuildAsync(endHour: 18);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 14))));

        var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);
        using var _professional = professional;

        var created = await professional.PostAsync("/api/blocks", new
        {
            startsAt = ClinicBuilder.Wall(clinic.Date, 9),
            endsAt = ClinicBuilder.Wall(clinic.Date, 10),
        });

        await ClinicBuilder.Succeeds(Task.FromResult(created));

        var blockId = (await ClinicBuilder.Body(created)).GetProperty("id").GetGuid();

        var refused = await professional.PutAsync($"/api/blocks/{blockId}", new
        {
            startsAt = ClinicBuilder.Wall(clinic.Date, 14),
            endsAt = ClinicBuilder.Wall(clinic.Date, 15),
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("booking.block_overlaps_appointment", await ClinicBuilder.CodeOf(refused));

        // The stored range is what the screen keeps showing, so it must be the OLD one even though
        // the in-memory entity had already been moved before the check ran.
        var listed = await professional.GetAsync("/api/blocks");
        var blocks = (await ClinicBuilder.Body(listed)).GetProperty("blocks");

        Assert.Equal(ClinicBuilder.Wall(clinic.Date, 9), blocks[0].GetProperty("startsAt").GetString());
    }

    [Fact]
    public async Task A_block_over_another_professionals_appointment_is_accepted()
    {
        var clinic = await Clinic.BuildAsync(rooms: 2, professionals: 2);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9), clinic.Professionals[0])));

        var other = await fixture.AsUserAsync(clinic.ProfessionalUsers[1]);
        using var _other = other;

        // The check is scoped to the block's own professional. Somebody else's appointment is
        // nobody's conflict.
        var response = await other.PostAsync("/api/blocks", new
        {
            startsAt = ClinicBuilder.Wall(clinic.Date, 9),
            endsAt = ClinicBuilder.Wall(clinic.Date, 10),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_booking_and_a_colliding_block_cannot_both_commit()
    {
        var clinic = await Clinic.BuildAsync();
        var start = ClinicBuilder.At(clinic.Date, 9);

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        var professional = await fixture.AsUserAsync(clinic.ProfessionalUser);
        using var _patient = patient;
        using var _professional = professional;

        // The G1 lock's reason for existing: a cross-table read-then-write race that no exclusion
        // constraint can adjudicate, because the two rows are in different tables.
        var results = await Task.WhenAll(
            patient.PostAsync("/api/appointments", Booking(clinic, start)),
            professional.PostAsync("/api/blocks", new
            {
                startsAt = ClinicBuilder.Wall(clinic.Date, 9),
                endsAt = ClinicBuilder.Wall(clinic.Date, 10),
            }));

        var succeeded = results.Count(response => response.StatusCode == HttpStatusCode.OK);
        var appointments = await AppointmentCountAsync(clinic);
        var blocks = await BlockCountAsync(clinic);

        // Exactly one of the two exists afterwards. Whichever won, the loser was refused as
        // colliding with what the winner created — which is only possible if the two paths were
        // serialized.
        Assert.Equal(1, succeeded);
        Assert.Equal(1, appointments + blocks);
    }

    // --- Read/write agreement at the API level ---------------------------------------

    [Fact]
    public async Task Every_slot_the_read_offers_is_bookable()
    {
        var clinic = await Clinic.BuildAsync(startHour: 9, endHour: 12, durationMinutes: 60, rooms: 4);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var offered = await OfferedStartsAsync(patient, clinic);

        Assert.NotEmpty(offered);

        // Each in isolation, because booking one removes its neighbours — which is correct and is
        // not what this asserts. The reset between attempts keeps the question "would the write
        // accept this offered slot" rather than "do these slots coexist".
        foreach (var start in offered)
        {
            var response = await patient.PostAsync("/api/appointments", new
            {
                appointmentTypeId = clinic.AppointmentTypeId,
                professionalId = clinic.ProfessionalId,
                startsAt = start,
            });

            if (response.StatusCode != HttpStatusCode.OK)
            {
                Assert.Fail($"the read offered {start} but the write refused it with "
                    + $"{(int)response.StatusCode} {await ClinicBuilder.CodeOf(response)}");
            }

            await ClearAppointmentsAsync(clinic);
        }
    }

    [Fact]
    public async Task A_start_the_read_withholds_for_lead_time_is_refused_by_the_write()
    {
        // Colon-separated, like every other override in this suite: these become an in-memory
        // configuration source, where the double-underscore environment-variable spelling is just
        // a key nothing reads.
        //
        // Seven days is the largest lead time the options type permits, so the clinic is built two
        // days out rather than the parameter being pushed past its Range — which would fail startup
        // validation and look like a different bug entirely.
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Scheduling:MinimumLeadTimeMinutes"] = $"{7 * 24 * 60}",
        });

        var clinic = await Clinic.BuildAsync(daysAhead: 2);

        var patient = fixture.CreateClientFor(
            host, await ApiFixture.IssueSessionOnAsync(host, await fixture.SeedUserAsync(Role.Patient)));
        using var _patient = patient;

        var offered = await OfferedStartsAsync(patient, clinic);

        Assert.Empty(offered);

        var refused = await patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9)));

        Assert.Equal(HttpStatusCode.UnprocessableContent, refused.StatusCode);
        Assert.Equal("booking.lead_time_violation", await ClinicBuilder.CodeOf(refused));
    }

    [Fact]
    public async Task A_start_beyond_the_horizon_is_withheld_by_the_read_and_refused_by_the_write()
    {
        // The target date is a week out, so a one-day horizon puts it beyond.
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Scheduling:HorizonDays"] = "1",
        });

        var clinic = await Clinic.BuildAsync();

        var patient = fixture.CreateClientFor(
            host, await ApiFixture.IssueSessionOnAsync(host, await fixture.SeedUserAsync(Role.Patient)));
        using var _patient = patient;

        Assert.Empty(await OfferedStartsAsync(patient, clinic));

        var refused = await patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9)));

        Assert.Equal(HttpStatusCode.UnprocessableContent, refused.StatusCode);
        Assert.Equal("booking.horizon_exceeded", await ClinicBuilder.CodeOf(refused));
    }

    [Fact]
    public async Task A_start_outside_working_hours_is_refused()
    {
        var clinic = await Clinic.BuildAsync(startHour: 9, endHour: 12);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var refused = await patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 15)));

        Assert.Equal(HttpStatusCode.UnprocessableContent, refused.StatusCode);
        Assert.Equal("booking.outside_working_hours", await ClinicBuilder.CodeOf(refused));
    }

    [Fact]
    public async Task A_qualification_cleared_between_search_and_confirm_is_caught()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.At(clinic.Date, 9);

        // Offered first, so the sequence is genuinely search-then-change-then-confirm.
        Assert.Contains(ClinicBuilder.Utc(start), await OfferedStartsAsync(patient, clinic));

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        await ClinicBuilder.Succeeds(admin.PostAsync(
            $"/api/config/professionals/{clinic.ProfessionalUser.Id}/durations/{clinic.AppointmentTypeId}/clear"));

        var refused = await patient.PostAsync("/api/appointments", Booking(clinic, start));

        Assert.Equal(HttpStatusCode.UnprocessableContent, refused.StatusCode);
        Assert.Equal("booking.specialty_mismatch", await ClinicBuilder.CodeOf(refused));
    }

    [Fact]
    public async Task A_slot_with_every_room_occupied_is_refused()
    {
        var clinic = await Clinic.BuildAsync(rooms: 1, professionals: 2);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var (first, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _first = first;

        await ClinicBuilder.Succeeds(first.PostAsync(
            "/api/appointments", Booking(clinic, start, clinic.Professionals[0])));

        var (second, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _second = second;

        var refused = await second.PostAsync(
            "/api/appointments", Booking(clinic, start, clinic.Professionals[1]));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("booking.resource_unavailable", await ClinicBuilder.CodeOf(refused));
    }

    // --- The seam ---------------------------------------------------------------------

    [Fact]
    public async Task A_booked_slot_and_its_overlapping_neighbours_stop_being_offered()
    {
        // A 15-minute step against a 60-minute visit, so neighbours genuinely overlap.
        var clinic = await Clinic.BuildAsync(startHour: 9, endHour: 12, durationMinutes: 60);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var before = await OfferedStartsAsync(patient, clinic);

        var start = ClinicBuilder.At(clinic.Date, 10);

        await ClinicBuilder.Succeeds(patient.PostAsync("/api/appointments", Booking(clinic, start)));

        var after = await OfferedStartsAsync(patient, clinic);

        // The booked slot is gone...
        Assert.Contains(ClinicBuilder.Utc(start), before);
        Assert.DoesNotContain(ClinicBuilder.Utc(start), after);

        // ...and so are the overlapping neighbours a finer step had offered: 09:15 through 10:45
        // all intersect a 10:00-11:00 visit.
        Assert.DoesNotContain(ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9, 15)), after);
        Assert.DoesNotContain(ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 10, 45)), after);

        // While the slot ending exactly when the appointment begins survives, because touching is
        // not overlapping.
        Assert.Contains(ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9)), after);
        Assert.Contains(ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 11)), after);
    }

    [Fact]
    public async Task A_booking_occupies_its_room_for_another_professional_too()
    {
        var clinic = await Clinic.BuildAsync(rooms: 1, professionals: 2);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            "/api/appointments", Booking(clinic, start, clinic.Professionals[0])));

        var offered = await OfferedStartsAsync(patient, clinic, clinic.Professionals[1]);

        // The second professional is entirely free; the room is not. This is the resource half of
        // the seam, which change 4 shipped typed and empty.
        Assert.DoesNotContain(ClinicBuilder.Utc(start), offered);
    }

    [Fact]
    public async Task A_room_is_not_offered_again_until_its_turnaround_has_passed()
    {
        var clinic = await Clinic.BuildAsync(
            startHour: 9, endHour: 12, durationMinutes: 30, rooms: 1, professionals: 2, bufferMinutes: 15);

        var start = ClinicBuilder.At(clinic.Date, 9);

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await ClinicBuilder.Succeeds(patient.PostAsync(
            "/api/appointments", Booking(clinic, start, clinic.Professionals[0])));

        var forOther = await OfferedStartsAsync(patient, clinic, clinic.Professionals[1]);

        // The appointment runs 09:00-09:30 and the room needs fifteen minutes to be cleaned, so
        // 09:30 is not offerable for that room and 09:45 is.
        Assert.DoesNotContain(ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9, 30)), forOther);
        Assert.Contains(ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9, 45)), forOther);

        // The professional who just finished, by contrast, is free at 09:30 — turnaround belongs
        // to the room, not to the person walking out of it. Only reachable with a second room, so
        // asserted at the unit tier for the professional side; here what matters is the asymmetry
        // does not accidentally apply to the OTHER professional's own availability.
        var forSame = await OfferedStartsAsync(patient, clinic, clinic.Professionals[0]);

        Assert.DoesNotContain(ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9, 30)), forSame);
    }

    // --- Authorization and the consent gate ------------------------------------------

    [Theory]
    [InlineData(Role.Professional)]
    [InlineData(Role.FrontDesk)]
    [InlineData(Role.Administrator)]
    public async Task Staff_cannot_book_through_the_patient_path(Role role)
    {
        var clinic = await Clinic.BuildAsync();
        var (staff, _) = await fixture.AsRoleAsync(role);
        using var _staff = staff;

        var refused = await staff.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9)));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("auth.forbidden", await ClinicBuilder.CodeOf(refused));
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_book()
    {
        var clinic = await Clinic.BuildAsync();
        using var anonymous = fixture.CreateAnonymousClient();

        var refused = await anonymous.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9)));

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("auth.session_expired", await ClinicBuilder.CodeOf(refused));
    }

    [Fact]
    public async Task A_patient_who_revoked_consent_cannot_book_until_they_grant_it_again()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await ClinicBuilder.Succeeds(patient.PostAsync("/api/patients/me/consents/DataProcessing/revoke"));

        var refused = await patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9)));

        // The loop change 2 opened by making revocation possible with nothing checking it.
        Assert.Equal(HttpStatusCode.UnprocessableContent, refused.StatusCode);
        Assert.Equal("auth.consent_required", await ClinicBuilder.CodeOf(refused));
        Assert.Equal(0, await AppointmentCountAsync(clinic));

        await ClinicBuilder.Succeeds(patient.PostAsync("/api/patients/me/consents/DataProcessing/grant"));

        var response = await patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_consent_of_another_version_does_not_satisfy_the_gate()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        // The fixture grants "test"; the running host's configured version is something else. The
        // version comparison is the mechanism a versioned consent exists for, so a gate that
        // ignored it would make Consent.Version decoration.
        await fixture.WithDatabaseAsync(async database =>
        {
            await database.Database.ExecuteSqlAsync(
                $"UPDATE consents SET version = 'ancient' WHERE user_id = {user.Id}");
        });

        var refused = await patient.PostAsync(
            "/api/appointments", Booking(clinic, ClinicBuilder.At(clinic.Date, 9)));

        Assert.Equal(HttpStatusCode.UnprocessableContent, refused.StatusCode);
        Assert.Equal("auth.consent_required", await ClinicBuilder.CodeOf(refused));
    }

    // --- Request validation ----------------------------------------------------------

    [Fact]
    public async Task A_wall_clock_start_is_refused_rather_than_coerced()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var refused = await patient.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = clinic.ProfessionalId,

            // No zone. Accepting this silently is how an appointment lands an hour out on a
            // clock-change date and nobody finds out until a patient misses it (Q4).
            startsAt = ClinicBuilder.Wall(clinic.Date, 9),
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("validation.invalid_format", await ClinicBuilder.CodeOf(refused));
    }

    [Fact]
    public async Task An_unknown_appointment_type_or_professional_is_refused()
    {
        var clinic = await Clinic.BuildAsync();
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var start = ClinicBuilder.Utc(ClinicBuilder.At(clinic.Date, 9));

        var unknownType = await patient.PostAsync("/api/appointments", new
        {
            appointmentTypeId = Guid.NewGuid(),
            professionalId = clinic.ProfessionalId,
            startsAt = start,
        });

        var unknownProfessional = await patient.PostAsync("/api/appointments", new
        {
            appointmentTypeId = clinic.AppointmentTypeId,
            professionalId = Guid.NewGuid(),
            startsAt = start,
        });

        Assert.Equal(HttpStatusCode.NotFound, unknownType.StatusCode);
        Assert.Equal("config.not_found", await ClinicBuilder.CodeOf(unknownType));
        Assert.Equal(HttpStatusCode.NotFound, unknownProfessional.StatusCode);
        Assert.Equal("config.not_found", await ClinicBuilder.CodeOf(unknownProfessional));
    }

    // --- Helpers ---------------------------------------------------------------------

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

    /// <summary>
    /// Exactly one racer committed, and every loser was refused with one of the codes this race can
    /// legitimately produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A set of codes rather than one, and the reason is the interesting part.</b> Two racing
    /// transactions cannot see each other's uncommitted rows, so each independently assigns the
    /// <em>first free</em> room — the same one. That means a race for one professional's slot also
    /// collides on the room, and a race to double-book one patient collides on the room too. More
    /// than one exclusion constraint is genuinely violated, and PostgreSQL reports whichever it
    /// checks first, which is a function of index order rather than of anything this project decides.
    /// </para>
    /// <para>
    /// Pinning a single code here was a flake, and fixing it by pinning harder would have been
    /// asserting an implementation detail of the database. What the concurrency tests are for is the
    /// guarantee — <b>exactly one commits, and the loser is told something true</b>. Which specific
    /// invariant a caller is told about is asserted in the sequential tests, where only one is in
    /// play and the answer is deterministic.
    /// </para>
    /// <para>
    /// Scoped to this test's own appointment type, like every count below. The suite shares one
    /// database and does not reset between tests — isolation comes from each test building its own
    /// uniquely-named clinic — so a global count would pass or fail depending on what ran before it.
    /// </para>
    /// </remarks>
    private async Task AssertExactlyOneWonAsync(
        BookableClinic clinic,
        HttpResponseMessage[] results,
        params string[] permittedCodes)
    {
        var won = results.Count(response => response.StatusCode == HttpStatusCode.OK);
        var lost = results.Where(response => response.StatusCode != HttpStatusCode.OK).ToArray();

        Assert.Equal(1, won);
        Assert.Equal(1, await LiveAppointmentCountAsync(clinic));

        foreach (var loser in lost)
        {
            // The body is reported on failure rather than only the status, because the failure this
            // most often catches is a 500 where a 409 belonged — and the code inside says which
            // layer let go. That is exactly how the deadlock behind design B8's retry was found.
            if (loser.StatusCode != HttpStatusCode.Conflict)
            {
                Assert.Fail($"loser returned {(int)loser.StatusCode}: {await loser.Content.ReadAsStringAsync()}");
            }

            var code = await ClinicBuilder.CodeOf(loser);

            if (!permittedCodes.Contains(code))
            {
                Assert.Fail(
                    $"loser was refused with {code}; this race can legitimately report "
                    + $"{string.Join(" or ", permittedCodes)}");
            }
        }
    }

    private async Task<int> AppointmentCountAsync(BookableClinic clinic)
    {
        var count = 0;

        await fixture.WithDatabaseAsync(async database => count = await database.Appointments
            .CountAsync(appointment => appointment.AppointmentTypeId == clinic.AppointmentTypeId));

        return count;
    }

    private async Task<int> LiveAppointmentCountAsync(BookableClinic clinic)
    {
        var count = 0;

        await fixture.WithDatabaseAsync(async database => count = await database.Appointments
            .CountAsync(appointment => appointment.AppointmentTypeId == clinic.AppointmentTypeId
                && appointment.Status == AppointmentStatus.Scheduled));

        return count;
    }

    private async Task<int> BlockCountAsync(BookableClinic clinic)
    {
        var count = 0;
        var ids = clinic.Professionals.ToList();

        await fixture.WithDatabaseAsync(async database => count = await database.TimeBlocks
            .CountAsync(block => ids.Contains(block.ProfessionalId)));

        return count;
    }

    private async Task<Appointment> SingleAppointmentAsync(BookableClinic clinic)
    {
        Appointment? appointment = null;

        await fixture.WithDatabaseAsync(async database => appointment = await database.Appointments
            .SingleAsync(candidate => candidate.AppointmentTypeId == clinic.AppointmentTypeId));

        return appointment!;
    }

    private async Task<List<Guid>> AppointmentRoomsAsync(BookableClinic clinic)
    {
        var rooms = new List<Guid>();

        await fixture.WithDatabaseAsync(async database => rooms = await database.Appointments
            .Where(appointment => appointment.AppointmentTypeId == clinic.AppointmentTypeId)
            .Select(appointment => appointment.ResourceId)
            .ToListAsync());

        return rooms;
    }

    /// <summary>Removes this clinic's appointments only, leaving the rest of the suite alone.</summary>
    private Task ClearAppointmentsAsync(BookableClinic clinic) =>
        fixture.WithDatabaseAsync(async database => await database.Database.ExecuteSqlAsync(
            $"DELETE FROM appointments WHERE appointment_type_id = {clinic.AppointmentTypeId}"));
}
