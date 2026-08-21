using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Clinic.Api.Infrastructure.Auth.Google;

/// <summary>
/// A verified Google identity: the only thing the rest of the system learns from the
/// federated path.
/// </summary>
/// <remarks>
/// No token, no access token, and no refresh token leave this boundary. Change 2 requests
/// identity scopes only (design A6), so there is nothing else here to leak — and the type
/// makes that visible rather than relying on discipline.
/// </remarks>
internal sealed record GoogleIdentity(string Subject, string Email, bool EmailVerified, string? FullName);

/// <summary>
/// Validates a Google ID token (design A4) — the seam that makes the federated path testable
/// without a network.
/// </summary>
internal interface IGoogleIdTokenValidator
{
    /// <summary>
    /// Validates signature, issuer, audience, expiry, and the nonce bound to this sign-in.
    /// Returns null when any of that fails.
    /// </summary>
    Task<GoogleIdentity?> ValidateAsync(string idToken, string expectedNonce, CancellationToken cancellationToken);
}

/// <summary>
/// Where the keys that tokens are checked against come from.
/// </summary>
/// <remarks>
/// This is the substitution point, and it is deliberately narrower than the validator itself:
/// tests replace the KEYS, not the validation. So the signature check, the issuer check, the
/// audience check, the expiry check, and the nonce comparison in
/// <see cref="GoogleIdTokenValidator"/> all run for real against a token the test minted with
/// its own RSA key. Swapping the whole validator would have left exactly that logic untested,
/// which is the part most worth testing (design A4).
/// </remarks>
internal interface IGoogleSigningKeys
{
    Task<IReadOnlyCollection<SecurityKey>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The real validation: JWKS signature plus <c>iss</c>, <c>aud</c>, <c>exp</c>, and
/// <c>nonce</c> (03-nfr.md §2).
/// </summary>
internal sealed class GoogleIdTokenValidator(
    IGoogleSigningKeys signingKeys,
    IOptions<AuthOptions> options,
    ILogger<GoogleIdTokenValidator> logger) : IGoogleIdTokenValidator
{
    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<GoogleIdentity?> ValidateAsync(
        string idToken,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        var google = options.Value.Google;

        if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(google.ClientId))
        {
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = google.Issuer,
            ValidateIssuer = true,

            // The audience is this application's own client id. Without this check, an ID token
            // minted for a DIFFERENT Google client would be accepted here — the classic
            // confused-deputy in OIDC.
            ValidAudience = google.ClientId,
            ValidateAudience = true,

            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = await signingKeys.GetAsync(cancellationToken),

            // Google's clocks and ours are both fine; a small skew avoids spurious failures at
            // the exact expiry boundary without meaningfully extending a token's life.
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        var result = await Handler.ValidateTokenAsync(idToken, parameters);

        if (!result.IsValid)
        {
            logger.LogWarning(result.Exception, "Google ID token rejected during validation.");
            return null;
        }

        var nonce = result.Claims.TryGetValue("nonce", out var nonceClaim) ? nonceClaim as string : null;

        if (string.IsNullOrEmpty(nonce) || !FixedTimeEquals(nonce, expectedNonce))
        {
            // The nonce ties this token to the authorization request THIS browser started, so a
            // token obtained elsewhere cannot be replayed into someone else's sign-in.
            logger.LogWarning("Google ID token rejected: nonce did not match the pending sign-in.");
            return null;
        }

        var subject = result.Claims.TryGetValue("sub", out var sub) ? sub as string : null;
        var email = result.Claims.TryGetValue("email", out var mail) ? mail as string : null;

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning("Google ID token rejected: no subject or no email claim.");
            return null;
        }

        var emailVerified = result.Claims.TryGetValue("email_verified", out var verified)
            && verified switch
            {
                bool flag => flag,
                string text => bool.TryParse(text, out var parsed) && parsed,
                _ => false,
            };

        var fullName = result.Claims.TryGetValue("name", out var name) ? name as string : null;

        return new GoogleIdentity(subject, email, emailVerified, fullName);
    }

    private static bool FixedTimeEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));
}
