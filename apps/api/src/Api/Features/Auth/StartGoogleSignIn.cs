using Microsoft.AspNetCore.WebUtilities;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Auth.Google;
using Clinic.Api.Infrastructure.Errors;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Features.Auth;

/// <summary>
/// <c>GET /api/auth/google/start</c> — sends the browser to Google (P1, S0).
/// </summary>
/// <remarks>
/// <para>
/// A server-side redirect rather than a client-built URL, because the <c>state</c> and
/// <c>nonce</c> have to be generated somewhere the browser cannot influence, and the cookie
/// that remembers them has to be <c>HttpOnly</c>.
/// </para>
/// <para>
/// The scope list is the decision worth noticing: <c>openid email profile</c> and nothing
/// else. No calendar scope and no <c>access_type=offline</c>, so this flow cannot come back
/// with a refresh token even by accident. The professional's calendar scope is requested in
/// change 6 through incremental authorization (design A6) — which is also why change 6 must
/// send <c>include_granted_scopes=true</c>, or its consent screen will silently drop the
/// identity grant this one obtained.
/// </para>
/// </remarks>
internal static class StartGoogleSignIn
{
    /// <summary>How long a sign-in may sit half-finished before its state expires.</summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    internal static RouteHandlerBuilder MapStartGoogleSignIn(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/auth/google/start", HandleAsync)
            .AllowAnonymous()
            .WithName("StartGoogleSignIn");

    private static IResult HandleAsync(
        HttpContext context,
        IOptions<AuthOptions> options,
        TimeProvider clock,
        ILogger<StartGoogleSignInMarker> logger,
        string? returnTo = null)
    {
        var google = options.Value.Google;

        if (!google.IsConfigured)
        {
            // Absent configuration is an operator fact, not a caller mistake (design A14), so it
            // gets its own code. Reported the same way as a callback failure — a redirect back
            // to the sign-in surface — because this endpoint is also reached by a top-level
            // navigation, and a JSON body in the address bar is nobody's idea of an error
            // message. The log line is what the operator needs.
            logger.LogWarning("Google sign-in requested but no Google client is configured.");

            return Results.Redirect(QueryHelpers.AddQueryString(
                GoogleOAuthState.SafeReturnPath(returnTo),
                CompleteGoogleSignIn.ErrorQueryParameter,
                ErrorCodes.GoogleUnavailable));
        }

        var pending = GoogleOAuthState.Start(returnTo);

        context.Response.Cookies.Append(
            AuthCookies.OAuthState,
            pending.ToCookieValue(),
            AuthCookies.ForOAuthState(clock.GetUtcNow().Add(StateLifetime)));

        var authorizationUrl = QueryHelpers.AddQueryString(google.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = google.ClientId,
            ["redirect_uri"] = google.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = pending.State,
            ["nonce"] = pending.Nonce,
            // Ask every time rather than silently reusing a session Google already holds: this
            // endpoint is reached by a deliberate click on "sign in".
            ["prompt"] = "select_account",
        });

        return Results.Redirect(authorizationUrl);
    }

    /// <summary>Anchor for the slice's logger category.</summary>
    private sealed class StartGoogleSignInMarker;
}
