using System.Net;
using System.Text.Json;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The catalog's API — S8-S10 (spec: clinic-configuration, catalog half).
/// </summary>
/// <remarks>
/// <para>
/// The unit tier already proves the rules given a fact. What only this tier can prove is that
/// the slices <em>obtain</em> the right fact: "active" is a predicate on the dependent, not on
/// the target, and getting that backwards produces a rule that fires on retired records and
/// refuses retirements it should permit. <see cref="A_reference_held_only_by_a_deactivated_record_does_not_block"/>
/// is the assertion that catches it.
/// </para>
/// <para>
/// Names carry a GUID suffix throughout. The fixture shares one database across the collection
/// and does not reset between tests, so a fixed name would make these tests pass or fail
/// depending on what ran before them — and the rule under test is precisely about name
/// collisions.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CatalogTests(ApiFixture fixture)
{
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    // --- 7.1 The three in-use refusals ----------------------------------------------

    [Fact]
    public async Task A_specialty_used_by_an_active_appointment_type_cannot_be_deactivated()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await CreateSpecialtyAsync(admin);
        var resourceType = await CreateResourceTypeAsync(admin);
        await CreateAppointmentTypeAsync(admin, specialty, resourceType);

        var refused = await admin.PostAsync($"/api/config/specialties/{specialty}/deactivate");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.in_use", await CodeOf(refused));

        // The refusal names how much is in the way, so the screen can say "1 active record"
        // rather than only "something". Resolves the design's first open question.
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("params").GetProperty("records").GetInt32());

        // And it really is still offered, not merely reported as refused.
        Assert.True(await IsActiveAsync(admin, "specialties", specialty));
    }

    [Fact]
    public async Task A_resource_type_holding_an_active_resource_cannot_be_deactivated()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var resourceType = await CreateResourceTypeAsync(admin);
        await CreateResourceAsync(admin, resourceType);

        var refused = await admin.PostAsync($"/api/config/resource-types/{resourceType}/deactivate");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.in_use", await CodeOf(refused));
    }

    [Fact]
    public async Task A_resource_type_required_by_an_active_appointment_type_cannot_be_deactivated()
    {
        // The second dependent kind, with no resources of the type at all — the half a
        // single-count check would silently have let through.
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await CreateSpecialtyAsync(admin);
        var resourceType = await CreateResourceTypeAsync(admin);
        await CreateAppointmentTypeAsync(admin, specialty, resourceType);

        var refused = await admin.PostAsync($"/api/config/resource-types/{resourceType}/deactivate");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.in_use", await CodeOf(refused));
    }

    // --- 7.2 The predicate is on the dependent --------------------------------------

    [Fact]
    public async Task A_reference_held_only_by_a_deactivated_record_does_not_block()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await CreateSpecialtyAsync(admin);
        var resourceType = await CreateResourceTypeAsync(admin);
        var appointmentType = await CreateAppointmentTypeAsync(admin, specialty, resourceType);

        // Retire the only dependent first.
        var retired = await admin.PostAsync($"/api/config/appointment-types/{appointmentType}/deactivate");
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        // Now the specialty has no ACTIVE dependents, so retiring it must succeed.
        var deactivated = await admin.PostAsync($"/api/config/specialties/{specialty}/deactivate");

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.False(await IsActiveAsync(admin, "specialties", specialty));
    }

    // --- 7.3 Name uniqueness among active records -----------------------------------

    [Fact]
    public async Task A_name_an_active_specialty_holds_is_refused()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var name = Unique("Cardiologia");
        await CreateSpecialtyAsync(admin, name);

        var refused = await admin.PostAsync("/api/config/specialties", new { name });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.duplicate_name", await CodeOf(refused));
    }

    [Fact]
    public async Task Uniqueness_is_case_insensitive()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var name = Unique("Dermatologia");
        await CreateSpecialtyAsync(admin, name);

        var refused = await admin.PostAsync("/api/config/specialties", new { name = name.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.duplicate_name", await CodeOf(refused));
    }

    [Fact]
    public async Task Uniqueness_is_scoped_to_one_kind_of_entity()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var name = Unique("Ultrassom");
        await CreateSpecialtyAsync(admin, name);

        // A resource type may hold a name a specialty holds: they are different kinds.
        var created = await admin.PostAsync("/api/config/resource-types", new { name, bufferMinutes = 10 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Deactivation_frees_the_name_for_reuse()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var name = Unique("Pediatria");
        var original = await CreateSpecialtyAsync(admin, name);

        await admin.PostAsync($"/api/config/specialties/{original}/deactivate");

        var recreated = await admin.PostAsync("/api/config/specialties", new { name });

        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);

        // Both rows exist — the retired one was never removed (I10).
        Assert.NotNull(await FindAsync(admin, "specialties", original));
    }

    [Fact]
    public async Task Renaming_onto_an_active_name_is_refused_but_renaming_itself_is_allowed()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var taken = Unique("Ortopedia");
        await CreateSpecialtyAsync(admin, taken);

        var mine = Unique("Neurologia");
        var id = await CreateSpecialtyAsync(admin, mine);

        var refused = await admin.PutAsync($"/api/config/specialties/{id}", new { name = taken });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.duplicate_name", await CodeOf(refused));

        // Renaming to its own current name must not collide with itself.
        var unchanged = await admin.PutAsync($"/api/config/specialties/{id}", new { name = mine });
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
    }

    // --- 7.4 Reactivation ------------------------------------------------------------

    [Fact]
    public async Task A_deactivated_specialty_reactivates()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await CreateSpecialtyAsync(admin);
        await admin.PostAsync($"/api/config/specialties/{specialty}/deactivate");

        var reactivated = await admin.PostAsync($"/api/config/specialties/{specialty}/reactivate");

        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.True(await IsActiveAsync(admin, "specialties", specialty));
    }

    [Fact]
    public async Task Reactivation_is_refused_when_the_name_was_taken_since()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var name = Unique("Oftalmologia");
        var original = await CreateSpecialtyAsync(admin, name);

        await admin.PostAsync($"/api/config/specialties/{original}/deactivate");
        await CreateSpecialtyAsync(admin, name);

        var refused = await admin.PostAsync($"/api/config/specialties/{original}/reactivate");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("config.duplicate_name", await CodeOf(refused));
        Assert.False(await IsActiveAsync(admin, "specialties", original));
    }

    [Fact]
    public async Task Reactivation_cannot_resurrect_an_appointment_type_onto_an_inactive_specialty()
    {
        // Design D5's back door, walked end to end: retire the dependent, then retire the
        // reference (now legally unreferenced), then try to restore the dependent.
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await CreateSpecialtyAsync(admin);
        var resourceType = await CreateResourceTypeAsync(admin);
        var appointmentType = await CreateAppointmentTypeAsync(admin, specialty, resourceType);

        await admin.PostAsync($"/api/config/appointment-types/{appointmentType}/deactivate");
        var specialtyRetired = await admin.PostAsync($"/api/config/specialties/{specialty}/deactivate");
        Assert.Equal(HttpStatusCode.OK, specialtyRetired.StatusCode);

        var refused = await admin.PostAsync($"/api/config/appointment-types/{appointmentType}/reactivate");

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("config.not_found", await CodeOf(refused));
        Assert.False(await IsActiveAsync(admin, "appointment-types", appointmentType));
    }

    [Fact]
    public async Task Reactivation_cannot_resurrect_a_resource_onto_an_inactive_type()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var resourceType = await CreateResourceTypeAsync(admin);
        var resource = await CreateResourceAsync(admin, resourceType);

        await admin.PostAsync($"/api/config/resources/{resource}/deactivate");
        await admin.PostAsync($"/api/config/resource-types/{resourceType}/deactivate");

        var refused = await admin.PostAsync($"/api/config/resources/{resource}/reactivate");

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("config.not_found", await CodeOf(refused));
    }

    [Fact]
    public async Task An_appointment_type_cannot_be_created_against_an_inactive_specialty()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await CreateSpecialtyAsync(admin);
        var resourceType = await CreateResourceTypeAsync(admin);
        await admin.PostAsync($"/api/config/specialties/{specialty}/deactivate");

        var refused = await admin.PostAsync("/api/config/appointment-types", new
        {
            name = Unique("Consulta"),
            specialtyId = specialty,
            requiredResourceTypeId = resourceType,
        });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("config.not_found", await CodeOf(refused));
    }

    // --- 7.5 The authorization boundary ----------------------------------------------

    [Fact]
    public async Task Front_desk_cannot_write_the_catalog()
    {
        var (desk, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _desk = desk;

        var refused = await desk.PostAsync("/api/config/specialties", new { name = Unique("Cardiologia") });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("auth.forbidden", await CodeOf(refused));
    }

    [Fact]
    public async Task Front_desk_cannot_read_the_catalog_even_though_the_navigation_hides_it()
    {
        // Hiding a navigation entry is an affordance, never the boundary.
        var (desk, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _desk = desk;

        var refused = await desk.GetAsync("/api/config/specialties");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("auth.forbidden", await CodeOf(refused));
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_differently_from_a_forbidden_one()
    {
        using var anonymous = fixture.CreateAnonymousClient();

        var refused = await anonymous.GetAsync("/api/config/specialties");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("auth.session_expired", await CodeOf(refused));
    }

    [Fact]
    public async Task A_patient_cannot_reach_the_catalog()
    {
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var refused = await patient.GetAsync("/api/config/resource-types");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task An_administrator_configures_the_whole_catalog_end_to_end()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await CreateSpecialtyAsync(admin);
        var resourceType = await CreateResourceTypeAsync(admin, bufferMinutes: 15);
        var resource = await CreateResourceAsync(admin, resourceType);
        var appointmentType = await CreateAppointmentTypeAsync(admin, specialty, resourceType);

        // The links resolve, and the buffer survived the round trip — the value change 4 reads.
        var types = await admin.GetAsync("/api/config/appointment-types");
        using var listed = JsonDocument.Parse(await types.Content.ReadAsStringAsync());

        var mine = listed.RootElement.EnumerateArray()
            .Single(entry => entry.GetProperty("id").GetGuid() == appointmentType);

        Assert.Equal(specialty, mine.GetProperty("specialtyId").GetGuid());
        Assert.Equal(resourceType, mine.GetProperty("requiredResourceTypeId").GetGuid());
        Assert.True(mine.GetProperty("isActive").GetBoolean());

        var resourceTypes = await admin.GetAsync("/api/config/resource-types");
        using var typeList = JsonDocument.Parse(await resourceTypes.Content.ReadAsStringAsync());
        var storedType = typeList.RootElement.EnumerateArray()
            .Single(entry => entry.GetProperty("id").GetGuid() == resourceType);

        Assert.Equal(15, storedType.GetProperty("bufferMinutes").GetInt32());

        var resources = await admin.GetAsync("/api/config/resources");
        using var resourceList = JsonDocument.Parse(await resources.Content.ReadAsStringAsync());
        Assert.Contains(
            resourceList.RootElement.EnumerateArray(),
            entry => entry.GetProperty("id").GetGuid() == resource);
    }

    // --- 7.6 Not-found and validation ------------------------------------------------

    [Fact]
    public async Task Acting_on_a_specialty_that_does_not_exist_is_config_not_found()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var missing = Guid.NewGuid();

        var deactivate = await admin.PostAsync($"/api/config/specialties/{missing}/deactivate");
        Assert.Equal(HttpStatusCode.NotFound, deactivate.StatusCode);
        Assert.Equal("config.not_found", await CodeOf(deactivate));

        var rename = await admin.PutAsync($"/api/config/specialties/{missing}", new { name = Unique("X") });
        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
        Assert.Equal("config.not_found", await CodeOf(rename));
    }

    [Fact]
    public async Task A_negative_turnaround_buffer_is_refused_by_the_validation_contract()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var refused = await admin.PostAsync(
            "/api/config/resource-types",
            new { name = Unique("Consultório"), bufferMinutes = -5 });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("validation.invalid_format", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("bufferMinutes", body.RootElement.GetProperty("params").GetProperty("field").GetString());
    }

    [Fact]
    public async Task A_missing_name_is_refused_as_required()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var refused = await admin.PostAsync("/api/config/specialties", new { name = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("validation.required", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_resource_cannot_be_created_against_an_inactive_type()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var resourceType = await CreateResourceTypeAsync(admin);
        await admin.PostAsync($"/api/config/resource-types/{resourceType}/deactivate");

        var refused = await admin.PostAsync(
            "/api/config/resources",
            new { name = Unique("Sala"), resourceTypeId = resourceType });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("config.not_found", await CodeOf(refused));
    }

    // --- 7.7 Nothing is ever physically removed (I10) --------------------------------

    [Fact]
    public async Task Deactivation_retains_the_row_rather_than_deleting_it()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await CreateSpecialtyAsync(admin);
        var resourceType = await CreateResourceTypeAsync(admin);
        var resource = await CreateResourceAsync(admin, resourceType);
        var appointmentType = await CreateAppointmentTypeAsync(admin, specialty, resourceType);

        await admin.PostAsync($"/api/config/appointment-types/{appointmentType}/deactivate");
        await admin.PostAsync($"/api/config/resources/{resource}/deactivate");
        await admin.PostAsync($"/api/config/resource-types/{resourceType}/deactivate");
        await admin.PostAsync($"/api/config/specialties/{specialty}/deactivate");

        await fixture.WithDatabaseAsync(async database =>
        {
            // Asserted against the database rather than the API, because "the list no longer
            // shows it" would pass even if the row had been destroyed.
            Assert.NotNull(await database.Specialties
                .FirstOrDefaultAsync(entity => entity.Id == specialty));
            Assert.NotNull(await database.ResourceTypes
                .FirstOrDefaultAsync(entity => entity.Id == resourceType));
            Assert.NotNull(await database.Resources
                .FirstOrDefaultAsync(entity => entity.Id == resource));
            Assert.NotNull(await database.AppointmentTypes
                .FirstOrDefaultAsync(entity => entity.Id == appointmentType));

            var retired = await database.Specialties.FirstAsync(entity => entity.Id == specialty);
            Assert.NotNull(retired.DeactivatedAtUtc);
        });
    }

    [Fact]
    public async Task The_database_refuses_a_duplicate_active_name_even_without_the_slice_check()
    {
        // The floor beneath design D3's friendly check: the partial unique index on
        // lower(name). Inserting straight through the context bypasses the slice entirely, so
        // what refuses here is Postgres.
        var name = Unique("Cardiologia");

        await fixture.WithDatabaseAsync(async database =>
        {
            database.Specialties.Add(
                Clinic.Domain.Configuration.Specialty.Define(name, DateTimeOffset.UtcNow));
            await database.SaveChangesAsync();
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            fixture.WithDatabaseAsync(async database =>
            {
                database.Specialties.Add(
                    Clinic.Domain.Configuration.Specialty.Define(
                        name.ToUpperInvariant(), DateTimeOffset.UtcNow));
                await database.SaveChangesAsync();
            }));
    }

    // --- helpers ---------------------------------------------------------------------

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("code").GetString();
    }

    private async Task<Guid> CreateSpecialtyAsync(TestClient admin, string? name = null)
    {
        var created = await admin.PostAsync(
            "/api/config/specialties",
            new { name = name ?? Unique("Especialidade") });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return await IdOf(created);
    }

    private async Task<Guid> CreateResourceTypeAsync(
        TestClient admin,
        string? name = null,
        int bufferMinutes = 15)
    {
        var created = await admin.PostAsync(
            "/api/config/resource-types",
            new { name = name ?? Unique("Tipo"), bufferMinutes });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return await IdOf(created);
    }

    private async Task<Guid> CreateResourceAsync(TestClient admin, Guid resourceTypeId, string? name = null)
    {
        var created = await admin.PostAsync(
            "/api/config/resources",
            new { name = name ?? Unique("Sala"), resourceTypeId });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return await IdOf(created);
    }

    private async Task<Guid> CreateAppointmentTypeAsync(
        TestClient admin,
        Guid specialtyId,
        Guid requiredResourceTypeId,
        string? name = null)
    {
        var created = await admin.PostAsync("/api/config/appointment-types", new
        {
            name = name ?? Unique("Consulta"),
            specialtyId,
            requiredResourceTypeId,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return await IdOf(created);
    }

    private static async Task<Guid> IdOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement?> FindAsync(TestClient admin, string collection, Guid id)
    {
        var listed = await admin.GetAsync($"/api/config/{collection}");
        var json = await listed.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("id").GetGuid() == id)
            {
                return entry.Clone();
            }
        }

        return null;
    }

    private static async Task<bool> IsActiveAsync(TestClient admin, string collection, Guid id)
    {
        var entry = await FindAsync(admin, collection, id);

        Assert.NotNull(entry);

        return entry!.Value.GetProperty("isActive").GetBoolean();
    }
}
