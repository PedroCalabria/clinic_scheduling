using System.Security.Cryptography;
using System.Text;

namespace Clinic.Api.Infrastructure.Auth.Google;

/// <summary>
/// The pending sign-in: a <c>state</c> that ties the callback to this browser, a
/// <c>nonce</c> that ties the ID token to this request, and where to send the user afterwards
/// (design A3).
/// </summary>
internal sealed record GoogleOAuthState(string State, string Nonce, string ReturnPath)
{
    private const int TokenByteLength = 32;

    /// <summary>Where a sign-in with no stated destination lands.</summary>
    internal const string DefaultReturnPath = "/";

    internal static GoogleOAuthState Start(string? requestedReturnPath) =>
        new(GenerateToken(), GenerateToken(), SafeReturnPath(requestedReturnPath));

    /// <summary>
    /// Encodes the pending sign-in for its short-lived cookie.
    /// </summary>
    /// <remarks>
    /// Neither <c>state</c> nor <c>nonce</c> is a secret from the client — both travel to
    /// Google in the authorization URL, visible in the address bar. What matters is that they
    /// are unguessable by a third party and that the cookie is cleared when consumed, which is
    /// what makes a replay fail: the second attempt finds nothing to match against.
    /// </remarks>
    internal string ToCookieValue() =>
        string.Join('.', State, Nonce, Base64UrlEncode(ReturnPath));

    internal static GoogleOAuthState? FromCookieValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('.');

        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return null;
        }

        var returnPath = Base64UrlDecode(parts[2]);

        return new GoogleOAuthState(parts[0], parts[1], SafeReturnPath(returnPath));
    }

    /// <summary>
    /// Reduces a requested destination to something that cannot leave this origin.
    /// </summary>
    /// <remarks>
    /// This is the open-redirect guard, and it is why the check is an allow-list rather than a
    /// block-list: only a single-slash-prefixed local path survives. <c>//evil.example</c> is a
    /// protocol-relative URL that a naive "starts with /" test would happily send a
    /// freshly-signed-in user to, handing over their arrival at an attacker's page.
    /// </remarks>
    internal static string SafeReturnPath(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)
            || requested[0] != '/'
            || requested.StartsWith("//", StringComparison.Ordinal)
            || requested.Contains("://", StringComparison.Ordinal)
            || requested.Contains('\\', StringComparison.Ordinal))
        {
            return DefaultReturnPath;
        }

        return requested;
    }

    /// <summary>Compares the callback's state to the cookie's without leaking timing.</summary>
    internal bool MatchesState(string? presented) =>
        !string.IsNullOrEmpty(presented)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(State),
            Encoding.UTF8.GetBytes(presented));

    private static string GenerateToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));

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
