using System.Security.Claims;
using System.Text.Json.Serialization;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DomainPasswordHasher = Clinic.Domain.Identity.IPasswordHasher;

namespace Clinic.Api.Features.Auth;

/// <summary>
/// <c>POST /api/auth/password</c> — replaces an internal account's password.
/// </summary>
/// <remarks>
/// This is the way out of the forced change the bootstrap administrator starts in
/// (design A6), and the only password-change path in this change: there is no reset-by-email
/// flow, because nothing can send mail until change 8 and scaffolding one now would be a
/// dead endpoint.
///
/// Changing a password revokes every session the user holds and issues a fresh one. That is
/// the conventional behaviour for a reason — a password change is often a response to
/// suspecting someone else has a session — and it costs nothing here because revocation is
/// immediate (design A1).
/// </remarks>
internal static class ChangePassword
{
    internal static RouteHandlerBuilder MapChangePassword(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/auth/password", HandleAsync)
            .WithName("ChangePassword");

    private static async Task<IResult> HandleAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        ClinicDbContext database,
        SessionStore sessions,
        DomainPasswordHasher passwordHasher,
        IOptions<AuthOptions> options,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return ApiError.Result(
                ErrorCodes.ValidationRequired,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["field"] = "newPassword" });
        }

        if (request.NewPassword.Length < options.Value.MinimumPasswordLength)
        {
            return ApiError.Result(
                ErrorCodes.ValidationInvalidFormat,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?>
                {
                    ["field"] = "newPassword",
                    ["minimumLength"] = options.Value.MinimumPasswordLength,
                });
        }

        var userId = principal.UserId();

        var user = await database.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == userId && candidate.DeletedAtUtc == null,
            cancellationToken);

        if (user?.PasswordHash is null)
        {
            // A federated user has no password to change. Not an error worth a new code:
            // from the caller's point of view the current password they offered is wrong.
            return ApiError.Result(ErrorCodes.InvalidCredentials, StatusCodes.Status401Unauthorized);
        }

        if (passwordHasher.Verify(user.PasswordHash, request.CurrentPassword) == PasswordVerificationOutcome.Failed)
        {
            return ApiError.Result(ErrorCodes.InvalidCredentials, StatusCodes.Status401Unauthorized);
        }

        user.SetPassword(passwordHasher.Hash(request.NewPassword));
        await database.SaveChangesAsync(cancellationToken);

        // Every existing session goes, including this one, and the caller continues on a new
        // one so they are not signed out by their own password change.
        await sessions.RevokeAllForUserAsync(user.Id, cancellationToken);

        var (token, expiresAtUtc) = await sessions.IssueAsync(user, cancellationToken);
        context.Response.Cookies.Append(AuthCookies.Session, token, AuthCookies.ForSession(expiresAtUtc));

        return Results.Ok(new SessionResponse(user.Email, user.Role.ToString(), user.MustChangePassword));
    }

    internal sealed record ChangePasswordRequest(
        [property: JsonPropertyName("currentPassword")] string? CurrentPassword,
        [property: JsonPropertyName("newPassword")] string? NewPassword);
}
