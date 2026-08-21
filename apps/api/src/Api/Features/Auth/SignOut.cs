using Clinic.Api.Infrastructure.Auth;

namespace Clinic.Api.Features.Auth;

/// <summary>
/// <c>POST /api/auth/sign-out</c> — revokes this session and clears the cookie.
/// </summary>
/// <remarks>
/// Revoking the row is what makes sign-out real: clearing the cookie alone would leave a
/// token that still authenticates anyone who kept a copy. Because the row is the authority
/// (design A1), this takes effect on the very next request rather than whenever the session
/// would have expired.
/// </remarks>
internal static class SignOut
{
    internal static RouteHandlerBuilder MapSignOut(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/auth/sign-out", HandleAsync)
            .WithName("SignOut");

    private static async Task<IResult> HandleAsync(
        SessionStore sessions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var token = context.Request.Cookies[AuthCookies.Session];

        if (!string.IsNullOrEmpty(token))
        {
            await sessions.RevokeAsync(token, cancellationToken);
        }

        AuthCookies.DeleteSession(context.Response);

        return Results.NoContent();
    }
}
