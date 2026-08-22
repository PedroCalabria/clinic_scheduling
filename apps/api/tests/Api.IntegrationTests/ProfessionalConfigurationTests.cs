using System.Net;
using System.Text.Json;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// S7's API — a professional's clinical configuration (spec: clinic-configuration).
/// </summary>
/// <remarks>
/// The unit tier proves each rule given a fact. What only this tier proves is that the slice
/// gathers the right fact: the gate's "holds an ACTIVE qualification for THIS appointment
/// type's specialty", and the overlap check's "against ACTIVE segments only". Both are queries
/// whose active-predicate can be on the wrong side while every unit test still passes.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ProfessionalConfigurationTests(ApiFixture fixture)
{
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    // --- Create on first save (E1) ---------------------------------------------------

    [Fact]
    public async Task An_invited_professional_is_listed_before_any_configuration_exists()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var invited = await InviteProfessionalAsync();

        var entry = await FindListEntryAsync(admin, invited);

        Assert.NotNull(entry);
        Assert.False(entry!.Value.GetProperty("isConfigured").GetBoolean());
        Assert.Equal(0, entry.Value.GetProperty("specialtyCount").GetInt32());
    }

    [Fact]
    public async Task The_record_is_created_on_first_save_and_reused_on_the_second()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var invited = await InviteProfessionalAsync();
        var specialtyA = await CreateSpecialtyAsync(admin);
        var specialtyB = await CreateSpecialtyAsync(admin);

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{invited}/specialties", new { specialtyId = specialtyA }));

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{invited}/specialties", new { specialtyId = specialtyB }));

        // One record, two qualifications — not two records.
        await fixture.WithDatabaseAsync(async database =>
        {
            var records = await database.Professionals.Where(p => p.UserId == invited).ToListAsync();

            Assert.Single(records);

            var user = await database.Users.SingleAsync(u => u.Id == invited);

            // Configuring must not touch identity.
            Assert.Equal(Role.Professional, user.Role);
            Assert.Equal(UserStatus.PendingClaim, user.Status);
        });
    }

    [Fact]
    public async Task A_professional_who_never_signed_in_can_still_be_configured()
    {
        // The requirement that ruled out creating the record at first sign-in (fork option ii).
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var invited = await InviteProfessionalAsync();

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(u => u.Id == invited);
            Assert.Null(user.ExternalSubjectId);
        });

        var specialty = await CreateSpecialtyAsync(admin);

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{invited}/specialties", new { specialtyId = specialty }));

        var entry = await FindListEntryAsync(admin, invited);

        Assert.True(entry!.Value.GetProperty("isConfigured").GetBoolean());
        Assert.True(entry.Value.GetProperty("awaitsClaim").GetBoolean());
    }

    [Fact]
    public async Task A_user_who_is_not_a_professional_cannot_be_configured()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var patient = await fixture.SeedUserAsync(Role.Patient);
        var specialty = await CreateSpecialtyAsync(admin);

        var refused = await admin.PostAsync(
            $"/api/config/professionals/{patient.Id}/specialties", new { specialtyId = specialty });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("config.not_found", await CodeOf(refused));

        var missing = await admin.PostAsync(
            $"/api/config/professionals/{Guid.NewGuid()}/specialties", new { specialtyId = specialty });

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_refused_write_leaves_no_orphan_record()
    {
        // ResolveAsync adds the record without saving, so a refusal must roll it back with the
        // rest of the request rather than leaving a configured-but-empty professional behind.
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var invited = await InviteProfessionalAsync();

        var refused = await admin.PostAsync(
            $"/api/config/professionals/{invited}/specialties", new { specialtyId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        await fixture.WithDatabaseAsync(async database =>
            Assert.Empty(await database.Professionals.Where(p => p.UserId == invited).ToListAsync()));
    }

    // --- The qualification gate (E2) -------------------------------------------------

    [Fact]
    public async Task A_duration_inside_a_held_specialty_is_accepted()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(admin.PutAsync(
            $"/api/config/professionals/{setup.UserId}/durations",
            new { appointmentTypeId = setup.AppointmentTypeId, durationMinutes = 40 }));

        var detail = await DetailAsync(admin, setup.UserId);
        var durations = detail.GetProperty("durations").EnumerateArray().ToList();

        Assert.Single(durations);
        Assert.Equal(40, durations[0].GetProperty("durationMinutes").GetInt32());
    }

    [Fact]
    public async Task A_duration_outside_the_held_specialties_is_refused()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        // A second specialty the professional does NOT hold, with its own appointment type.
        var otherSpecialty = await CreateSpecialtyAsync(admin);
        var otherType = await CreateAppointmentTypeAsync(admin, otherSpecialty, setup.ResourceTypeId);

        var refused = await admin.PutAsync(
            $"/api/config/professionals/{setup.UserId}/durations",
            new { appointmentTypeId = otherType, durationMinutes = 40 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("config.specialty_not_held", await CodeOf(refused));
    }

    [Fact]
    public async Task Revoking_a_specialty_is_refused_while_durations_depend_on_it()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(admin.PutAsync(
            $"/api/config/professionals/{setup.UserId}/durations",
            new { appointmentTypeId = setup.AppointmentTypeId, durationMinutes = 40 }));

        var refused = await admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/specialties/{setup.SpecialtyId}/revoke");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.in_use", await CodeOf(refused));

        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("params").GetProperty("records").GetInt32());
    }

    [Fact]
    public async Task Revoking_a_specialty_counts_only_durations_of_that_specialty()
    {
        // The wrong-side-of-the-join case: a duration under specialty B must not block revoking
        // specialty A.
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        var specialtyB = await CreateSpecialtyAsync(admin);
        var typeB = await CreateAppointmentTypeAsync(admin, specialtyB, setup.ResourceTypeId);

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/specialties", new { specialtyId = specialtyB }));

        await AssertNoContent(admin.PutAsync(
            $"/api/config/professionals/{setup.UserId}/durations",
            new { appointmentTypeId = typeB, durationMinutes = 30 }));

        // Specialty A has no durations of its own, so revoking it must succeed.
        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/specialties/{setup.SpecialtyId}/revoke"));
    }

    [Fact]
    public async Task A_cleared_duration_stops_blocking_the_revocation()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(admin.PutAsync(
            $"/api/config/professionals/{setup.UserId}/durations",
            new { appointmentTypeId = setup.AppointmentTypeId, durationMinutes = 40 }));

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/durations/{setup.AppointmentTypeId}/clear"));

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/specialties/{setup.SpecialtyId}/revoke"));
    }

    [Fact]
    public async Task A_duration_must_be_positive()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        var refused = await admin.PutAsync(
            $"/api/config/professionals/{setup.UserId}/durations",
            new { appointmentTypeId = setup.AppointmentTypeId, durationMinutes = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Two_professionals_hold_independent_durations_for_one_type()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var first = await ConfiguredProfessionalAsync(admin);

        var second = await InviteProfessionalAsync();
        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{second}/specialties", new { specialtyId = first.SpecialtyId }));

        await AssertNoContent(admin.PutAsync(
            $"/api/config/professionals/{first.UserId}/durations",
            new { appointmentTypeId = first.AppointmentTypeId, durationMinutes = 40 }));

        await AssertNoContent(admin.PutAsync(
            $"/api/config/professionals/{second}/durations",
            new { appointmentTypeId = first.AppointmentTypeId, durationMinutes = 50 }));

        Assert.Equal(40, await SingleDurationAsync(admin, first.UserId));
        Assert.Equal(50, await SingleDurationAsync(admin, second));
    }

    // --- Working hours: the two-dimensional overlap rule (E5) ------------------------

    [Fact]
    public async Task A_split_day_is_allowed()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(DefineHours(admin, setup.UserId, "Monday", "08:00", "12:00", "2026-01-01", "2026-06-30"));
        await AssertNoContent(DefineHours(admin, setup.UserId, "Monday", "13:00", "17:00", "2026-01-01", "2026-06-30"));

        var detail = await DetailAsync(admin, setup.UserId);
        Assert.Equal(2, detail.GetProperty("workingHours").GetArrayLength());
    }

    [Fact]
    public async Task The_same_hours_in_a_later_period_are_allowed()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(DefineHours(admin, setup.UserId, "Monday", "08:00", "12:00", "2026-01-01", "2026-03-31"));
        await AssertNoContent(DefineHours(admin, setup.UserId, "Monday", "08:00", "12:00", "2026-04-01", "2026-12-31"));
    }

    [Fact]
    public async Task Overlapping_in_both_dimensions_is_refused()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(DefineHours(admin, setup.UserId, "Monday", "08:00", "12:00", "2026-01-01", "2026-06-30"));

        var refused = await DefineHours(admin, setup.UserId, "Monday", "10:00", "14:00", "2026-04-01", "2026-12-31");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.working_hours_overlap", await CodeOf(refused));
    }

    [Fact]
    public async Task A_retired_segment_stops_blocking_new_ones()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(DefineHours(admin, setup.UserId, "Monday", "08:00", "12:00", "2026-01-01", null));

        var detail = await DetailAsync(admin, setup.UserId);
        var segmentId = detail.GetProperty("workingHours")[0].GetProperty("id").GetGuid();

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/working-hours/{segmentId}/retire"));

        // Identical segment, which would have collided a moment ago.
        await AssertNoContent(DefineHours(admin, setup.UserId, "Monday", "08:00", "12:00", "2026-01-01", null));
    }

    [Theory]
    [InlineData("22:00", "02:00")]
    [InlineData("09:00", "09:00")]
    public async Task An_impossible_span_is_refused(string start, string end)
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        var refused = await DefineHours(admin, setup.UserId, "Tuesday", start, end, "2026-01-01", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("config.working_hours_invalid", await CodeOf(refused));
    }

    [Fact]
    public async Task Hours_read_back_as_the_wall_clock_values_entered()
    {
        // The assertion design E3 exists for: no shift, whatever zone the server runs in.
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(DefineHours(admin, setup.UserId, "Wednesday", "08:30", "12:15", "2026-02-01", null));

        var segment = (await DetailAsync(admin, setup.UserId)).GetProperty("workingHours")[0];

        Assert.Equal("08:30", segment.GetProperty("startTime").GetString());
        Assert.Equal("12:15", segment.GetProperty("endTime").GetString());
        Assert.Equal("2026-02-01", segment.GetProperty("effectiveFrom").GetString());
        Assert.Equal("Wednesday", segment.GetProperty("dayOfWeek").GetString());
    }

    [Fact]
    public async Task A_time_carrying_an_offset_is_refused_rather_than_shifted()
    {
        // The specific hole an ISO parser would open: accepting 09:00-03:00 and applying it.
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        var refused = await DefineHours(admin, setup.UserId, "Thursday", "09:00-03:00", "12:00", "2026-01-01", null);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task The_working_hours_columns_are_not_timestamps()
    {
        // Asserted against the live schema, because a value converter added later could quietly
        // change this and every behavioural test would still pass.
        await fixture.WithDatabaseAsync(async database =>
        {
            var offending = await database.Database
                .SqlQuery<string>($"""
                    SELECT table_name || '.' || column_name || ' is ' || data_type AS "Value"
                    FROM information_schema.columns
                    WHERE table_name IN ('working_hours_templates', 'working_hours_exceptions')
                      AND column_name IN ('start_time', 'end_time', 'effective_from', 'effective_to', 'date')
                      AND data_type LIKE 'timestamp%'
                    """)
                .ToListAsync();

            Assert.Empty(offending);
        });
    }

    // --- Exceptions (E4) ------------------------------------------------------------

    [Fact]
    public async Task An_unavailable_day_and_a_replacement_day_are_both_stored()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/exceptions", new { date = "2026-12-25" }));

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/exceptions",
            new { date = "2026-12-24", startTime = "08:00", endTime = "12:00" }));

        var exceptions = (await DetailAsync(admin, setup.UserId)).GetProperty("exceptions")
            .EnumerateArray().ToList();

        Assert.Equal(2, exceptions.Count);
        Assert.Contains(exceptions, e =>
            e.GetProperty("date").GetString() == "2026-12-25"
            && e.GetProperty("startTime").ValueKind == JsonValueKind.Null);
        Assert.Contains(exceptions, e =>
            e.GetProperty("date").GetString() == "2026-12-24"
            && e.GetProperty("startTime").GetString() == "08:00");
    }

    [Fact]
    public async Task A_second_exception_on_the_same_date_is_refused()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/exceptions", new { date = "2026-11-02" }));

        var refused = await admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/exceptions", new { date = "2026-11-02" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.working_hours_overlap", await CodeOf(refused));
    }

    [Fact]
    public async Task An_exception_affects_only_its_own_professional()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var first = await ConfiguredProfessionalAsync(admin);
        var second = await InviteProfessionalAsync();

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{second}/specialties", new { specialtyId = first.SpecialtyId }));

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{first.UserId}/exceptions", new { date = "2026-10-12" }));

        var other = await DetailAsync(admin, second);

        Assert.Equal(0, other.GetProperty("exceptions").GetArrayLength());
    }

    [Fact]
    public async Task An_exception_with_only_one_time_is_refused()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        var refused = await admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/exceptions",
            new { date = "2026-09-01", startTime = "08:00" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    // --- Authorization ---------------------------------------------------------------

    [Fact]
    public async Task Front_desk_cannot_read_or_configure_professionals()
    {
        var (desk, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _desk = desk;

        var read = await desk.GetAsync("/api/config/professionals");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal("auth.forbidden", await CodeOf(read));

        var write = await desk.PostAsync(
            $"/api/config/professionals/{Guid.NewGuid()}/specialties", new { specialtyId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task A_professional_cannot_configure_themselves()
    {
        // Qualification is an administrative decision, not self-service.
        var (professional, user) = await fixture.AsRoleAsync(Role.Professional);
        using var _professional = professional;

        var refused = await professional.GetAsync($"/api/config/professionals/{user.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("auth.forbidden", await CodeOf(refused));
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_differently()
    {
        using var anonymous = fixture.CreateAnonymousClient();

        var refused = await anonymous.GetAsync("/api/config/professionals");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("auth.session_expired", await CodeOf(refused));
    }

    // --- I10 -------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_physically_deleted()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var setup = await ConfiguredProfessionalAsync(admin);

        await AssertNoContent(admin.PutAsync(
            $"/api/config/professionals/{setup.UserId}/durations",
            new { appointmentTypeId = setup.AppointmentTypeId, durationMinutes = 40 }));
        await AssertNoContent(DefineHours(admin, setup.UserId, "Friday", "08:00", "12:00", "2026-01-01", null));
        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/exceptions", new { date = "2026-07-07" }));

        var detail = await DetailAsync(admin, setup.UserId);
        var segmentId = detail.GetProperty("workingHours")[0].GetProperty("id").GetGuid();
        var exceptionId = detail.GetProperty("exceptions")[0].GetProperty("id").GetGuid();

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/durations/{setup.AppointmentTypeId}/clear"));
        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/working-hours/{segmentId}/retire"));
        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/exceptions/{exceptionId}/retire"));
        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{setup.UserId}/specialties/{setup.SpecialtyId}/revoke"));

        await fixture.WithDatabaseAsync(async database =>
        {
            Assert.NotNull(await database.WorkingHoursTemplates.FirstOrDefaultAsync(x => x.Id == segmentId));
            Assert.NotNull(await database.WorkingHoursExceptions.FirstOrDefaultAsync(x => x.Id == exceptionId));

            var record = await database.Professionals.SingleAsync(p => p.UserId == setup.UserId);

            Assert.NotEmpty(await database.ProfessionalSpecialties
                .Where(x => x.ProfessionalId == record.Id).ToListAsync());
            Assert.NotEmpty(await database.ProfessionalAppointmentTypes
                .Where(x => x.ProfessionalId == record.Id).ToListAsync());
        });
    }

    // --- helpers ---------------------------------------------------------------------

    private sealed record Setup(Guid UserId, Guid SpecialtyId, Guid ResourceTypeId, Guid AppointmentTypeId);

    /// <summary>An invited professional holding one specialty, with a type they can be given.</summary>
    private async Task<Setup> ConfiguredProfessionalAsync(TestClient admin)
    {
        var userId = await InviteProfessionalAsync();
        var specialty = await CreateSpecialtyAsync(admin);
        var resourceType = await CreateResourceTypeAsync(admin);
        var appointmentType = await CreateAppointmentTypeAsync(admin, specialty, resourceType);

        await AssertNoContent(admin.PostAsync(
            $"/api/config/professionals/{userId}/specialties", new { specialtyId = specialty }));

        return new Setup(userId, specialty, resourceType, appointmentType);
    }

    private async Task<Guid> InviteProfessionalAsync()
    {
        // Through the fixture rather than S11, so this test class does not depend on change 2's
        // endpoint shape — but the user it creates is the same shape S11 creates.
        var user = await fixture.SeedUserAsync(Role.Professional);

        await fixture.WithDatabaseAsync(async database =>
        {
            // SeedUserAsync claims the invitation; undo that, since "never signed in" is the
            // state several of these tests are about.
            var stored = await database.Users.SingleAsync(u => u.Id == user.Id);
            database.Entry(stored).Property(nameof(User.ExternalSubjectId)).CurrentValue = null;
            database.Entry(stored).Property(nameof(User.Status)).CurrentValue = UserStatus.PendingClaim;
            await database.SaveChangesAsync();
        });

        return user.Id;
    }

    private static Task<HttpResponseMessage> DefineHours(
        TestClient admin,
        Guid userId,
        string day,
        string start,
        string end,
        string from,
        string? to) =>
        admin.PostAsync($"/api/config/professionals/{userId}/working-hours", new
        {
            dayOfWeek = day,
            startTime = start,
            endTime = end,
            effectiveFrom = from,
            effectiveTo = to,
        });

    private static async Task AssertNoContent(Task<HttpResponseMessage> call)
    {
        var response = await call;

        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"expected success, got {(int)response.StatusCode}: {body}");
        }
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("code").GetString();
    }

    private static async Task<JsonElement> DetailAsync(TestClient admin, Guid userId)
    {
        var response = await admin.GetAsync($"/api/config/professionals/{userId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.Clone();
    }

    private static async Task<JsonElement?> FindListEntryAsync(TestClient admin, Guid userId)
    {
        var response = await admin.GetAsync("/api/config/professionals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("userId").GetGuid() == userId)
            {
                return entry.Clone();
            }
        }

        return null;
    }

    private static async Task<int> SingleDurationAsync(TestClient admin, Guid userId)
    {
        var durations = (await DetailAsync(admin, userId)).GetProperty("durations").EnumerateArray().ToList();

        return Assert.Single(durations).GetProperty("durationMinutes").GetInt32();
    }

    private async Task<Guid> CreateSpecialtyAsync(TestClient admin) =>
        await CreatedIdAsync(admin.PostAsync("/api/config/specialties", new { name = Unique("Esp") }));

    private async Task<Guid> CreateResourceTypeAsync(TestClient admin) =>
        await CreatedIdAsync(admin.PostAsync(
            "/api/config/resource-types", new { name = Unique("Tipo"), bufferMinutes = 15 }));

    private async Task<Guid> CreateAppointmentTypeAsync(
        TestClient admin,
        Guid specialtyId,
        Guid requiredResourceTypeId) =>
        await CreatedIdAsync(admin.PostAsync("/api/config/appointment-types", new
        {
            name = Unique("Consulta"),
            specialtyId,
            requiredResourceTypeId,
        }));

    private static async Task<Guid> CreatedIdAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("id").GetGuid();
    }
}
