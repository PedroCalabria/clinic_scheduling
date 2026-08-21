using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Infrastructure.Auth.Google;

/// <summary>
/// Exchanges an authorization code for an ID token at Google's token endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The second half of the A4 seam. This goes through a typed <see cref="HttpClient"/>, so a
/// test replaces its message handler and the callback runs end to end with no network — while
/// the validation of what comes back stays real (see <see cref="GoogleIdTokenValidator"/>).
/// </para>
/// <para>
/// The request asks for nothing but the code exchange: no <c>access_type=offline</c>, so
/// Google returns no refresh token, and this change therefore stores no long-lived
/// credential and needs no token-encryption key. That surface arrives with change 6, which is
/// the capability that actually uses it (design A6).
/// </para>
/// </remarks>
internal sealed class GoogleTokenExchange(
    HttpClient httpClient,
    IOptions<AuthOptions> options,
    ILogger<GoogleTokenExchange> logger)
{
    /// <summary>Returns the ID token, or null when the exchange failed.</summary>
    public async Task<string?> ExchangeForIdTokenAsync(string code, CancellationToken cancellationToken)
    {
        var google = options.Value.Google;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = google.ClientId!,
            ["client_secret"] = google.ClientSecret!,
            ["redirect_uri"] = google.RedirectUri!,
            ["grant_type"] = "authorization_code",
        });

        using var response = await httpClient.PostAsync(google.TokenEndpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The body can contain Google's error description. It is logged (correlated) and
            // never returned: the caller gets a catalogue code, not a provider message.
            logger.LogWarning(
                "Google token exchange failed with status {StatusCode}.",
                (int)response.StatusCode);

            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);

        if (string.IsNullOrWhiteSpace(payload?.IdToken))
        {
            logger.LogWarning("Google token exchange returned no id_token.");
            return null;
        }

        return payload.IdToken;
    }

    /// <summary>
    /// Only <c>id_token</c> is read. <c>access_token</c> and <c>refresh_token</c> are not
    /// modelled here on purpose — nothing in this change may store them.
    /// </summary>
    private sealed record TokenResponse(
        [property: JsonPropertyName("id_token")] string? IdToken);
}
