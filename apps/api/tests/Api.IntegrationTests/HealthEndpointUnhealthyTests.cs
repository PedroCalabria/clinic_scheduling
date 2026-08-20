using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// Covers the unhealthy branch of the platform-health spec.
/// </summary>
/// <remarks>
/// Owns its own container instead of using <see cref="ApiFixture"/>: the test works by
/// STOPPING PostgreSQL, which would break every other test sharing it.
///
/// The database is stopped only after the host has started, so the startup migration has
/// already run. That ordering matters — migrate-on-startup means an app that never reached
/// a database never finishes starting, so "unhealthy" is a state a *running* system enters
/// when its database goes away, which is exactly the production failure worth covering.
/// </remarks>
public sealed class HealthEndpointUnhealthyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString()));

        _ = _factory.Services;
    }

    [Fact]
    public async Task Reports_unhealthy_with_503_when_the_database_becomes_unreachable()
    {
        using var client = _factory!.CreateClient();

        // Sanity check: healthy first, so a failure below cannot be blamed on setup.
        var healthy = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);

        await _database.StopAsync();

        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("Unhealthy", root.GetProperty("status").GetString());
        Assert.Equal("Unhealthy", root.GetProperty("checks").GetProperty("database").GetString());

        // The failure path is where leaks happen: the framework's health report carries the
        // Npgsql exception, and this asserts it is not projected into the response.
        foreach (var fragment in new[] { "Password", "Host=", "Npgsql", "Exception", "StackTrace" })
        {
            Assert.DoesNotContain(fragment, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _database.DisposeAsync();
    }
}
