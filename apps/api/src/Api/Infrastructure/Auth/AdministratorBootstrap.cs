using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DomainPasswordHasher = Clinic.Domain.Identity.IPasswordHasher;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Creates the first administrator from configuration, so the system is reachable before any
/// administrator exists to create one (design A6).
/// </summary>
/// <remarks>
/// <para>
/// The chicken-and-egg is real: S11 is how staff accounts are created, and only an
/// administrator may open it. Configuration is the way in because it matches how everything
/// else about this deployment is supplied (12-factor, the same as the connection string), and
/// because it works identically in development, in CI, and on the VPS. A one-off CLI command
/// would be a second entry point in the container for a single job; "the first user to sign
/// in becomes an administrator" would be a footgun on a public URL.
/// </para>
/// <para>
/// Idempotent, because it runs on every boot and not only the first. If the account already
/// exists it is left exactly as it is — including a password the operator has since changed,
/// which a naive "ensure the configured password" would silently undo.
/// </para>
/// <para>
/// Ordering: registered after the migration startup service, so the schema exists by the time
/// this runs. Hosted services start in registration order, which is the only coupling
/// between the two.
/// </para>
/// </remarks>
internal sealed class AdministratorBootstrap(
    IServiceProvider services,
    IOptions<AuthOptions> options,
    TimeProvider clock,
    ILogger<AdministratorBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value.BootstrapAdministrator;

        if (!configured.IsConfigured)
        {
            // Not an error: a deployment whose administrator already exists needs no bootstrap
            // values, and leaving them out is the safer default.
            logger.LogInformation(
                "No bootstrap administrator configured; skipping. Set Auth__BootstrapAdministrator__Email and __Password to create one.");

            return;
        }

        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<DomainPasswordHasher>();

        var email = EmailAddress.Normalize(configured.Email!);

        var existing = await database.Users.SingleOrDefaultAsync(
            user => user.Email == email && user.DeletedAtUtc == null,
            cancellationToken);

        if (existing is not null)
        {
            if (existing.MustChangePassword)
            {
                // The backstop to the forced change (PasswordChangeGate). It names the account
                // so the signal is in the logs an operator actually reads, rather than only on
                // a screen someone dismissed.
                logger.LogWarning(
                    "Bootstrap administrator {Email} is still using the password supplied by configuration. "
                    + "It must be changed before the account can do anything else.",
                    existing.Email);
            }

            return;
        }

        var administrator = User.CreateInternalStaff(
            email,
            passwordHasher.Hash(configured.Password!),
            Role.Administrator,
            clock.GetUtcNow(),
            // The credential came from an environment file, so it is known to whoever can read
            // that file. It gets the account through the door once and no further.
            mustChangePassword: true);

        database.Users.Add(administrator);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Created bootstrap administrator {Email}. Its password must be changed on first sign-in.",
            administrator.Email);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
