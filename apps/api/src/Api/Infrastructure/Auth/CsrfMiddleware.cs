using System.Security.Cryptography;
using Clinic.Api.Infrastructure.Errors;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Double-submit request-forgery protection for the cookie-authenticated API (design A3).
/// </summary>
/// <remarks>
/// <para>
/// This is the API's own defence, and it is NOT the same mechanism as the <c>state</c> and
/// <c>nonce</c> that protect the Google redirect flow. Conflating the two is the usual way a
/// real hole gets left: <c>state</c> proves a callback belongs to a sign-in this browser
/// started, and says nothing about whether a later state-changing request was intended.
/// </para>
/// <para>
/// How it works: a random token is issued in a readable cookie, and every unsafe request must
/// echo it in a header. A cross-site attacker can cause the browser to attach the session
/// cookie, but same-origin policy stops them reading the CSRF cookie to build the matching
/// header — that asymmetry is the entire protection. <c>SameSite=Lax</c> on the session
/// cookie is the second layer, not the first, because it does not cover top-level
/// navigations.
/// </para>
/// <para>
/// ASP.NET's antiforgery was the alternative. It is built around server-rendered views and
/// needs a token-priming endpoint plus per-session server state to be usable from an SPA;
/// double-submit is stateless and natural for a fetch client that already sets headers.
/// </para>
/// <para>
/// The token is issued on safe requests, so the frontend always holds one before it can
/// possibly need it — both apps call <c>GET /api/auth/session</c> on boot.
/// </para>
/// </remarks>
internal sealed class CsrfMiddleware(RequestDelegate next)
{
    internal const string HeaderName = "X-CSRF-Token";

    private const int TokenByteLength = 32;

    private static readonly string[] SafeMethods =
        [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options, HttpMethods.Trace];

    public async Task InvokeAsync(HttpContext context)
    {
        var cookieToken = context.Request.Cookies[AuthCookies.Csrf];

        if (IsSafe(context.Request.Method))
        {
            if (string.IsNullOrEmpty(cookieToken))
            {
                context.Response.Cookies.Append(AuthCookies.Csrf, GenerateToken(), AuthCookies.ForCsrf());
            }

            await next(context);
            return;
        }

        var headerToken = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrEmpty(cookieToken)
            || string.IsNullOrEmpty(headerToken)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(cookieToken),
                System.Text.Encoding.UTF8.GetBytes(headerToken)))
        {
            // auth.forbidden rather than a code of its own: the catalogue's rule is one code
            // per user-meaningful failure, not one per throw site, and to a user this is the
            // same "that was not allowed" as any other refusal. A dedicated code would only
            // serve debugging, which the logs already cover.
            await ApiError.WriteAsync(
                context.Response,
                ErrorCodes.Forbidden,
                StatusCodes.Status403Forbidden,
                cancellationToken: context.RequestAborted);

            return;
        }

        await next(context);
    }

    private static bool IsSafe(string method) =>
        SafeMethods.Any(safe => string.Equals(safe, method, StringComparison.OrdinalIgnoreCase));

    private static string GenerateToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenByteLength));
}
