using System.Net;
using System.Text.Json;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The availability read and internal block time (spec: availability).
/// </summary>
/// <remarks>
/// The unit tier proves the solver decides correctly given facts. What only this tier proves is
/// that the slice gathers the right facts, and every one of them has an active-predicate, a window
/// bound, or an ordering that can be on the wrong side while all 51 unit tests still pass: which
/// professionals are eligible for this appointment type, which blocks fall in this window, and
/// which rooms of the required type are active and in what order.
///
/// It also carries the one assertion neither tier can fake — that blocking time through the API
/// removes exactly the slots it should, which is the whole reason S3 was pulled into this change.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AvailabilityTests(ApiFixture fixture)
{
    private static readonly DateTimeZone Clinic =
        DateTimeZoneProviders.Tzdb[ApiFixture.ClinicTimezoneId];

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>
    /// A week out, so neither the minimum lead time nor the horizon is in play.
    /// </summary>
    /// <remarks>
    /// Relative to now rather than a fixed date, for the reason the seed uses the same trick: a
    /// hard-coded date drifts past the horizon and the test starts asserting emptiness while
    /// looking like it asserts availability.
    /// </remarks>
    private static LocalDate TargetDate() =>
        SystemClock.Instance.GetCurrentInstant().InZone(Clinic).Date.PlusDays(7);

    private static string Iso(LocalDate date) => LocalDatePattern.Iso.Format(date);

    /// <summary>The instant a clinic wall-clock time on a date corresponds to.</summary>
    private static Instant At(LocalDate date, int hour, int minute = 0) =>
        Clinic.AtStrictly(date.At(new LocalTime(hour, minute))).ToInstant();

    private static string Wall(LocalDate date, int hour, int minute = 0) =>
        $"{Iso(date)}T{hour:D2}:{minute:D2}";

    // --- The availability read -------------------------------------------------------

    [Fact]
    public async Task A_configured_professional_yields_slots_stepped_across_their_working_hours()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var response = await patient.GetAsync(Query(clinic));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Decision S: this path is deliberately uncached, and saying so on the wire is what stops
        // an intermediary undoing that. No in-process assertion can see what a proxy does, which
        // is why the validation guide checks the header again in a browser.
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await Body(response);

        Assert.Equal(ApiFixture.ClinicTimezoneId, body.GetProperty("timezone").GetString());

        var slots = Slots(body);

        // 09:00 to 12:00, hour-long visits, the default 15-minute step: starts every quarter hour
        // from 09:00 to 11:00 inclusive.
        Assert.Equal(9, slots.Count);
        Assert.Equal(At(clinic.Date, 9), slots[0].Start);
        Assert.Equal(At(clinic.Date, 10), slots[0].End);
        Assert.Equal(At(clinic.Date, 11), slots[^1].Start);
        Assert.All(slots, slot => Assert.Equal(clinic.ProfessionalId, slot.ProfessionalId));

        // The (professional, resource) pair, resolved server-side. The clinic has one room of the
        // required type, so it is that one — and the loading step's ordering is what makes the
        // choice assertable at all when there are several.
        Assert.All(slots, slot => Assert.Equal(clinic.ResourceId, slot.ResourceId));
    }

    [Fact]
    public async Task Blocking_time_removes_exactly_the_slots_it_overlaps()
    {
        // The round trip this change exists to make demonstrable: a real producer writing real
        // rows, and the read reflecting them.
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var before = Slots(await Body(await patient.GetAsync(Query(clinic))));

        Assert.Equal(9, before.Count);

        using var professional = await fixture.AsUserAsync(clinic.User);

        var created = await professional.PostAsync("/api/blocks", new
        {
            startsAt = Wall(clinic.Date, 10),
            endsAt = Wall(clinic.Date, 11),
        });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var after = Slots(await Body(await patient.GetAsync(Query(clinic))));

        // Only the two slots that merely TOUCH the block survive: 09:00-10:00 ends as it begins
        // and 11:00-12:00 begins as it ends. Everything between genuinely overlaps.
        Assert.Equal(
            [At(clinic.Date, 9), At(clinic.Date, 11)],
            after.Select(slot => slot.Start));
    }

    [Fact]
    public async Task Retiring_a_block_offers_its_slots_again()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        using var professional = await fixture.AsUserAsync(clinic.User);

        var created = await Body(await professional.PostAsync("/api/blocks", new
        {
            startsAt = Wall(clinic.Date, 10),
            endsAt = Wall(clinic.Date, 11),
        }));

        var blockId = created.GetProperty("id").GetGuid();

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        Assert.Equal(2, Slots(await Body(await patient.GetAsync(Query(clinic)))).Count);

        var retired = await professional.PostAsync($"/api/blocks/{blockId}/retire");

        Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);

        // Soft-delete, so the block still exists — and stops subtracting, which is the property
        // the active predicate on TimeBlock.BusyIntervalsOf carries.
        Assert.Equal(9, Slots(await Body(await patient.GetAsync(Query(clinic)))).Count);
    }

    [Fact]
    public async Task An_unqualified_professional_never_appears_in_the_answer()
    {
        // The eligibility join, which is the fact this tier exists to check. The second
        // professional has working hours and is perfectly free — they simply hold no duration for
        // this kind of visit.
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var other = await fixture.SeedUserAsync(Role.Professional);

        await Succeeds(admin.PostAsync(
            $"/api/config/professionals/{other.Id}/specialties",
            new { specialtyId = clinic.SpecialtyId }));

        await Succeeds(admin.PostAsync(
            $"/api/config/professionals/{other.Id}/working-hours",
            new
            {
                dayOfWeek = clinic.Date.DayOfWeek.ToString(),
                startTime = "09:00",
                endTime = "12:00",
                effectiveFrom = Iso(clinic.Date.PlusDays(-30)),
                effectiveTo = (string?)null,
            }));

        var slots = Slots(await Body(await admin.GetAsync(Query(clinic))));

        Assert.NotEmpty(slots);
        Assert.All(slots, slot => Assert.Equal(clinic.ProfessionalId, slot.ProfessionalId));
    }

    [Fact]
    public async Task No_active_resource_of_the_required_type_means_no_slots()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        Assert.NotEmpty(Slots(await Body(await admin.GetAsync(Query(clinic)))));

        await Succeeds(admin.PostAsync($"/api/config/resources/{clinic.ResourceId}/deactivate"));

        // However free the professional is, the visit needs a room that exists.
        Assert.Empty(Slots(await Body(await admin.GetAsync(Query(clinic)))));
    }

    [Fact]
    public async Task Specific_and_any_professional_modes_agree_over_the_same_data()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var union = Slots(await Body(await admin.GetAsync(Query(clinic))));
        var specific = Slots(await Body(await admin.GetAsync(
            $"{Query(clinic)}&professionalId={clinic.ProfessionalId}")));

        // There is one solver, so the specific answer must be exactly the subset the union holds.
        Assert.Equal(
            union.Where(slot => slot.ProfessionalId == clinic.ProfessionalId).ToList(),
            specific);
    }

    [Fact]
    public async Task A_specific_professional_who_is_not_qualified_is_an_empty_answer_not_an_error()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var other = await fixture.SeedUserAsync(Role.Professional);

        await Succeeds(admin.PostAsync(
            $"/api/config/professionals/{other.Id}/specialties",
            new { specialtyId = clinic.SpecialtyId }));

        var otherProfessionalId = await ProfessionalIdAsync(other.Id);

        var response = await admin.GetAsync($"{Query(clinic)}&professionalId={otherProfessionalId}");

        // A well-formed question with no answer, distinct from a reference that does not resolve.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Slots(await Body(response)));
    }

    [Fact]
    public async Task The_room_named_is_the_first_by_name_and_is_stable_across_requests()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        // A second room of the same type whose name sorts BEFORE the seeded one, so a passing
        // assertion means the ordering is real rather than incidentally the insertion order.
        var firstByName = await CreateAsync(admin, "/api/config/resources", new
        {
            name = "AAA-" + Guid.NewGuid().ToString("N"),
            resourceTypeId = clinic.ResourceTypeId,
        });

        var once = Slots(await Body(await admin.GetAsync(Query(clinic))));
        var twice = Slots(await Body(await admin.GetAsync(Query(clinic))));

        Assert.NotEmpty(once);
        Assert.All(once, slot => Assert.Equal(firstByName, slot.ResourceId));

        // Stable, not merely plausible: an unordered query can return either room per request, and
        // a client comparing two answers would see the pairing flap for no reason.
        Assert.Equal(once, twice);
    }

    // --- Window and reference validation ---------------------------------------------

    [Fact]
    public async Task A_reversed_or_oversized_or_malformed_window_is_refused()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var reversed = await admin.GetAsync(
            $"/api/availability?appointmentTypeId={clinic.AppointmentTypeId}"
            + $"&from={Iso(clinic.Date)}&to={Iso(clinic.Date.PlusDays(-1))}");

        await AssertRefusal(reversed, HttpStatusCode.BadRequest, "availability.window_invalid");

        var oversized = await admin.GetAsync(
            $"/api/availability?appointmentTypeId={clinic.AppointmentTypeId}"
            + $"&from={Iso(clinic.Date)}&to={Iso(clinic.Date.PlusDays(60))}");

        await AssertRefusal(oversized, HttpStatusCode.BadRequest, "availability.window_invalid");

        // A date the pattern refuses, including one carrying an offset — which would otherwise
        // let a caller decide which zone the clinic's day meant.
        foreach (var bad in new[] { "not-a-date", "2026-13-01", "2026-08-24T00:00:00Z" })
        {
            var malformed = await admin.GetAsync(
                $"/api/availability?appointmentTypeId={clinic.AppointmentTypeId}"
                + $"&from={bad}&to={Iso(clinic.Date)}");

            await AssertRefusal(malformed, HttpStatusCode.BadRequest, "availability.window_invalid");
        }
    }

    [Fact]
    public async Task An_unknown_appointment_type_or_professional_is_not_found()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var unknownType = await admin.GetAsync(
            $"/api/availability?appointmentTypeId={Guid.NewGuid()}"
            + $"&from={Iso(clinic.Date)}&to={Iso(clinic.Date)}");

        await AssertRefusal(unknownType, HttpStatusCode.NotFound, "config.not_found");

        var unknownProfessional = await admin.GetAsync($"{Query(clinic)}&professionalId={Guid.NewGuid()}");

        await AssertRefusal(unknownProfessional, HttpStatusCode.NotFound, "config.not_found");

        // "Exists but retired" is the same answer as "does not exist", per design D5.
        await Succeeds(admin.PostAsync(
            $"/api/config/appointment-types/{clinic.AppointmentTypeId}/deactivate"));

        var retiredType = await admin.GetAsync(Query(clinic));

        await AssertRefusal(retiredType, HttpStatusCode.NotFound, "config.not_found");
    }

    [Fact]
    public async Task A_missing_appointment_type_names_the_field()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var response = await admin.GetAsync(
            $"/api/availability?from={Iso(clinic.Date)}&to={Iso(clinic.Date)}");

        await AssertRefusal(response, HttpStatusCode.BadRequest, "validation.required");
    }

    // --- Authorization (F11) ---------------------------------------------------------

    [Fact]
    public async Task Every_authenticated_role_may_read_availability()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        foreach (var role in new[] { Role.Patient, Role.FrontDesk, Role.Administrator, Role.Professional })
        {
            var (client, _) = await fixture.AsRoleAsync(role);
            using var _client = client;

            var response = await client.GetAsync(Query(clinic));

            // Availability exposes free time, never patient data, and every role has a
            // legitimate reason to ask.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task An_unauthenticated_availability_request_is_refused()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        using var anonymous = fixture.CreateClient();

        var response = await anonymous.GetAsync(Query(clinic));

        await AssertRefusal(response, HttpStatusCode.Unauthorized, "auth.session_expired");
    }

    [Fact]
    public async Task Availability_is_rate_limited_per_caller()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 12, durationMinutes: 60);

        // A second host with a budget of one, rather than 61 real requests against the shared
        // one — the pattern the fixture exists for.
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Scheduling:AvailabilityRequestsPerMinute"] = "1",
        });

        var user = await fixture.SeedUserAsync(Role.Patient);
        var token = await ApiFixture.IssueSessionOnAsync(host, user);

        using var client = fixture.CreateClientFor(host, token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Query(clinic))).StatusCode);

        var refused = await client.GetAsync(Query(clinic));

        // One code, shared with the login limiter: the caller sees the same failure and has the
        // same remedy, and 07-error-codes.md's rule is one code per user-meaningful failure.
        await AssertRefusal(refused, HttpStatusCode.TooManyRequests, "auth.rate_limited");
    }

    // --- Blocks: ownership and refusals (F11) ----------------------------------------

    [Fact]
    public async Task A_professional_manages_their_own_blocks_end_to_end()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 17, durationMinutes: 60);

        using var professional = await fixture.AsUserAsync(clinic.User);

        var created = await Body(await professional.PostAsync("/api/blocks", new
        {
            startsAt = Wall(clinic.Date, 10),
            endsAt = Wall(clinic.Date, 11),
        }));

        var blockId = created.GetProperty("id").GetGuid();

        // Wall clock in, wall clock out, and the value read back is the value entered.
        Assert.Equal(Wall(clinic.Date, 10), created.GetProperty("startsAt").GetString());
        Assert.True(created.GetProperty("isActive").GetBoolean());

        var moved = await Body(await professional.PutAsync($"/api/blocks/{blockId}", new
        {
            startsAt = Wall(clinic.Date, 14),
            endsAt = Wall(clinic.Date, 15, 30),
        }));

        Assert.Equal(Wall(clinic.Date, 14), moved.GetProperty("startsAt").GetString());
        Assert.Equal(Wall(clinic.Date, 15, 30), moved.GetProperty("endsAt").GetString());

        var listed = await Body(await professional.GetAsync("/api/blocks"));

        Assert.Equal(ApiFixture.ClinicTimezoneId, listed.GetProperty("timezone").GetString());
        Assert.Single(listed.GetProperty("blocks").EnumerateArray());

        await Succeeds(professional.PostAsync($"/api/blocks/{blockId}/retire"));
        await Succeeds(professional.PostAsync($"/api/blocks/{blockId}/restore"));

        var restored = await Body(await professional.GetAsync("/api/blocks"));

        Assert.True(restored.GetProperty("blocks")[0].GetProperty("isActive").GetBoolean());
    }

    [Theory]
    [InlineData(11, 0, 10, 0)]
    [InlineData(10, 0, 10, 0)]
    public async Task A_range_that_does_not_move_forward_is_refused(
        int startHour,
        int startMinute,
        int endHour,
        int endMinute)
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 17, durationMinutes: 60);

        using var professional = await fixture.AsUserAsync(clinic.User);

        var refused = await professional.PostAsync("/api/blocks", new
        {
            startsAt = Wall(clinic.Date, startHour, startMinute),
            endsAt = Wall(clinic.Date, endHour, endMinute),
        });

        await AssertRefusal(refused, HttpStatusCode.UnprocessableEntity, "block.invalid_range");

        await fixture.WithDatabaseAsync(async database =>
            Assert.Empty(await database.TimeBlocks
                .Where(block => block.ProfessionalId == clinic.ProfessionalId)
                .ToListAsync()));
    }

    [Fact]
    public async Task A_refused_edit_leaves_the_stored_range_untouched()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 17, durationMinutes: 60);

        using var professional = await fixture.AsUserAsync(clinic.User);

        var created = await Body(await professional.PostAsync("/api/blocks", new
        {
            startsAt = Wall(clinic.Date, 10),
            endsAt = Wall(clinic.Date, 11),
        }));

        var blockId = created.GetProperty("id").GetGuid();

        var refused = await professional.PutAsync($"/api/blocks/{blockId}", new
        {
            startsAt = Wall(clinic.Date, 12),
            endsAt = Wall(clinic.Date, 12),
        });

        await AssertRefusal(refused, HttpStatusCode.UnprocessableEntity, "block.invalid_range");

        // What the screen relies on to keep showing the truth after a refusal.
        var listed = await Body(await professional.GetAsync("/api/blocks"));

        Assert.Equal(Wall(clinic.Date, 10), listed.GetProperty("blocks")[0].GetProperty("startsAt").GetString());
    }

    [Fact]
    public async Task A_professional_cannot_touch_another_professionals_block()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 17, durationMinutes: 60);

        using var owner = await fixture.AsUserAsync(clinic.User);

        var created = await Body(await owner.PostAsync("/api/blocks", new
        {
            startsAt = Wall(clinic.Date, 10),
            endsAt = Wall(clinic.Date, 11),
        }));

        var blockId = created.GetProperty("id").GetGuid();

        // A second professional who is configured, so the refusal is genuinely about ownership
        // rather than about having no record.
        var intruderClinic = await BuildClinicAsync(startHour: 9, endHour: 17, durationMinutes: 60);
        using var intruder = await fixture.AsUserAsync(intruderClinic.User);

        var edit = await intruder.PutAsync($"/api/blocks/{blockId}", new
        {
            startsAt = Wall(clinic.Date, 15),
            endsAt = Wall(clinic.Date, 16),
        });

        await AssertRefusal(edit, HttpStatusCode.Forbidden, "auth.ownership_denied");

        var retire = await intruder.PostAsync($"/api/blocks/{blockId}/retire");

        await AssertRefusal(retire, HttpStatusCode.Forbidden, "auth.ownership_denied");

        // Ownership refused, not role refused: this caller may manage blocks, just not this one.
        await fixture.WithDatabaseAsync(async database =>
        {
            var stored = await database.TimeBlocks.SingleAsync(block => block.Id == blockId);

            Assert.Equal(At(clinic.Date, 10), stored.StartsAt);
            Assert.True(stored.IsActive);
        });
    }

    [Fact]
    public async Task A_new_block_carries_no_way_to_name_another_owner()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 17, durationMinutes: 60);
        var victim = await BuildClinicAsync(startHour: 9, endHour: 17, durationMinutes: 60);

        using var professional = await fixture.AsUserAsync(clinic.User);

        // Sending one anyway: the contract has no such field, so it is ignored rather than
        // honoured, and the block belongs to the caller. That is what makes the guarantee
        // structural instead of a check somebody has to remember (design F11).
        var created = await Body(await professional.PostAsync("/api/blocks", new
        {
            startsAt = Wall(clinic.Date, 10),
            endsAt = Wall(clinic.Date, 11),
            professionalId = victim.ProfessionalId,
        }));

        await fixture.WithDatabaseAsync(async database =>
        {
            var stored = await database.TimeBlocks.SingleAsync(
                block => block.Id == created.GetProperty("id").GetGuid());

            Assert.Equal(clinic.ProfessionalId, stored.ProfessionalId);
        });
    }

    [Theory]
    [InlineData(Role.Administrator)]
    [InlineData(Role.FrontDesk)]
    [InlineData(Role.Patient)]
    public async Task Only_professionals_may_reach_the_blocks_endpoints(Role role)
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 17, durationMinutes: 60);

        var (client, _) = await fixture.AsRoleAsync(role);
        using var _client = client;

        // An administrator owns qualification and a professional owns their own time. The mirror
        // of 3b refusing to let a professional configure themselves.
        await AssertRefusal(await client.GetAsync("/api/blocks"), HttpStatusCode.Forbidden, "auth.forbidden");

        await AssertRefusal(
            await client.PostAsync("/api/blocks", new
            {
                startsAt = Wall(clinic.Date, 10),
                endsAt = Wall(clinic.Date, 11),
            }),
            HttpStatusCode.Forbidden,
            "auth.forbidden");
    }

    [Fact]
    public async Task A_professional_with_no_configuration_has_no_schedule_to_block()
    {
        // Real state, not an edge case: change 2 invites, and 3b's S7 creates the record on the
        // administrator's first save. A claimed invitation can sit in between.
        var unconfigured = await fixture.SeedUserAsync(Role.Professional);

        using var professional = await fixture.AsUserAsync(unconfigured);

        var refused = await professional.GetAsync("/api/blocks");

        await AssertRefusal(refused, HttpStatusCode.NotFound, "config.not_found");
    }

    [Fact]
    public async Task Reading_or_writing_a_block_records_no_patient_access()
    {
        var clinic = await BuildClinicAsync(startHour: 9, endHour: 17, durationMinutes: 60);

        var before = await AccessLogCountAsync();

        using var professional = await fixture.AsUserAsync(clinic.User);

        await Succeeds(professional.PostAsync("/api/blocks", new
        {
            startsAt = Wall(clinic.Date, 10),
            endsAt = Wall(clinic.Date, 11),
        }));

        await Succeeds(professional.GetAsync("/api/blocks"));

        // The audit trail exists so a patient can be told who read their data. Widening it to
        // cover a doctor reading their own diary would dilute an audit whose value is its
        // narrowness.
        Assert.Equal(before, await AccessLogCountAsync());
    }

    // --- Schema (design F9, 00-context.md §5) ----------------------------------------

    [Fact]
    public async Task A_blocks_range_columns_are_timestamps_with_a_timezone()
    {
        // The exact inverse of the assertion professional-configuration added for its wall-clock
        // columns. Both directions of §5 now have a test: a rule must never become a timestamp,
        // and an event must never become a bare time. Asserted against the live schema, because a
        // value converter added later could quietly change it while every behavioural test passed.
        await fixture.WithDatabaseAsync(async database =>
        {
            var types = await database.Database
                .SqlQuery<string>($"""
                    SELECT column_name || ' is ' || data_type AS "Value"
                    FROM information_schema.columns
                    WHERE table_name = 'time_blocks'
                      AND column_name IN ('starts_at_utc', 'ends_at_utc')
                    ORDER BY column_name
                    """)
                .ToListAsync();

            Assert.Equal(
                ["ends_at_utc is timestamp with time zone", "starts_at_utc is timestamp with time zone"],
                types);
        });
    }

    // --- Fixtures --------------------------------------------------------------------

    private sealed record SeededClinic(
        User User,
        Guid ProfessionalId,
        Guid SpecialtyId,
        Guid ResourceTypeId,
        Guid ResourceId,
        Guid AppointmentTypeId,
        LocalDate Date);

    /// <summary>
    /// A clinic that can actually answer an availability question, built through the admin API.
    /// </summary>
    /// <remarks>
    /// Through the endpoints rather than by writing rows, for the same reason the dev seed goes
    /// through the domain factories: a fixture that bypasses a rule can set up a state the product
    /// cannot reach, and then the test proves something about nothing.
    /// </remarks>
    private async Task<SeededClinic> BuildClinicAsync(int startHour, int endHour, int durationMinutes)
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var date = TargetDate();

        var specialty = await CreateAsync(admin, "/api/config/specialties", new { name = Unique("Specialty") });

        var resourceType = await CreateAsync(
            admin, "/api/config/resource-types", new { name = Unique("Room"), bufferMinutes = 15 });

        var resource = await CreateAsync(
            admin, "/api/config/resources", new { name = Unique("Room 1"), resourceTypeId = resourceType });

        var appointmentType = await CreateAsync(admin, "/api/config/appointment-types", new
        {
            name = Unique("Visit"),
            specialtyId = specialty,
            requiredResourceTypeId = resourceType,
        });

        var user = await fixture.SeedUserAsync(Role.Professional);

        await Succeeds(admin.PostAsync(
            $"/api/config/professionals/{user.Id}/specialties", new { specialtyId = specialty }));

        await Succeeds(admin.PutAsync(
            $"/api/config/professionals/{user.Id}/durations",
            new { appointmentTypeId = appointmentType, durationMinutes }));

        await Succeeds(admin.PostAsync(
            $"/api/config/professionals/{user.Id}/working-hours",
            new
            {
                dayOfWeek = date.DayOfWeek.ToString(),
                startTime = $"{startHour:D2}:00",
                endTime = $"{endHour:D2}:00",

                // Already in force, and open-ended, so the effective-date dimension is satisfied
                // rather than being what the test is about.
                effectiveFrom = Iso(date.PlusDays(-30)),
                effectiveTo = (string?)null,
            }));

        return new SeededClinic(
            user,
            await ProfessionalIdAsync(user.Id),
            specialty,
            resourceType,
            resource,
            appointmentType,
            date);
    }

    private static string Query(SeededClinic clinic) =>
        $"/api/availability?appointmentTypeId={clinic.AppointmentTypeId}"
        + $"&from={Iso(clinic.Date)}&to={Iso(clinic.Date)}";

    private async Task<Guid> ProfessionalIdAsync(Guid userId)
    {
        var id = Guid.Empty;

        await fixture.WithDatabaseAsync(async database =>
            id = await database.Professionals
                .Where(professional => professional.UserId == userId && professional.DeactivatedAtUtc == null)
                .Select(professional => professional.Id)
                .SingleAsync());

        return id;
    }

    private async Task<int> AccessLogCountAsync()
    {
        var count = 0;

        await fixture.WithDatabaseAsync(async database => count = await database.AccessLog.CountAsync());

        return count;
    }

    private static async Task<Guid> CreateAsync(TestClient client, string url, object body)
    {
        var response = await client.PostAsync(url, body);

        await Succeeds(Task.FromResult(response));

        return (await Body(response)).GetProperty("id").GetGuid();
    }

    private static async Task Succeeds(Task<HttpResponseMessage> call)
    {
        var response = await call;

        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.Created))
        {
            Assert.Fail($"expected success, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertRefusal(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private sealed record Slot(Guid ProfessionalId, Guid ResourceId, Instant Start, Instant End);

    private static List<Slot> Slots(JsonElement body) =>
        body.GetProperty("slots")
            .EnumerateArray()
            .Select(slot => new Slot(
                slot.GetProperty("professionalId").GetGuid(),
                slot.GetProperty("resourceId").GetGuid(),
                InstantPattern.ExtendedIso.Parse(slot.GetProperty("start").GetString()!).Value,
                InstantPattern.ExtendedIso.Parse(slot.GetProperty("end").GetString()!).Value))
            .ToList();
}
