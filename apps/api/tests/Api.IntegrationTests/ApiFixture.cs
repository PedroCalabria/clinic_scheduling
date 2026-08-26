using System.Net;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Auth.Google;
using Clinic.Api.Infrastructure.Calendar;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;
using DomainPasswordHasher = Clinic.Domain.Identity.IPasswordHasher;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// Boots the API against a real PostgreSQL container (00-context.md §6, design D6), and — from
/// change 2 — lets a test act as any role in one line (design A13).
/// </summary>
/// <remarks>
/// <para>
/// Testcontainers gives every run a disposable database on the SAME pinned image the Compose
/// stack uses, so a passing test says something about the deployed system rather than about an
/// in-memory substitute. Starting the host also runs the startup migration, which is what
/// makes "migrations apply cleanly" an assertion rather than a hope.
/// </para>
/// <para>
/// <see cref="AsRoleAsync"/> mints a session directly rather than signing in. That is the
/// point — change 3 onward should not have to drive a login form to test a schedule — but it
/// has a cost worth stating plainly: it skips the code that ISSUES sessions, so a bug there
/// would be invisible to every test that uses it. That is why the sign-in flows have their own
/// end-to-end tests (<see cref="InternalSignInTests"/>, <see cref="GoogleSignInTests"/>), and
/// why those are not optional.
/// </para>
/// <para>
/// Composition over inheritance: this fixture OWNS a WebApplicationFactory rather than
/// deriving from it, because xunit's IAsyncLifetime.DisposeAsync (Task) collides with
/// WebApplicationFactory's IAsyncDisposable.DisposeAsync (ValueTask).
/// </para>
/// </remarks>
public sealed class ApiFixture : IAsyncLifetime
{
    /// <summary>The seeded administrator's credentials, as configuration supplies them.</summary>
    public const string BootstrapAdministratorEmail = "bootstrap.admin@clinic.test";

    public const string BootstrapAdministratorPassword = "bootstrap-password-123";

    /// <summary>Password given to every internal account this fixture seeds.</summary>
    public const string SeededPassword = "seeded-password-123";

    /// <summary>
    /// The clinic timezone every host in this collection runs on.
    /// </summary>
    /// <remarks>
    /// A real zone with a real history rather than UTC, on purpose: UTC would make an
    /// accidental instant-conversion invisible, which is precisely the bug design E3 exists to
    /// prevent. São Paulo has no DST today but had it until 2019, so the zone database has
    /// something to say about it.
    /// </remarks>
    public const string ClinicTimezoneId = "America/Sao_Paulo";

    /// <summary>
    /// The key every host in this collection protects calendar credentials with (change 6a).
    /// </summary>
    /// <remarks>
    /// A fixed test value, and it being fixed is what lets a test assert that a stored column is
    /// not the plaintext token while still being able to open it. Thirty-two bytes, because
    /// anything else is refused at startup — the validator is not decorative.
    /// </remarks>
    public const string CalendarEncryptionKey = "Y2xpbmljLXNjaGVkdWxpbmctdGVzdC1rZXktMzJieXQ=";

    // Same tag as infra/docker-compose.yml — that agreement is the point of pinning
    // (00-context.md §1).
    private readonly PostgreSqlContainer _database =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    private static readonly Uri BaseAddress = new("https://localhost");

    private WebApplicationFactory<Program>? _factory;
    private NpgsqlConnection? _respawnConnection;
    private Respawner? _respawner;

    /// <summary>Google, stubbed at its two seams (design A4).</summary>
    public GoogleTestDouble Google { get; } = new();

    /// <summary>
    /// Stands in for Google's calendar token and revocation endpoints (change 6a).
    /// </summary>
    /// <remarks>
    /// Deliberately a second double rather than a widening of <see cref="Google"/>: the two
    /// flows are separate all the way down (design K2), and a shared double would let a test
    /// stage a refresh token that the sign-in flow could then be shown to return — which is
    /// precisely the state this change's design says must be unreachable.
    /// </remarks>
    public CalendarTestDouble Calendar { get; } = new();

    public string ConnectionString => _database.GetConnectionString();

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _factory = BuildHost(new Dictionary<string, string>
        {
            // The shared host must not trip its own brake: every test in this collection sends
            // login attempts from the same address, so the default of ten a minute would leak
            // 429s into unrelated tests. The tests that PROVE the limiter refuses build their
            // own host with a low limit (see CreateHost).
            ["Auth:LoginAttemptsPerMinute"] = "10000",
        });

