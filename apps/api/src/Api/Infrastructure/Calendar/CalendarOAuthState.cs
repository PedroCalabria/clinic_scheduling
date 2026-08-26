using System.Security.Cryptography;
using System.Text;
using Clinic.Api.Infrastructure.Auth.Google;

namespace Clinic.Api.Infrastructure.Calendar;

/// <summary>
/// The pending calendar authorization: a <c>state</c> that ties the callback to this browser,
/// and where to send the professional afterwards (design K2).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> <see cref="GoogleOAuthState"/>, and the difference is not only the
/// missing nonce. That record computes a <c>Surface</c> from its return path, which decides
/// provisioning rules — whether an unknown address becomes a patient or is refused. None of
/// that applies here: the caller is already authenticated, and no user is ever created. Reusing
/// the type would carry a provisioning decision into a flow that must not make one.
/// </para>
/// <para>
/// <b>No nonce</b>, because a nonce exists to bind an ID token to the request that asked for it,
/// and this flow validates no ID token — it wants a refresh token. Carrying an unused nonce
/// would suggest a check that is not happening.
/// </para>
/// <para>
/// The open-redirect guard is <em>shared</em> rather than copied:
/// <see cref="GoogleOAuthState.SafeReturnPath"/> is a genuinely general rule about what may
/// follow a redirect, and a second copy is how one of them quietly loses the
/// protocol-relative-URL case.
/// </para>
/// </remarks>
internal sealed record CalendarOAuthState(string State, string ReturnPath)
{
    private const int TokenByteLength = 32;

    /// <summary>Where a professional lands when the flow states no destination.</summary>
    internal const string DefaultReturnPath = "/staff/calendar";

    internal static CalendarOAuthState Start(string? requestedReturnPath) =>
        new(GenerateToken(), SafeReturnPath(requestedReturnPath));

    /// <summary>Encodes the pending authorization for its short-lived cookie.</summary>
    internal string ToCookieValue() => string.Join('.', State, Base64UrlEncode(ReturnPath));

    internal static CalendarOAuthState? FromCookieValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('.');

        if (parts.Length != 2 || parts[0].Length == 0)
        {
            return null;
        }

        return new CalendarOAuthState(parts[0], SafeReturnPath(Base64UrlDecode(parts[1])));
    }

    /// <summary>Compares the callback's state to the cookie's without leaking timing.</summary>
    internal bool MatchesState(string? presented) =>
        !string.IsNullOrEmpty(presented)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(State),
            Encoding.UTF8.GetBytes(presented));

    /// <summary>
    /// Reduces a requested destination to something that cannot leave this origin, then to
    /// something inside the staff console.
    /// </summary>
    /// <remarks>
    /// Two narrowings, not one. The shared guard keeps the redirect on this origin; this method
    /// additionally refuses anything outside <c>/staff</c>, because a calendar authorization has
    /// no business returning a professional to the patient portal. The default is S2 itself,
    /// which is the only screen that starts this flow.
    /// </remarks>
    internal static string SafeReturnPath(string? requested)
    {
        var onOrigin = GoogleOAuthState.SafeReturnPath(requested);

        return onOrigin.StartsWith("/staff", StringComparison.Ordinal)
            ? onOrigin
            : DefaultReturnPath;
    }

    private static string GenerateToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));

    private static string Base64UrlEncode(string value) => Base64UrlEncode(Encoding.UTF8.GetBytes(value));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return DefaultReturnPath;
        }
    }
}
