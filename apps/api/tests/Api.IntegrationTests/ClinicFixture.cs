using System.Net;
using System.Text.Json;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// A bookable clinic, built through the admin API.
/// </summary>
/// <param name="Professionals">
/// Configuration-record ids, in the order they were created. Booking references this id, not the
/// user id — the same distinction the availability read makes.
/// </param>
/// <param name="Rooms">
/// Resource ids, ordered by name so the order here is the order the solver prefers. That makes the
/// server's room assignment assertable: the first free one wins.
/// </param>
internal sealed record BookableClinic(
    IReadOnlyList<User> ProfessionalUsers,
    IReadOnlyList<Guid> Professionals,
    IReadOnlyList<Guid> Rooms,
    Guid SpecialtyId,
    Guid ResourceTypeId,
    Guid AppointmentTypeId,
    int DurationMinutes,
    LocalDate Date)
{
    public User ProfessionalUser => ProfessionalUsers[0];

    public Guid ProfessionalId => Professionals[0];

    public Guid RoomId => Rooms[0];
}

/// <summary>
/// Builds a clinic that can be booked against (design B9, B11).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>AvailabilityTests</c>'s own fixture rather than shared with it, and the reason
/// is a real difference and not convenience: the booking tests need <b>several rooms and several
/// professionals</b> — to prove the server's room assignment falls through, that the last free room
/// is genuinely contended across professionals, and that a patient can be double-booked by two
/// different professionals. Change 4's fixture models one of each because one of each is all its
/// read needed. Bending it into a superset would have meant editing twenty-six passing tests to
/// support cases none of them exercises.
/// </para>
/// <para>
/// Built through the admin endpoints rather than by writing rows, for the same reason the dev seed
/// goes through the domain factories: a fixture that bypasses a rule can construct a state the
/// product cannot reach, and then the test proves something about nothing.
/// </para>
/// </remarks>
internal sealed class ClinicBuilder(ApiFixture fixture)
{
    internal static readonly DateTimeZone Clinic =
        DateTimeZoneProviders.Tzdb[ApiFixture.ClinicTimezoneId];

    internal static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>
    /// A week out, so neither the minimum lead time nor the horizon is in play.
    /// </summary>
    /// <remarks>
    /// Relative to now rather than a fixed date, for the reason the seed uses the same trick: a
    /// hard-coded date drifts past the horizon and the test starts asserting emptiness while
    /// looking like it asserts availability.
    /// </remarks>
    internal static LocalDate TargetDate(int daysAhead = 7) =>
        SystemClock.Instance.GetCurrentInstant().InZone(Clinic).Date.PlusDays(daysAhead);

    internal static string Iso(LocalDate date) => LocalDatePattern.Iso.Format(date);

    /// <summary>The instant a clinic wall-clock time on a date corresponds to.</summary>
    internal static Instant At(LocalDate date, int hour, int minute = 0) =>
        Clinic.AtStrictly(date.At(new LocalTime(hour, minute))).ToInstant();

    internal static string Utc(Instant instant) => InstantPattern.ExtendedIso.Format(instant);

    internal static string Wall(LocalDate date, int hour, int minute = 0) =>
        $"{Iso(date)}T{hour:D2}:{minute:D2}";

    /// <summary>
    /// A clinic with the given number of rooms and professionals, all working the same hours.
    /// </summary>
    /// <param name="bufferMinutes">
    /// Turnaround on the room type. Zero by default so tests that are not about the buffer are not
    /// silently shifted by it.
    /// </param>
    internal async Task<BookableClinic> BuildAsync(
        int startHour = 9,
        int endHour = 12,
        int durationMinutes = 60,
        int rooms = 1,
        int professionals = 1,
        int bufferMinutes = 0,
        int daysAhead = 7)
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        // A week out by default, so neither the lead time nor the horizon is in play. The tests
        // that are ABOUT those two move the date instead of moving the parameter past its
        // configured range, which would fail startup validation rather than the assertion.
        var date = TargetDate(daysAhead);

        var specialty = await CreateAsync(admin, "/api/config/specialties", new { name = Unique("Specialty") });

        var resourceType = await CreateAsync(
            admin, "/api/config/resource-types", new { name = Unique("Room"), bufferMinutes });

        // Named so the alphabetical order is the creation order, because the loading step orders
        // candidates by name and the solver takes the first free one — which makes "the server
        // assigned the first room" an assertion about an id rather than about luck.
        var prefix = Unique("Room");
        var roomIds = new List<Guid>();

        for (var index = 0; index < rooms; index++)
        {
            roomIds.Add(await CreateAsync(
                admin,
                "/api/config/resources",
                new { name = $"{prefix}-{index:D2}", resourceTypeId = resourceType }));
        }

        var appointmentType = await CreateAsync(admin, "/api/config/appointment-types", new
        {
            name = Unique("Visit"),
            specialtyId = specialty,
            requiredResourceTypeId = resourceType,
        });

        var users = new List<User>();
        var professionalIds = new List<Guid>();

        for (var index = 0; index < professionals; index++)
        {
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

                    // Already in force and open-ended, so the effective-date dimension is
                    // satisfied rather than being what these tests are about.
                    effectiveFrom = Iso(date.PlusDays(-30)),
                    effectiveTo = (string?)null,
                }));

            users.Add(user);
            professionalIds.Add(await ProfessionalIdAsync(user.Id));
        }

        return new BookableClinic(
            users,
            professionalIds,
            roomIds,
            specialty,
            resourceType,
            appointmentType,
            durationMinutes,
            date);
    }

    internal async Task<Guid> ProfessionalIdAsync(Guid userId)
    {
        var id = Guid.Empty;

        await fixture.WithDatabaseAsync(async database =>
            id = await database.Professionals
                .Where(professional => professional.UserId == userId && professional.DeactivatedAtUtc == null)
                .Select(professional => professional.Id)
                .SingleAsync());

        return id;
    }

    internal async Task<Guid> PatientIdAsync(Guid userId)
    {
        var id = Guid.Empty;

        await fixture.WithDatabaseAsync(async database =>
            id = await database.Patients
                .Where(patient => patient.UserId == userId)
                .Select(patient => patient.Id)
                .SingleAsync());

        return id;
    }

    internal static async Task<Guid> CreateAsync(TestClient client, string url, object body)
    {
        var response = await client.PostAsync(url, body);

        await Succeeds(Task.FromResult(response));

        return (await Body(response)).GetProperty("id").GetGuid();
    }

    internal static async Task Succeeds(Task<HttpResponseMessage> call)
    {
        var response = await call;

        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.Created))
        {
            Assert.Fail($"expected success, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    internal static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>The error code an API refusal carries, for asserting the catalogue contract.</summary>
    internal static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await Body(response)).TryGetProperty("code", out var code) ? code.GetString() : null;
}