        // Forces host creation (and therefore the startup migration and the administrator
        // bootstrap) before any test runs, so a failure there surfaces here rather than as a
        // confusing 500 later.
        _ = Factory.Services;

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
    /// Builds a second host against the SAME database, with settings overridden.
    /// </summary>
    /// <remarks>
    /// For behaviour that is a property of configuration rather than of a request — the rate
    /// limiter's threshold, or a deployment with no Google client. Sharing the container keeps
    /// it cheap; the caller disposes what it gets. This follows the pattern
    /// <see cref="HealthEndpointUnhealthyTests"/> established: a test that needs a differently
    /// configured app gets its own app rather than mutating the one everybody else is using.
    /// </remarks>
    internal WebApplicationFactory<Program> CreateHost(IDictionary<string, string> settings) =>
        BuildHost(settings);

    /// <summary>A client for a host built by <see cref="CreateHost"/>.</summary>
    internal TestClient CreateClientFor(WebApplicationFactory<Program> host, string? sessionToken = null)
    {
        var cookies = new CookieContainer();

        if (sessionToken is not null)
        {
            cookies.Add(new Cookie(AuthCookies.Session, sessionToken, "/", BaseAddress.Host) { Secure = true });
        }

        var client = host.CreateDefaultClient(new CookieContainerHandler(cookies));
        client.BaseAddress = BaseAddress;

        return new TestClient(client, cookies, BaseAddress);
    }

    /// <summary>Issues a session for a user on an alternate host built by <see cref="CreateHost"/>.</summary>
    internal static async Task<string> IssueSessionOnAsync(WebApplicationFactory<Program> host, User user)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionStore>();

        var (token, _) = await sessions.IssueAsync(user, CancellationToken.None);

