namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// The cookies this API sets, and the flags they are set with (design A2).
/// </summary>
/// <remarks>
/// <para>
/// One place for all three, because the differences between them are the interesting part
/// and they are only visible side by side.
/// </para>
/// <para>
/// <c>Secure</c> is unconditional, with no environment branch. Browsers treat
/// <c>localhost</c> as a secure context and send <c>Secure</c> cookies over plain HTTP
/// there, so a conditional flag would buy nothing and add a way to ship the insecure
/// branch — the same reasoning as one Caddyfile for both environments (change 1, design D9).
/// The failure mode to know about: serving the app over plain HTTP on a hostname that is
/// NOT localhost silently drops these cookies, and the symptom ("sign-in succeeds, then
/// I am immediately signed out") points nowhere near the cause.
/// </para>
/// <para>
/// <c>SameSite=Lax</c> rather than <c>Strict</c> is a decision, not a default. Under
/// <c>Strict</c> the OAuth state cookie would not be returned on the cross-site navigation
/// back from Google, and sign-in would break in a way that looks like a nonce bug. <c>Lax</c>
/// covers exactly that top-level-navigation case and nothing more — which is also why it is
/// not by itself CSRF protection for the API (see <see cref="CsrfMiddleware"/>).
/// </para>
/// </remarks>
internal static class AuthCookies
{
    /// <summary>Holds the opaque session token. Unreadable to scripts.</summary>
    internal const string Session = "clinic.session";

    /// <summary>
    /// Holds the CSRF token. Deliberately readable by scripts — the frontend has to echo it
    /// in a header, and a same-origin script reading it is the entire mechanism.
    /// </summary>
    internal const string Csrf = "clinic.csrf";

    /// <summary>Holds <c>state</c> and <c>nonce</c> for one in-flight Google sign-in.</summary>
    internal const string OAuthState = "clinic.oauth";

    /// <summary>Path the OAuth state cookie is scoped to — it has no business anywhere else.</summary>
    internal const string OAuthStatePath = "/api/auth";

    /// <summary>
    /// Holds <c>state</c> for one in-flight calendar authorization (change 6a, design K2).
    /// </summary>
    /// <remarks>
    /// A separate cookie from <see cref="OAuthState"/>, scoped to a separate path, and this is
    /// the cheapest place to see why. The two flows have opposite obligations — the sign-in
    /// callback establishes a session and may create a user, the calendar callback must do
    /// neither — so a shared cookie would let one flow's half-finished state be presented to the
    /// other's callback. Separate names and separate paths make that impossible rather than
    /// carefully avoided.
    ///
    /// It carries <b>no nonce</b>: a nonce binds an ID token to a request, and this flow
    /// validates no ID token. Carrying one would be cargo.
    /// </remarks>
    internal const string CalendarState = "clinic.calendar";

    /// <summary>Path the calendar state cookie is scoped to.</summary>
    internal const string CalendarStatePath = "/api/calendar";

    internal static CookieOptions ForSession(DateTimeOffset expiresAtUtc) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        // Host-only: no Domain attribute, so a sibling subdomain cannot be handed this cookie.
        Expires = expiresAtUtc,
    };

    internal static CookieOptions ForCsrf() => new()
    {
        // Readable on purpose — see the constant above.
        HttpOnly = false,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        // Session cookie in the browser sense: it lives as long as the tab does, and a new
        // one is issued on the next safe request if it is missing.
        Expires = null,
    };

    internal static CookieOptions ForOAuthState(DateTimeOffset expiresAtUtc) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = OAuthStatePath,
        Expires = expiresAtUtc,
    };

    /// <summary>
    /// Deletion has to repeat the flags the cookie was set with, or the browser treats it as
    /// a different cookie and quietly keeps the original.
    /// </summary>
    internal static void DeleteSession(HttpResponse response) =>
        response.Cookies.Delete(Session, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });

    internal static CookieOptions ForCalendarState(DateTimeOffset expiresAtUtc) => new()
    {
        HttpOnly = true,
        Secure = true,
        // Lax for the same reason the sign-in state cookie needs it: the browser comes back from
        // Google by top-level navigation, and under Strict this cookie would not be sent — the
        // flow would fail looking exactly like a state bug.
        SameSite = SameSiteMode.Lax,
        Path = CalendarStatePath,
        Expires = expiresAtUtc,
    };

    internal static void DeleteCalendarState(HttpResponse response) =>
        response.Cookies.Delete(CalendarState, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = CalendarStatePath,
        });

    internal static void DeleteOAuthState(HttpResponse response) =>
        response.Cookies.Delete(OAuthState, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = OAuthStatePath,
        });
}
