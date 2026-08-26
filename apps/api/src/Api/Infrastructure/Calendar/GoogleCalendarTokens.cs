using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clinic.Api.Infrastructure.Auth;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Infrastructure.Calendar;

/// <summary>
/// What the authorization brought back: the long-lived credential, if Google issued one, and
/// the scopes it actually granted.
/// </summary>
/// <remarks>
/// Both fields are the interesting ones, for opposite reasons. <see cref="RefreshToken"/> can be
/// absent from a completely successful exchange (design K6), and <see cref="GrantedScopes"/> can
/// omit the scope that was asked for (design K5) — so neither "it succeeded" nor "we asked for
/// it" is evidence of anything, and both have to be read.
/// </remarks>
internal sealed record CalendarTokenGrant(string? RefreshToken, IReadOnlyList<string> GrantedScopes)
{
    /// <summary>Whether the provider granted the scope this feature cannot work without.</summary>
    internal bool Includes(string scope) =>
        GrantedScopes.Any(granted => string.Equals(granted, scope, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What a check against the provider found (design K8).
/// </summary>
/// <remarks>
/// Three outcomes, and keeping <see cref="Unreachable"/> distinct from <see cref="Revoked"/> is
/// the whole point of the enum. Collapsing them would record a Google outage as a withdrawn
/// authorization — telling a professional to reconnect something that is working fine.
/// </remarks>
internal enum CalendarProbeOutcome
{
    /// <summary>The credential still works.</summary>
    Valid = 1,

    /// <summary>The provider says this authorization is gone. Believed, and recorded.</summary>
    Revoked = 2,

    /// <summary>The provider could not be asked. Says nothing about the authorization.</summary>
    Unreachable = 3,
}

/// <summary>
/// The calendar flow's half of the conversation with Google: exchanging a code for a long-lived
/// credential, checking that credential, and handing it back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="Auth.Google.GoogleTokenExchange"/>, which is the sign-in flow's
/// half.</b> The two look similar enough to merge and must not be: that one asks for an
/// <c>id_token</c> and is forbidden from storing a refresh token; this one asks for a refresh
/// token and never reads an <c>id_token</c>. One class serving both would branch on which flow
/// it is in, one mistaken branch away from a sign-in exchange coming back with a long-lived
/// credential — the shape design K2 rejects at the callback level, applied one layer down.
/// </para>
/// <para>
/// <b>Client credentials come from <see cref="AuthOptions"/>; everything else from
/// <see cref="CalendarOptions"/>.</b> It is one OAuth client asking for a second permission
/// (design K1), so the id and secret are shared while the redirect URI, scope and endpoints are
/// this flow's own.
/// </para>
/// <para>
/// A typed <see cref="HttpClient"/>, so tests replace its message handler and every path here
/// runs end to end with no network and no secrets in CI — the change-2 seam
/// (<c>00-context.md</c> §6), reused rather than reinvented. What is substituted is Google's
/// transport; the envelope, the scope check and the state machine above all run for real.
/// </para>
/// </remarks>
internal sealed class GoogleCalendarTokens(
    HttpClient httpClient,
    IOptions<AuthOptions> authOptions,
    IOptions<CalendarOptions> calendarOptions,
    ILogger<GoogleCalendarTokens> logger)
{
    /// <summary>
    /// Exchanges the authorization code for a long-lived credential and the granted scopes.
    /// </summary>
    /// <returns>The grant, or null when the exchange itself failed.</returns>
    internal async Task<CalendarTokenGrant?> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        var google = authOptions.Value.Google;
        var calendar = calendarOptions.Value;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = google.ClientId!,
            ["client_secret"] = google.ClientSecret!,
            // This flow's own redirect URI, and Google checks it matches the one the
            // authorization request carried. Sending the sign-in flow's would fail here rather
            // than at the consent screen, which is a confusing place to learn about it.
            ["redirect_uri"] = calendar.RedirectUri!,
            ["grant_type"] = "authorization_code",
        });

        using var response = await httpClient.PostAsync(calendar.TokenEndpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Google's body can name the reason. It is logged (correlated) and never returned:
            // the caller gets a catalogue code, not a provider message.
            logger.LogWarning(
                "Calendar authorization exchange failed with status {StatusCode}.",
                (int)response.StatusCode);

            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);

        if (payload is null)
        {
            logger.LogWarning("Calendar authorization exchange returned no readable payload.");
            return null;
        }

        // Space-separated, per RFC 6749. An absent scope field is treated as "granted nothing",
        // which is the safe reading: it makes the caller refuse rather than assume.
        var scopes = (payload.Scope ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new CalendarTokenGrant(payload.RefreshToken, scopes);
    }

    /// <summary>
    /// Asks the provider whether a stored credential still works, reading no calendar content.
    /// </summary>
    /// <remarks>
    /// A refresh-token grant is the cheapest question that has the answer we want. It touches no
    /// calendar data at all, which is what makes it defensible to run from a screen: checking
    /// whether we still have permission should not require exercising the permission.
    /// </remarks>
    internal async Task<CalendarProbeOutcome> ProbeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var google = authOptions.Value.Google;
        var calendar = calendarOptions.Value;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = google.ClientId!,
            ["client_secret"] = google.ClientSecret!,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        });

        HttpResponseMessage response;

        try
        {
            response = await httpClient.PostAsync(calendar.TokenEndpoint, content, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // Transport failure. Says nothing about the authorization, and is reported as saying
            // nothing — see the enum's remarks.
            logger.LogWarning(exception, "Calendar credential check could not reach the provider.");
            return CalendarProbeOutcome.Unreachable;
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Calendar credential check timed out.");
            return CalendarProbeOutcome.Unreachable;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return CalendarProbeOutcome.Valid;
            }

            // The one status that means the grant is gone. Google returns 400 with
            // error=invalid_grant for a revoked or expired refresh token; a 401 says the client
            // credentials are wrong, which is our misconfiguration and not their revocation.
            if (response.StatusCode == HttpStatusCode.BadRequest
                && await IsInvalidGrantAsync(response, cancellationToken))
            {
                return CalendarProbeOutcome.Revoked;
            }

            logger.LogWarning(
                "Calendar credential check failed with status {StatusCode}, which does not " +
                "indicate revocation.",
                (int)response.StatusCode);

            return CalendarProbeOutcome.Unreachable;
        }
    }

    /// <summary>
    /// Hands the grant back to the provider (design K9).
    /// </summary>
    /// <returns>
    /// True when the authorization is gone from the provider's side — <b>including</b> when it
    /// was already gone, which is the common "they revoked it in Google first, then pressed
    /// disconnect here" path. False when the provider could not be reached, which the caller
    /// reports rather than swallows.
    /// </returns>
    internal async Task<bool> RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var calendar = calendarOptions.Value;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = refreshToken,
        });

        try
        {
            using var response = await httpClient.PostAsync(calendar.RevocationEndpoint, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // Already revoked. Idempotent by intent: the caller asked for the grant to be gone,
            // and it is gone.
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                logger.LogInformation(
                    "Calendar revocation reported the grant was already invalid, which is success.");

                return true;
            }

            logger.LogWarning(
                "Calendar revocation failed with status {StatusCode}.",
                (int)response.StatusCode);

            return false;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Calendar revocation could not reach the provider.");
            return false;
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Calendar revocation timed out.");
            return false;
        }
    }

    private static async Task<bool> IsInvalidGrantAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorPayload>(cancellationToken);

            return string.Equals(body?.Error, "invalid_grant", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            // A 400 we cannot read is not evidence of revocation. Erring towards "unreachable"
            // keeps a working connection connected, which is the failure worth preferring.
            return false;
        }
    }

    /// <summary>
    /// <c>id_token</c> is deliberately not modelled: this flow establishes no identity, and a
    /// field nobody reads is an invitation for somebody to start.
    /// </summary>
    private sealed record TokenResponse(
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("scope")] string? Scope);

    private sealed record ErrorPayload(
        [property: JsonPropertyName("error")] string? Error);
}