        return token;
    }

    /// <summary>An unauthenticated client.</summary>
    public TestClient CreateAnonymousClient()
    {
        var (client, cookies) = CreateHttpClient(sessionToken: null);

        return new TestClient(client, cookies, BaseAddress);
    }

    /// <summary>Kept for the health tests, which predate the session mechanism.</summary>
    public HttpClient CreateClient() => CreateHttpClient(sessionToken: null).Client;

    /// <summary>
    /// Seeds a user with this role and returns a client already holding a session for them —
    /// the one-liner every later change's tests are built on (design A13).
    /// </summary>
    public async Task<(TestClient Client, User User)> AsRoleAsync(Role role, string? email = null)
    {
        var user = await SeedUserAsync(role, email);
        var client = await AsUserAsync(user);

        return (client, user);
    }

    /// <summary>Returns a client holding a fresh session for an existing user.</summary>
    public async Task<TestClient> AsUserAsync(User user)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionStore>();

        var (token, _) = await sessions.IssueAsync(user, CancellationToken.None);
        var (client, cookies) = CreateHttpClient(token);

        return new TestClient(client, cookies, BaseAddress);
    }

    /// <summary>
    /// Creates a user of the given role, with the shape that role legitimately has: staff get
    /// a password, patients arrive from Google with a patient record, professionals start as an
    /// invitation that has been claimed.
    /// </summary>
    public async Task<User> SeedUserAsync(Role role, string? email = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<DomainPasswordHasher>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();

        // The version the RUNNING host is configured with, not a literal. Read from configuration
        // by `booking-core`, which made this consent load-bearing: until then nothing checked it,
        // so a seeded patient holding version "test" was indistinguishable from a real one. Now the
        // booking gate compares versions, and a fixture that seeds a stale one would refuse every
        // test in the suite while looking like a bug in the gate (design B12).
        var consentVersion = scope.ServiceProvider
            .GetRequiredService<IOptions<AuthOptions>>().Value.ConsentVersion;

        var address = email ?? $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@clinic.test";

        User user;

        switch (role)
        {
            case Role.FrontDesk:
            case Role.Administrator:
                user = User.CreateInternalStaff(address, hasher.Hash(SeededPassword), role, now);
                database.Users.Add(user);
                break;

            case Role.Professional:
                user = User.InviteProfessional(address, now);
                user.ClaimWithGoogleIdentity($"google-sub-{Guid.NewGuid():N}");
                database.Users.Add(user);
                break;

            case Role.Patient:
            default:
                user = User.RegisterGooglePatient(address, $"google-sub-{Guid.NewGuid():N}", now);
                database.Users.Add(user);
                database.Patients.Add(Patient.Register(user.Id, "Test Patient", address, now));
                database.Consents.Add(Consent.Grant(user.Id, ConsentType.DataProcessing, consentVersion, now));
                break;
        }

        await database.SaveChangesAsync();

        return user;
    }

    /// <summary>
    /// Runs work against the app's own services — for arranging and asserting on state.
    /// </summary>
    /// <remarks>
    /// Internal rather than public because <c>ClinicDbContext</c> is internal to the API and
    /// reaches this assembly through <c>InternalsVisibleTo</c>. Tests asserting on persisted
    /// state is exactly why that attribute exists.
    /// </remarks>
    internal async Task WithDatabaseAsync(Func<ClinicDbContext, Task> work)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<ClinicDbContext>());
    }

    /// <summary>
    /// A scope the caller owns, for tests that need <b>two live contexts at once</b>.
    /// </summary>
    /// <remarks>
    /// Added by <c>booking-core</c>. <see cref="WithDatabaseAsync"/> opens and closes its own
    /// scope around one callback, which is right for arranging and asserting and useless for the
    /// thing this change has to prove: that two concurrent transactions on two connections
    /// contend for a lock, and that one of them blocks. That needs both contexts alive
    /// simultaneously, so the lifetime has to belong to the test.
    /// </remarks>
    internal AsyncServiceScope CreateScope() => Factory.Services.CreateAsyncScope();

    /// <summary>Runs the administrator bootstrap again, as a restart would.</summary>
    /// <remarks>
    /// Invoked explicitly rather than relying on host startup, so a test can assert what a
    /// SECOND run does — which is the interesting half of idempotency.
    /// </remarks>
    internal static Task RunAdministratorBootstrapAsync(WebApplicationFactory<Program> host)
    {
        var bootstrap = host.Services.GetServices<IHostedService>().OfType<AdministratorBootstrap>().Single();

        return bootstrap.StartAsync(CancellationToken.None);
    }

    public Task RunAdministratorBootstrapAsync() => RunAdministratorBootstrapAsync(Factory);

    /// <summary>
    /// Truncates test-owned data between tests.
    /// </summary>
    /// <remarks>
    /// Identity data is seeded per test rather than relied upon from startup, precisely because
    /// this runs: a test that depended on the bootstrapped administrator surviving a reset would
    /// pass or fail depending on what ran before it (design A13).
    /// </remarks>
    public Task ResetDatabaseAsync() =>
        _respawner is null || _respawnConnection is null
            ? Task.CompletedTask
            : _respawner.ResetAsync(_respawnConnection);

    public async Task DisposeAsync()
    {
        Google.Dispose();

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

    private WebApplicationFactory<Program> BuildHost(IDictionary<string, string> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString());

            // The federated path needs a configured client to be reachable at all (design A14).
            // These are the test double's values, and the app validates real tokens against them.
            builder.UseSetting("Auth:Google:ClientId", GoogleTestDouble.ClientId);
            builder.UseSetting("Auth:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Auth:Google:RedirectUri", "https://localhost/api/auth/google/callback");
            builder.UseSetting("Auth:Google:Issuer", GoogleTestDouble.Issuer);

            // The calendar feature, on for every host in this collection (change 6a). Setting
            // the redirect URI is what turns it on, and the key must then be present or the host
            // refuses to start — which CalendarStartupTests asserts deliberately, using its own
            // host so no other test trips over it.
            builder.UseSetting("Calendar:RedirectUri", "https://localhost/api/calendar/connect/callback");
            builder.UseSetting("Calendar:TokenEncryptionKey", CalendarEncryptionKey);

            // Required configuration with no default (design E3): without it every host in
            // this collection fails to start, which is the behaviour ClinicTimezoneTests
            // asserts deliberately and every other test must not trip over.
            builder.UseSetting("Clinic:Timezone", ClinicTimezoneId);

            builder.UseSetting("Auth:BootstrapAdministrator:Email", BootstrapAdministratorEmail);
            builder.UseSetting("Auth:BootstrapAdministrator:Password", BootstrapAdministratorPassword);

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Google);
                services.AddSingleton<IGoogleSigningKeys>(
                    provider => new GoogleTestDouble.SigningKeys(provider.GetRequiredService<GoogleTestDouble>()));

                services.AddHttpClient<GoogleTokenExchange>()
                    .ConfigurePrimaryHttpMessageHandler(
                        provider => new GoogleTestDouble.TokenEndpointHandler(
                            provider.GetRequiredService<GoogleTestDouble>()));

                services.AddSingleton(Calendar);
                services.AddHttpClient<GoogleCalendarTokens>()
                    .ConfigurePrimaryHttpMessageHandler(
                        provider => new CalendarTestDouble.Handler(
                            provider.GetRequiredService<CalendarTestDouble>()));
            });
        });

    private (HttpClient Client, CookieContainer Cookies) CreateHttpClient(string? sessionToken)
    {
        var cookies = new CookieContainer();

        if (sessionToken is not null)
        {
            cookies.Add(new Cookie(AuthCookies.Session, sessionToken, "/", BaseAddress.Host)
            {
                Secure = true,
            });
        }

        var client = Factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        client.BaseAddress = BaseAddress;

        return (client, cookies);
    }
}

/// <summary>
/// Shares one container and one host across the integration tests.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
