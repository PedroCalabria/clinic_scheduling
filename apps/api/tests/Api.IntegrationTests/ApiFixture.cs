using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// Boots the API against a real PostgreSQL container (00-context.md §6, design D6).
/// </summary>
/// <remarks>
/// Testcontainers gives every run a disposable database on the SAME pinned image the
/// Compose stack uses, so a passing test says something about the deployed system rather
/// than about an in-memory substitute. Starting the host also runs the startup migration,
/// which is what makes "migrations apply cleanly" an assertion rather than a hope.
///
/// Composition over inheritance: this fixture OWNS a WebApplicationFactory rather than
/// deriving from it, because xunit's IAsyncLifetime.DisposeAsync (Task) collides with
/// WebApplicationFactory's IAsyncDisposable.DisposeAsync (ValueTask).
/// </remarks>
public sealed class ApiFixture : IAsyncLifetime
{
    // Same tag as infra/docker-compose.yml — that agreement is the point of pinning
    // (00-context.md §1).
    private readonly PostgreSqlContainer _database =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    private WebApplicationFactory<Program>? _factory;
    private NpgsqlConnection? _respawnConnection;
    private Respawner? _respawner;

    public HttpClient CreateClient() =>
        (_factory ?? throw new InvalidOperationException("Fixture not initialised.")).CreateClient();

    public string ConnectionString => _database.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString()));

        // Forces host creation (and therefore the startup migration) before any test runs,
        // so a migration failure surfaces here rather than as a confusing 500 later.
        _ = _factory.Services;

        _respawnConnection = new NpgsqlConnection(_database.GetConnectionString());
        await _respawnConnection.OpenAsync();

        _respawner = await Respawner.CreateAsync(_respawnConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            // Never wipe the migration history — that would make the schema look unapplied.
            TablesToIgnore = [new Table("__EFMigrationsHistory")],
        });
    }

    /// <summary>
    /// Truncates test-owned data between tests.
    /// </summary>
    /// <remarks>
    /// There is nothing to reset in change 1 — the only table is an empty marker. It is
    /// wired now because retrofitting state isolation onto an existing suite is far worse
    /// than establishing it while there is one test (design D6).
    /// </remarks>
    public Task ResetDatabaseAsync() =>
        _respawner is null || _respawnConnection is null
            ? Task.CompletedTask
            : _respawner.ResetAsync(_respawnConnection);

    public async Task DisposeAsync()
    {
        if (_respawnConnection is not null)
        {
            await _respawnConnection.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _database.DisposeAsync();
    }
}

/// <summary>
/// Shares one container and one host across the integration tests that only read.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
