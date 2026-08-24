using System.Net;
using Clinic.Domain.Identity;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The catalogue as a patient sees it (spec: booking — the patient portal requirement).
/// </summary>
/// <remarks>
/// <para>
/// This endpoint exists because P2 could not: every other catalogue read is administrator-only,
/// which is right for the screens that manage the catalogue and useless for the one that consumes
/// it. So what matters here is the two things a wrong version would get wrong silently — who may
/// read it, and whether it offers paths that lead nowhere.
/// </para>
/// <para>
/// The second is the substantive one. A specialty nobody is qualified in, or a kind of visit nobody
/// holds a duration for, would render as a selectable option whose every search comes back empty —
/// and a patient reasonably reads that as a broken clinic rather than an unstaffed one.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class BookingOptionsTests(ApiFixture fixture)
{
    private ClinicBuilder Clinic => new(fixture);

    private const string Url = "/api/booking/options";

    [Fact]
    public async Task A_patient_sees_the_specialty_the_visit_and_who_offers_it()
    {
        var clinic = await Clinic.BuildAsync(professionals: 2);
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var response = await patient.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ClinicBuilder.Body(response);

        // The clinic's zone, carried here as well as on the availability response — because the
        // confirmation step renders times without asking for availability, and the browser's own
        // zone is not an acceptable substitute.
        Assert.Equal(ApiFixture.ClinicTimezoneId, body.GetProperty("timezone").GetString());

        var specialty = body.GetProperty("specialties")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("specialtyId").GetGuid() == clinic.SpecialtyId);

        var type = specialty.GetProperty("appointmentTypes")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("appointmentTypeId").GetGuid() == clinic.AppointmentTypeId);

        var professionals = type.GetProperty("professionals").EnumerateArray().ToList();

        Assert.Equal(2, professionals.Count);
        Assert.Equal(
            clinic.Professionals.Order().ToArray(),
            professionals.Select(entry => entry.GetProperty("professionalId").GetGuid()).Order().ToArray());

        // A label rather than the account address: a patient has no use for a staff email, and
        // handing one out is a small discourtesy this project has no reason to commit.
        Assert.All(professionals, entry =>
        {
            var name = entry.GetProperty("displayName").GetString();

            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.DoesNotContain("@", name);
        });
    }

    [Fact]
    public async Task A_display_name_is_derived_from_the_account_address()
    {
        // The interim naming seam, asserted so its shape is deliberate rather than incidental —
        // and so the day a real name lands on the Professional record, this test is what says the
        // derivation is no longer in use.
        var email = $"dra.helena.{Guid.NewGuid():N}@clinic.local";

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await ClinicBuilder.CreateAsync(
            admin, "/api/config/specialties", new { name = ClinicBuilder.Unique("Specialty") });

        var resourceType = await ClinicBuilder.CreateAsync(
            admin, "/api/config/resource-types", new { name = ClinicBuilder.Unique("Room"), bufferMinutes = 0 });

        var appointmentType = await ClinicBuilder.CreateAsync(admin, "/api/config/appointment-types", new
        {
            name = ClinicBuilder.Unique("Visit"),
            specialtyId = specialty,
            requiredResourceTypeId = resourceType,
        });

        var user = await fixture.SeedUserAsync(Role.Professional, email);

        await ClinicBuilder.Succeeds(admin.PostAsync(
            $"/api/config/professionals/{user.Id}/specialties", new { specialtyId = specialty }));

        await ClinicBuilder.Succeeds(admin.PutAsync(
            $"/api/config/professionals/{user.Id}/durations",
            new { appointmentTypeId = appointmentType, durationMinutes = 30 }));

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var body = await ClinicBuilder.Body(await patient.GetAsync(Url));

        var name = body.GetProperty("specialties")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("specialtyId").GetGuid() == specialty)
            .GetProperty("appointmentTypes")[0]
            .GetProperty("professionals")[0]
            .GetProperty("displayName")
            .GetString();

        Assert.StartsWith("Dra Helena", name);
    }

    [Fact]
    public async Task An_appointment_type_nobody_is_qualified_for_is_not_offered()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var specialty = await ClinicBuilder.CreateAsync(
            admin, "/api/config/specialties", new { name = ClinicBuilder.Unique("Orphan") });

        var resourceType = await ClinicBuilder.CreateAsync(
            admin, "/api/config/resource-types", new { name = ClinicBuilder.Unique("Room"), bufferMinutes = 0 });

        // Defined, and nobody holds a duration for it. Perfectly valid configuration — 3a can
        // create a kind of visit before anybody is set up to deliver it.
        await ClinicBuilder.CreateAsync(admin, "/api/config/appointment-types", new
        {
            name = ClinicBuilder.Unique("Unstaffed"),
            specialtyId = specialty,
            requiredResourceTypeId = resourceType,
        });

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var body = await ClinicBuilder.Body(await patient.GetAsync(Url));

        // The whole specialty is absent, not merely the type: a specialty whose every option is
        // unstaffed is a dead end, and offering it would make the clinic look broken.
        Assert.DoesNotContain(
            specialty,
            body.GetProperty("specialties").EnumerateArray().Select(e => e.GetProperty("specialtyId").GetGuid()));
    }

    [Fact]
    public async Task A_retired_professional_stops_being_offered()
    {
        var clinic = await Clinic.BuildAsync();
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        await ClinicBuilder.Succeeds(admin.PostAsync(
            $"/api/config/professionals/{clinic.ProfessionalUser.Id}/durations/{clinic.AppointmentTypeId}/clear"));

        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var body = await ClinicBuilder.Body(await patient.GetAsync(Url));

        // Clearing the duration is clearing the qualification, so the option goes with it — the
        // same gate the availability read and the booking check both use, said once.
        Assert.DoesNotContain(
            clinic.SpecialtyId,
            body.GetProperty("specialties").EnumerateArray().Select(e => e.GetProperty("specialtyId").GetGuid()));
    }

    [Theory]
    [InlineData(Role.Patient)]
    [InlineData(Role.Professional)]
    [InlineData(Role.FrontDesk)]
    [InlineData(Role.Administrator)]
    public async Task Any_authenticated_role_may_read_the_catalogue(Role role)
    {
        await Clinic.BuildAsync();

        var (client, _) = await fixture.AsRoleAsync(role);
        using var _client = client;

        // Readable by everyone signed in, like availability itself: this is the clinic's service
        // catalogue, never patient data. Front desk and reception need it for S5 in 5b.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url)).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        using var anonymous = fixture.CreateAnonymousClient();

        var response = await anonymous.GetAsync(Url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.session_expired", await ClinicBuilder.CodeOf(response));
    }
}
