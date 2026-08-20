using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Infrastructure.Persistence;

/// <summary>
/// Applies pending EF Core migrations as an explicit startup step (design D5).
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately an explicit call in Program.cs rather than a hosted-service side
/// effect: for a single-instance VPS deployment (04-architecture.md §9) it keeps
/// <c>docker compose up</c> a single command, and it makes the ordering obvious.
/// </para>
/// <para>
/// CAVEAT (recorded, not overlooked): migrate-on-startup is unsafe with concurrent
/// instances — two replicas would race the same migration. Horizontal scale is out of
/// scope and is already the documented trigger for revisiting Redis
/// (04-architecture.md §7); it is the same trigger for promoting migrations to a
/// separate deploy step. Do not add replicas without addressing this.
/// </para>
/// <para>
/// The connect wait exists because a container can be reachable before PostgreSQL is
/// accepting connections. Compose healthchecks already order startup; this is a second,
/// cheap guard that also covers the Testcontainers path. Note it is a bounded wait
/// rather than EF's <c>EnableRetryOnFailure</c> — an execution strategy would constrain
/// the explicit transaction + advisory-lock code that change 5 needs (G1).
/// </para>
/// </remarks>
internal static class DatabaseMigrator
{
    private const int MaxAttempts = 10;
    private static readonly TimeSpan DelayBetweenAttempts = TimeSpan.FromSeconds(2);

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();

        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ClinicDbContext>>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                break;
            }

            if (attempt == MaxAttempts)
            {
                throw new InvalidOperationException(
                    $"Database not reachable after {MaxAttempts} attempts; cannot apply migrations.");
            }

            logger.LogWarning(
                "Database not reachable yet (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}.",
                attempt, MaxAttempts, DelayBetweenAttempts);

            await Task.Delay(DelayBetweenAttempts, cancellationToken);
        }

        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

        if (pending.Length == 0)
        {
            logger.LogInformation("Database schema is up to date; no migrations to apply.");
            return;
        }

        logger.LogInformation("Applying {Count} pending migration(s): {Migrations}.", pending.Length, pending);
        await dbContext.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Migrations applied successfully.");
    }
}
