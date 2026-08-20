using System.Net;
using System.Text.Json;
using Npgsql;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// Covers the platform-health spec against a real PostgreSQL.
/// </summary>
/// <remarks>
/// Scope boundary worth knowing: these tests drive the API in-process through
/// WebApplicationFactory, so Caddy, the static builds, and the base paths are NOT
/// exercised here — they cannot be. That gap is covered by the compose-smoke tier
/// (scripts/compose-smoke.mjs, design D6).
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class HealthEndpointTests(ApiFixture fixture)
{
    [Fact]
    public async Task Reports_healthy_when_database_is_reachable()
    {
        using var client = fixture.CreateClient();

        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("Healthy", root.GetProperty("status").GetString());
        Assert.Equal("Healthy", root.GetProperty("checks").GetProperty("database").GetString());
    }

    [Fact]
    public async Task Does_not_disclose_infrastructure_details()
    {
        using var client = fixture.CreateClient();

        var body = await client.GetStringAsync("/api/health");

        // The endpoint is anonymous and publicly reachable through Caddy, so the body must
        // never carry connection details or exception internals.
        var forbidden = new[] { "Password", "Host=", "Npgsql", "Exception", "StackTrace", "at Clinic." };

        foreach (var fragment in forbidden)
        {
            Assert.DoesNotContain(fragment, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Applies_migrations_creating_the_expected_schema()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var tableCommand = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'platform_marker'
            """,
            connection);

        Assert.Equal(1L, Convert.ToInt64(await tableCommand.ExecuteScalarAsync()));

        // The history row is what proves the schema arrived via a migration rather than
        // EnsureCreated — the distinction matters for every later change.
        await using var historyCommand = new NpgsqlCommand(
            "SELECT count(*) FROM \"__EFMigrationsHistory\"",
            connection);

        Assert.True(Convert.ToInt64(await historyCommand.ExecuteScalarAsync()) >= 1L);
    }

    [Fact]
    public async Task Generates_a_correlation_id_when_the_request_omits_one()
    {
        using var client = fixture.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.True(
            response.Headers.TryGetValues("X-Correlation-ID", out var values),
            "The response must carry a correlation id.");

        Assert.False(string.IsNullOrWhiteSpace(values!.Single()));
    }

    [Fact]
    public async Task Preserves_an_inbound_correlation_id()
    {
        const string inbound = "11111111-2222-3333-4444-555555555555";

        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("X-Correlation-ID", inbound);

        var response = await client.SendAsync(request);

        Assert.Equal(inbound, response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Respawn_resets_state_between_tests()
    {
        // Nothing to reset yet; this asserts the harness itself works, so change 3 inherits
        // a reset mechanism that has been exercised rather than merely configured.
        await fixture.ResetDatabaseAsync();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT count(*) FROM platform_marker", connection);

        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }
}
