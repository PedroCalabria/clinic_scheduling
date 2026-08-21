using System.Text.Json.Serialization;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DomainPasswordHasher = Clinic.Domain.Identity.IPasswordHasher;

namespace Clinic.Api.Features.Auth;

/// <summary>
/// <c>POST /api/auth/sign-in</c> — the internal-account login path (S0).
/// </summary>
/// <remarks>
/// The order of checks is the security-relevant part, and it is not the obvious one. The
/// password is verified BEFORE the account status is consulted, so that a wrong password and
/// an unknown address are indistinguishable (<c>401 auth.invalid_credentials</c> for both),
/// and the "this account is disabled" answer is only ever given to someone who already
/// proved they know the password. Checking status first would turn this endpoint into an
/// account-existence oracle.
/// </remarks>
internal static class SignIn
{
    internal static RouteHandlerBuilder MapSignIn(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/auth/sign-in", HandleAsync)
            .AllowAnonymous()
            .WithName("SignIn");

    private static async Task<IResult> HandleAsync(
        SignInRequest request,
        ClinicDbContext database,
        SessionStore sessions,
        DomainPasswordHasher passwordHasher,
        IOptions<AuthOptions> options,
        HttpContext context,
        ILogger<SignInMarker> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return ApiError.Result(
                ErrorCodes.ValidationRequired,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["field"] = "email" });
        }

        string email;

        try
        {
            email = EmailAddress.Normalize(request.Email);
        }
        catch (DomainRuleViolationException)
        {
            // A malformed address cannot match any account, but answering "invalid format"
            // here would still be a slightly different answer than "wrong credentials" —
            // so it is treated as a failed attempt like any other.
            return ApiError.Result(ErrorCodes.InvalidCredentials, StatusCodes.Status401Unauthorized);
        }

        var user = await database.Users.SingleOrDefaultAsync(
            candidate => candidate.Email == email
                && candidate.AuthProvider == AuthProvider.Internal
                && candidate.DeletedAtUtc == null,
            cancellationToken);

        if (user?.PasswordHash is null)
        {
            // No such account, or an account with no password (a federated user, or a
            // professional invitation). One answer for all of them.
            logger.LogInformation("Sign-in rejected: no internal account with a password for the address given.");

            return ApiError.Result(ErrorCodes.InvalidCredentials, StatusCodes.Status401Unauthorized);
        }

        var verification = passwordHasher.Verify(user.PasswordHash, request.Password);

        if (verification == PasswordVerificationOutcome.Failed)
        {
            user.RecordFailedSignIn(options.Value.LockoutThreshold);
            await database.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Sign-in rejected for {UserId}: wrong password. Failed attempts: {FailedAttempts}. Status: {Status}.",
                user.Id, user.FailedSignInCount, user.Status);

            return ApiError.Result(ErrorCodes.InvalidCredentials, StatusCodes.Status401Unauthorized);
        }

        if (!user.CanAuthenticate)
        {
            // The password was right, so this discloses nothing the caller did not already
            // know — and the operator needs the account to say why it is refusing.
            logger.LogWarning("Sign-in refused for {UserId}: account status is {Status}.", user.Id, user.Status);

            return ApiError.Result(ErrorCodes.AccountDisabled, StatusCodes.Status403Forbidden);
        }

        if (verification == PasswordVerificationOutcome.SucceededButNeedsRehash)
        {
            // The stored verifier predates the current work factor. Re-hashing on a
            // successful sign-in is the only moment the plaintext is available to do it.
            user.SetPassword(passwordHasher.Hash(request.Password));
        }

        user.RecordSuccessfulSignIn();
        await database.SaveChangesAsync(cancellationToken);

        var (token, expiresAtUtc) = await sessions.IssueAsync(user, cancellationToken);
        context.Response.Cookies.Append(AuthCookies.Session, token, AuthCookies.ForSession(expiresAtUtc));

        logger.LogInformation("Sign-in succeeded for {UserId} with role {Role}.", user.Id, user.Role);

        return Results.Ok(new SessionResponse(user.Email, user.Role.ToString(), user.MustChangePassword));
    }

    /// <summary>Anchor for the slice's logger category.</summary>
    private sealed class SignInMarker;

    internal sealed record SignInRequest(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("password")] string? Password);
}
