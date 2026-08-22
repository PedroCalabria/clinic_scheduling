using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The development clinic seed (spec: a development deployment can seed a complete, runnable
/// clinic).
/// </summary>
/// <remarks>
/// Each test owns its host, because the seed is durable state and two tests sharing one would
/// pass or fail depending on which ran first — the same reason
/// <see cref="BootstrapAndHardeningTests"/> builds its own.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DevelopmentClinicSeedTests(ApiFixture fixture)
{
    [Fact]
    public async Task It_provisions_a_clinic_that_change_four_could_compute_against()
    {
        using var host = SeedingHost(enabled: true, environment: Environments.Development);

        await RunSeedAsync(host);

        await WithDatabaseAsync(host, async database =>
        {
            // Not just "rows exist": the specific shape the availability formula needs.
            Assert.NotEmpty(await database.Specialties.ToListAsync());
            Assert.NotEmpty(await database.Resources.ToListAsync());

            var buffered = await database.ResourceTypes.Where(t => t.BufferMinutes > 0).ToListAsync();
            Assert.NotEmpty(buffered);

            var professional = await database.Professionals.FirstOrDefaultAsync();
            Assert.NotNull(professional);

            Assert.NotEmpty(await database.ProfessionalSpecialties
                .Where(x => x.ProfessionalId == professional!.Id).ToListAsync());

            var durations = await database.ProfessionalAppointmentTypes
                .Where(x => x.ProfessionalId == professional!.Id).ToListAsync();

            // Two different lengths — Decision C is the reason this entity exists at all, and a
            // seed with one duration would not demonstrate it.
            Assert.True(durations.Count >= 2);
            Assert.True(durations.Select(d => d.DurationMinutes).Distinct().Count() >= 2);

            Assert.NotEmpty(await database.WorkingHoursTemplates
                .Where(x => x.ProfessionalId == professional!.Id).ToListAsync());
        });
    }

    [Fact]
    public async Task The_seeded_professional_is_an_unclaimed_invitation()
    {
        // The seed must not fake a signed-in professional: "invited, never signed in" is the
        // state S7 has to handle (design E1), so the demo should show it.
        using var host = SeedingHost(enabled: true, environment: Environments.Development);

        await RunSeedAsync(host);

        await WithDatabaseAsync(host, async database =>
        {
            var professional = await database.Professionals.FirstAsync();
            var user = await database.Users.SingleAsync(u => u.Id == professional.UserId);

            Assert.Equal(Role.Professional, user.Role);
            Assert.Null(user.ExternalSubjectId);
            Assert.Null(user.PasswordHash);
        });
    }

    [Fact]
    public async Task Running_it_twice_creates_no_duplicates_and_keeps_an_edit()
    {
        using var host = SeedingHost(enabled: true, environment: Environments.Development);

        await RunSeedAsync(host);

        int specialtiesAfterFirst = 0;
        var editedName = $"Cardiologia (renomeada {Guid.NewGuid():N})";

        await WithDatabaseAsync(host, async database =>
        {
            specialtiesAfterFirst = await database.Specialties.CountAsync();

            // An operator edits something between restarts. A naive "ensure the seed values"
            // would silently undo this.
            var dermatology = await database.Specialties.SingleAsync(s => s.Name == "Dermatologia");
            dermatology.Rename(editedName);
            await database.SaveChangesAsync();
        });

        await RunSeedAsync(host);

        await WithDatabaseAsync(host, async database =>
        {
            Assert.Equal(specialtiesAfterFirst, await database.Specialties.CountAsync());
            Assert.Single(await database.Professionals.ToListAsync());

            // The edit survived.
            Assert.NotNull(await database.Specialties.FirstOrDefaultAsync(s => s.Name == editedName));
        });
    }

    [Fact]
    public async Task It_does_nothing_outside_development_however_it_is_configured()
    {
        // Enabled, but in Production. The environment guard has to win.
        using var host = SeedingHost(enabled: true, environment: Environments.Production);

        await AssertCreatesNothingAsync(host);
    }

    [Fact]
    public async Task It_does_nothing_when_not_explicitly_enabled()
    {
        // Development alone is not enough: a laptop pointed at a shared database is in
        // Development too.
        using var host = SeedingHost(enabled: false, environment: Environments.Development);

        await AssertCreatesNothingAsync(host);
    }

    /// <summary>
    /// Asserts this run created nothing, rather than that the database is empty.
    /// </summary>
    /// <remarks>
    /// Every host in this collection shares one Postgres container, so by the time these tests
    /// run another one may legitimately have seeded. "The table is empty" was never the property
    /// under test — "this invocation added no rows" is, and it holds whatever ran first.
    /// </remarks>
    private static async Task AssertCreatesNothingAsync(WebApplicationFactory<Program> host)
    {
        var before = await CountsAsync(host);

        await RunSeedAsync(host);

        Assert.Equal(before, await CountsAsync(host));
    }

    private static async Task<(int Professionals, int Specialties, int Segments)> CountsAsync(
        WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        return (
            await database.Professionals.CountAsync(),
            await database.Specialties.CountAsync(),
            await database.WorkingHoursTemplates.CountAsync());
    }

    private WebApplicationFactory<Program> SeedingHost(bool enabled, string environment) =>
        fixture.CreateHost(new Dictionary<string, string>
        {
            ["Clinic:SeedDevelopmentData"] = enabled ? "true" : "false",
            ["environment"] = environment,
        });

    private static Task RunSeedAsync(WebApplicationFactory<Program> host)
    {
        var seed = host.Services.GetServices<IHostedService>().OfType<DevelopmentClinicSeed>().Single();

        return seed.StartAsync(CancellationToken.None);
    }

    private static async Task WithDatabaseAsync(
        WebApplicationFactory<Program> host,
        Func<ClinicDbContext, Task> work)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<ClinicDbContext>());
    }
}
