namespace Clinic.Api.Infrastructure.Persistence;

/// <summary>
/// Applies pending migrations when the host starts, before the app serves traffic
/// (design D5).
/// </summary>
/// <remarks>
/// <para>
/// Registered as the first hosted service so it runs before anything else. It is a
/// deliberately thin wrapper: all the logic — and the concurrent-instance caveat — lives
/// in <see cref="DatabaseMigrator"/>.
/// </para>
/// <para>
/// Why a hosted service rather than an inline <c>await</c> after <c>builder.Build()</c> in
/// Program.cs: <c>WebApplicationFactory</c> does not reliably execute statements between
/// <c>Build()</c> and <c>Run()</c> — it captures the built host and stops the entry point
/// there. An inline call would therefore be skipped under the integration tests, which are
/// exactly what must prove that migrations apply cleanly. Host startup is a hook both the
/// real container and the test host genuinely share.
/// </para>
/// </remarks>
internal sealed class DatabaseMigrationStartupService(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        DatabaseMigrator.MigrateAsync(services, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
