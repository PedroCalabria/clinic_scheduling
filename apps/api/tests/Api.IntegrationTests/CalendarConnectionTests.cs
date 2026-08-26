using System.Net;
using System.Text.Json;
using Clinic.Api.Features.CalendarSync;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Calendar;
using Clinic.Domain.Calendar;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// S2 — connecting, checking and withdrawing a professional's calendar (change 6a).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test here talks to a stubbed Google</b>, which is what keeps CI free of secrets and
/// is also the honest limit of this tier: it proves the code does the right thing with the
/// response it was handed, and nothing about whether Google hands back that response. The
/// consent screen, the granular tickbox, the second redirect URI and a real <c>invalid_grant</c>
/// are the validation guide's, and cannot be moved here.
/// </para>
/// <para>
/// What <em>can</em> live here is every decision made from a response — and the ones worth
/// finding first are the two that look like success:
/// <see cref="A_declined_calendar_scope_stores_nothing"/> and
/// <see cref="An_unreachable_provider_is_not_recorded_as_a_revocation"/>.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CalendarConnectionTests
{
    private const string CalendarPath = "/api/calendar";

    private readonly ApiFixture fixture;

    /// <summary>
    /// Resets the shared double, because the database is not reset between tests.
    /// </summary>
    /// <remarks>
    /// Two consequences of that fixture design, and both bit before they were handled. The
    /// double is shared mutable state, so a test that stages an unreachable provider would leak
    /// it into whatever ran next; xunit builds the test class once per test, so undoing it here
    /// is exactly per-test. And every assertion below is scoped to the professional or user it
    /// created — never to a whole table — which is the convention the other suites in this
    /// project already follow.
    /// </remarks>
    public CalendarConnectionTests(ApiFixture fixture)
    {
        this.fixture = fixture;

        fixture.Calendar.NextRefreshToken = $"1//refresh-{Guid.NewGuid():N}";
        fixture.Calendar.NextGrantedScope = $"openid email profile {CalendarTestDouble.CalendarScope}";
        fixture.Calendar.FailExchange = false;
        fixture.Calendar.RefreshOutcome = CalendarTestDouble.RefreshResult.Valid;
        fixture.Calendar.RevokeOutcome = CalendarTestDouble.RevokeResult.Accepted;
        fixture.Calendar.Revoked.Clear();
    }

    // --- The authorization request ------------------------------------------------------

    [Fact]
    public async Task The_authorization_request_retains_the_identity_grant_and_asks_for_offline_access()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        var response = await client.GetAsync($"{CalendarPath}/connect");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query);

        // THE assertion of this change's first half. Without include_granted_scopes, Google
        // issues a grant covering the calendar scope alone and silently replaces the identity
        // grant obtained at sign-in — nothing breaks visibly, and the damage surfaces later
        // somewhere unrelated. StartGoogleSignIn has warned about this in a comment since
        // change 2; this is the line that makes it a fact (design K1).
        Assert.Equal("true", query["include_granted_scopes"]);

        // A refresh token is the entire point of the flow, and prompt=consent is the first of
        // the two guards that one actually arrives (design K6).
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);

        Assert.Equal(CalendarTestDouble.CalendarScope, query["scope"]);
        Assert.Equal("code", query["response_type"]);
        Assert.False(string.IsNullOrWhiteSpace(query["state"]));

        // No nonce: this flow validates no ID token, and carrying one would suggest a check
        // that is not happening (design K2).
        Assert.Null(query["nonce"]);
    }

    [Fact]
    public async Task The_sign_in_flow_is_unchanged_by_this_change()
    {
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/api/auth/google/start");
        var location = response.Headers.Location!.ToString();

        // The boundary change 2 asserted, re-asserted from the other side now that a flow
        // exists which does ask for these. If this ever fails, the two flows have merged.
        Assert.DoesNotContain("calendar", location, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_type", location, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("include_granted_scopes", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_state_cookie_is_this_flows_own_and_is_http_only()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        var response = await client.GetAsync($"{CalendarPath}/connect");
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();

        var calendarCookie = cookies.Single(value =>
            value.StartsWith($"{AuthCookies.CalendarState}=", StringComparison.Ordinal));

        Assert.Contains("httponly", calendarCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"path={AuthCookies.CalendarStatePath}", calendarCookie, StringComparison.OrdinalIgnoreCase);

        // The sign-in flow's cookie is untouched by this flow — separate names, separate paths,
        // so one flow's half-finished state can never be presented to the other's callback.
        Assert.DoesNotContain(cookies, value =>
            value.StartsWith($"{AuthCookies.OAuthState}=", StringComparison.Ordinal));
    }

    // --- The callback -------------------------------------------------------------------

    [Fact]
    public async Task A_completed_authorization_stores_a_sealed_credential_and_a_consent()
    {
        var actor = await ConfiguredProfessionalAsync();
        var (client, user) = (actor.Client, actor.User);
        using var _client = client;

        fixture.Calendar.NextRefreshToken = "1//google-refresh-token-for-storage";

        var response = await CompleteAsync(client);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(CalendarEndpoints.ConnectedQueryParameter, response.Headers.Location!.ToString(), StringComparison.Ordinal);

        await fixture.WithDatabaseAsync(async database =>
        {
            var connection = await Mine(database, actor).SingleAsync();

            Assert.Equal(CalendarConnectionStatus.Connected, connection.Status);
            Assert.Equal("primary", connection.TargetCalendarId);
            Assert.Equal(CalendarProvider.Google, connection.Provider);

            var consent = await database.Consents.SingleAsync(candidate =>
                candidate.UserId == user.Id && candidate.Type == ConsentType.CalendarSync);

            Assert.True(consent.IsActive);
            Assert.False(string.IsNullOrWhiteSpace(consent.Version));
        });
    }

    [Fact]
    public async Task What_is_stored_is_not_the_token_google_returned()
    {
        // The single assertion this whole change exists to make true (design K3), read from the
        // column rather than from a DTO — the property that matters is what is at rest.
        const string Token = "1//plaintext-refresh-token-that-must-not-be-stored";

        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        fixture.Calendar.NextRefreshToken = Token;

        await CompleteAsync(client);

        await fixture.WithDatabaseAsync(async database =>
        {
            var stored = await Mine(database, actor)
                .Select(connection => connection.SealedCredential)
                .SingleAsync();

            Assert.NotNull(stored);
            Assert.NotEqual(Token, stored);
            Assert.DoesNotContain("plaintext", stored, StringComparison.OrdinalIgnoreCase);

            // Carries its scheme version, so a future rotation is additive rather than an
            // incident: a blob with no room to say how it was made can only be rotated by
            // guessing which key made it.
            Assert.StartsWith("v1.", stored, StringComparison.Ordinal);

            // And it is genuinely the token, not merely something else — proved by opening it
            // with the same key the app used.
            var protector = new CalendarTokenProtector(Options.Create(new CalendarOptions
            {
                TokenEncryptionKey = ApiFixture.CalendarEncryptionKey,
            }));

            Assert.Equal(Token, protector.Open(stored));
        });
    }

    [Fact]
    public async Task A_declined_calendar_scope_stores_nothing()
    {
        // The most likely real-world failure in this change, and invisible unless asked about:
        // Google's consent screen is granular, so a professional can approve the request and
        // untick calendar access while the token response stays perfectly valid (design K5).
        var actor = await ConfiguredProfessionalAsync();
        var (client, user) = (actor.Client, actor.User);
        using var _client = client;

        fixture.Calendar.NextGrantedScope = "openid email profile";

        var response = await CompleteAsync(client);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(ErrorCodesForTest.ScopeDeclined, response.Headers.Location!.ToString(), StringComparison.Ordinal);

        await fixture.WithDatabaseAsync(async database =>
        {
            Assert.Empty(await Mine(database, actor).ToListAsync());

            Assert.Empty(await database.Consents
                .Where(consent => consent.UserId == user.Id && consent.Type == ConsentType.CalendarSync)
                .ToListAsync());
        });
    }

    [Fact]
    public async Task Declining_is_reported_differently_from_revoking()
    {
        // Same remedy, different sentence: "you declined" invites granting permission, "it was
        // revoked" invites reconnecting. Reporting one as the other sends the professional to
        // the wrong action, which is why they are separate codes.
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        fixture.Calendar.NextGrantedScope = "openid email profile";

        var declined = await CompleteAsync(client);
        var location = declined.Headers.Location!.ToString();

        Assert.Contains(ErrorCodesForTest.ScopeDeclined, location, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorCodesForTest.ConsentRevoked, location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_callback_whose_state_was_already_consumed_is_refused()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        var state = await StartAsync(client);

        var first = await client.GetAsync($"{CalendarPath}/connect/callback?code=code-1&state={state}");
        Assert.Contains(CalendarEndpoints.ConnectedQueryParameter, first.Headers.Location!.ToString(), StringComparison.Ordinal);

        // The cookie was cleared when it was consumed, so the replay finds nothing to match
        // against and is refused before any exchange is attempted.
        var replay = await client.GetAsync($"{CalendarPath}/connect/callback?code=code-1&state={state}");

        Assert.Contains(ErrorCodesForTest.GoogleFailed, replay.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_callback_with_mismatched_or_absent_state_is_refused()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        await StartAsync(client);

        var mismatched = await client.GetAsync($"{CalendarPath}/connect/callback?code=code-1&state=not-the-one");
        Assert.Contains(ErrorCodesForTest.GoogleFailed, mismatched.Headers.Location!.ToString(), StringComparison.Ordinal);

        await StartAsync(client);

        var absent = await client.GetAsync($"{CalendarPath}/connect/callback?code=code-1");
        Assert.Contains(ErrorCodesForTest.GoogleFailed, absent.Headers.Location!.ToString(), StringComparison.Ordinal);

        await fixture.WithDatabaseAsync(async database =>
            Assert.Empty(await Mine(database, actor).ToListAsync()));
    }

    [Fact]
    public async Task The_callback_establishes_no_session_and_creates_no_user()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        // Snapshot immediately before the callback rather than before the setup, so the number
        // covers exactly the request under test.
        var usersBefore = 0;
        await fixture.WithDatabaseAsync(async database => usersBefore = await database.Users.CountAsync());

        var response = await CompleteAsync(client);

        // The sign-in callback one folder away does exactly these two things. This one must do
        // neither, and it cannot: the route requires an authenticated professional, so there is
        // nobody to provision and nothing to sign in (design K2).
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            value => value.StartsWith($"{AuthCookies.Session}=", StringComparison.Ordinal));

        await fixture.WithDatabaseAsync(async database =>
            Assert.Equal(usersBefore, await database.Users.CountAsync()));
    }

    [Fact]
    public async Task An_authorization_returning_no_credential_keeps_the_one_already_held()
    {
        // Google issues a refresh token only on the first grant for a client/user pair, so this
        // is the ordinary shape of a reconnection — not an error (design K6).
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        fixture.Calendar.NextRefreshToken = "1//the-original-token";
        await CompleteAsync(client);

        var original = await StoredCredentialAsync(actor);

        fixture.Calendar.NextRefreshToken = null;
        var response = await CompleteAsync(client);

        Assert.Contains(CalendarEndpoints.ConnectedQueryParameter, response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(original, await StoredCredentialAsync(actor));
    }

    [Fact]
    public async Task An_authorization_returning_no_credential_with_none_held_is_refused()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        fixture.Calendar.NextRefreshToken = null;

        var response = await CompleteAsync(client);

        // Recording a connection here would mean a status of "connected" that 6b could never
        // dispatch against.
        Assert.Contains(ErrorCodesForTest.ConnectFailed, response.Headers.Location!.ToString(), StringComparison.Ordinal);

        await fixture.WithDatabaseAsync(async database =>
            Assert.Empty(await Mine(database, actor).ToListAsync()));
    }

    [Fact]
    public async Task Reconnecting_updates_the_one_connection_rather_than_adding_a_second()
    {
        var actor = await ConfiguredProfessionalAsync();
        var (client, user) = (actor.Client, actor.User);
        using var _client = client;

        await CompleteAsync(client);

        fixture.Calendar.NextRefreshToken = "1//a-second-token";
        await CompleteAsync(client);

        await fixture.WithDatabaseAsync(async database =>
        {
            // One row, guaranteed by a unique index rather than by this handler being careful.
            Assert.Single(await Mine(database, actor).ToListAsync());

            // And one consent, because granting the same version twice is not a second legal fact.
            Assert.Single(await database.Consents
                .Where(consent => consent.UserId == user.Id && consent.Type == ConsentType.CalendarSync)
                .ToListAsync());
        });
    }

    // --- Reading and checking -----------------------------------------------------------

    [Fact]
    public async Task A_professional_who_never_connected_reads_a_state_rather_than_an_error()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        var response = await client.GetAsync($"{CalendarPath}/connection");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);

        Assert.False(body.GetProperty("connected").GetBoolean());
        Assert.Equal("NotConnected", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_status_is_reported_with_when_it_was_observed_and_no_credential()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        await CompleteAsync(client);

        var response = await client.GetAsync($"{CalendarPath}/connection");
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;

        Assert.True(body.GetProperty("connected").GetBoolean());
        Assert.Equal("Connected", body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("stateObservedAtUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("connectedAtUtc").ValueKind);

        // Asserted against the serialized body rather than the DTO's shape, because what matters
        // is what goes over the wire. No sealed envelope, no token, nothing resembling either.
        Assert.DoesNotContain("refresh", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v1.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_check_that_finds_the_grant_revoked_records_it_and_offers_reconnection()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        await CompleteAsync(client);

        fixture.Calendar.RefreshOutcome = CalendarTestDouble.RefreshResult.InvalidGrant;

        var response = await client.PostAsync($"{CalendarPath}/connection/check");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodesForTest.ConsentRevoked, await CodeAsync(response));

        await fixture.WithDatabaseAsync(async database =>
        {
            var connection = await Mine(database, actor).SingleAsync();

            Assert.Equal(CalendarConnectionStatus.Revoked, connection.Status);

            // The material is kept, so "your permission lapsed" stays distinguishable from
            // "you never connected".
            Assert.NotNull(connection.SealedCredential);
        });
    }

    [Fact]
    public async Task An_unreachable_provider_is_not_recorded_as_a_revocation()
    {
        // The test that keeps a Google outage from telling every professional to reconnect a
        // connection that is fine (design K8).
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        await CompleteAsync(client);

        var observedBefore = await ObservedAtAsync(actor);

        fixture.Calendar.RefreshOutcome = CalendarTestDouble.RefreshResult.Unreachable;

        var response = await client.PostAsync($"{CalendarPath}/connection/check");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodesForTest.SyncFailed, await CodeAsync(response));

        await fixture.WithDatabaseAsync(async database =>
        {
            var connection = await Mine(database, actor).SingleAsync();

            Assert.Equal(CalendarConnectionStatus.Connected, connection.Status);

            // Not merely the status: the observation moment is untouched too, so the screen
            // keeps saying how long it has actually been since anybody knew.
            Assert.Equal(observedBefore, connection.StateObservedAtUtc);
        });
    }

    [Fact]
    public async Task A_bad_request_that_is_not_an_invalid_grant_is_not_a_revocation_either()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        await CompleteAsync(client);

        fixture.Calendar.RefreshOutcome = CalendarTestDouble.RefreshResult.OtherBadRequest;

        var response = await client.PostAsync($"{CalendarPath}/connection/check");

        // A 401/400 about the CLIENT credentials is our misconfiguration, not their revocation.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await fixture.WithDatabaseAsync(async database =>
            Assert.Equal(CalendarConnectionStatus.Connected, (await Mine(database, actor).SingleAsync()).Status));
    }

    [Fact]
    public async Task Checking_without_a_connection_asks_the_provider_nothing()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        fixture.Calendar.RefreshOutcome = CalendarTestDouble.RefreshResult.Unreachable;

        // If the handler reached the provider at all, the double would throw rather than let
        // this return a clean 422.
        var response = await client.PostAsync($"{CalendarPath}/connection/check");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodesForTest.NotConnected, await CodeAsync(response));
    }

    // --- Withdrawal ---------------------------------------------------------------------

    [Fact]
    public async Task Disconnecting_withdraws_the_consent_the_credential_and_the_grant()
    {
        var actor = await ConfiguredProfessionalAsync();
        var (client, user) = (actor.Client, actor.User);
        using var _client = client;

        fixture.Calendar.NextRefreshToken = "1//token-to-hand-back";
        await CompleteAsync(client);

        var response = await client.PostAsync($"{CalendarPath}/connection/disconnect");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("revokedAtProvider").GetBoolean());

        // Handed back to Google, in plaintext form — which also proves the stored envelope was
        // opened rather than sent along as-is.
        Assert.Contains("1//token-to-hand-back", fixture.Calendar.Revoked);

        await fixture.WithDatabaseAsync(async database =>
        {
            var connection = await Mine(database, actor).SingleAsync();

            // The row survives (I10); the key to their calendar does not.
            Assert.Equal(CalendarConnectionStatus.Disconnected, connection.Status);
            Assert.Null(connection.SealedCredential);

            var consent = await database.Consents.SingleAsync(candidate =>
                candidate.UserId == user.Id && candidate.Type == ConsentType.CalendarSync);

            Assert.False(consent.IsActive);
            Assert.NotNull(consent.RevokedAtUtc);
        });
    }

    [Fact]
    public async Task A_failed_revocation_still_withdraws_locally_and_says_so()
    {
        // The professional asked to withdraw. Refusing would leave them connected against their
        // stated wish, with a retry button that keeps failing while Google is down (design K9).
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        await CompleteAsync(client);

        fixture.Calendar.RevokeOutcome = CalendarTestDouble.RevokeResult.Unreachable;

        var response = await client.PostAsync($"{CalendarPath}/connection/disconnect");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Not an unqualified success: the screen has to be able to say the grant may still be
        // listed in their Google account.
        Assert.False((await ReadAsync(response)).GetProperty("revokedAtProvider").GetBoolean());

        await fixture.WithDatabaseAsync(async database =>
        {
            var connection = await Mine(database, actor).SingleAsync();

            Assert.Equal(CalendarConnectionStatus.Disconnected, connection.Status);
            Assert.Null(connection.SealedCredential);
        });
    }

    [Fact]
    public async Task Disconnecting_an_already_revoked_grant_succeeds()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        await CompleteAsync(client);

        // They revoked it in Google first, then pressed disconnect here. Google answers 400 for
        // an already-invalid token, and the caller asked for the grant to be gone — it is gone.
        fixture.Calendar.RevokeOutcome = CalendarTestDouble.RevokeResult.AlreadyInvalid;

        var response = await client.PostAsync($"{CalendarPath}/connection/disconnect");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("revokedAtProvider").GetBoolean());
    }

    [Fact]
    public async Task Reconnecting_after_a_withdrawal_records_a_second_consent_and_keeps_the_first()
    {
        var actor = await ConfiguredProfessionalAsync();
        var (client, user) = (actor.Client, actor.User);
        using var _client = client;

        await CompleteAsync(client);
        await client.PostAsync($"{CalendarPath}/connection/disconnect");

        fixture.Calendar.NextRefreshToken = "1//a-fresh-token-after-withdrawal";
        await CompleteAsync(client);

        await fixture.WithDatabaseAsync(async database =>
        {
            var consents = await database.Consents
                .Where(consent => consent.UserId == user.Id && consent.Type == ConsentType.CalendarSync)
                .OrderBy(consent => consent.GrantedAtUtc)
                .ToListAsync();

            // "Consented, withdrew, consented again" is three facts. Un-revoking would erase the
            // middle one — the rule identity-session established, meeting its second consent type.
            Assert.Equal(2, consents.Count);
            Assert.NotNull(consents[0].RevokedAtUtc);
            Assert.True(consents[1].IsActive);

            Assert.Single(await Mine(database, actor).ToListAsync());
        });
    }

    [Fact]
    public async Task Disconnecting_without_a_connection_is_refused()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        var response = await client.PostAsync($"{CalendarPath}/connection/disconnect");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodesForTest.NotConnected, await CodeAsync(response));
    }

    // --- Ending an account ends what it authorized (design K16) --------------------------

    [Theory]
    [InlineData("disable")]
    [InlineData("deactivate")]
    public async Task Ending_a_professionals_access_withdraws_their_calendar(string action)
    {
        // Revoking sessions ends access to THIS system and says nothing about the standing
        // authorization the clinic holds to write to that professional's personal calendar.
        // Leaving it alive would mean a switched-off account still holding live write access to
        // somebody's private diary — the authorization outliving the role it came with.
        var actor = await ConfiguredProfessionalAsync();
        using var _client = actor.Client;

        fixture.Calendar.NextRefreshToken = "1//token-that-must-be-handed-back";
        await CompleteAsync(actor.Client);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var response = await admin.PostAsync($"/api/staff-accounts/{actor.User.Id}/{action}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Contains("1//token-that-must-be-handed-back", fixture.Calendar.Revoked);

        await fixture.WithDatabaseAsync(async database =>
        {
            var connection = await Mine(database, actor).SingleAsync();

            Assert.Equal(CalendarConnectionStatus.Disconnected, connection.Status);
            Assert.Null(connection.SealedCredential);

            var consent = await database.Consents.SingleAsync(candidate =>
                candidate.UserId == actor.User.Id && candidate.Type == ConsentType.CalendarSync);

            Assert.False(consent.IsActive);
        });
    }

    [Fact]
    public async Task An_unreachable_provider_does_not_block_disabling_an_account()
    {
        // The account action is the administrator's decision and must not depend on Google being
        // up. The local withdrawal still happens; only the confirmation is lost.
        var actor = await ConfiguredProfessionalAsync();
        using var _client = actor.Client;

        await CompleteAsync(actor.Client);

        fixture.Calendar.RevokeOutcome = CalendarTestDouble.RevokeResult.Unreachable;

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var response = await admin.PostAsync($"/api/staff-accounts/{actor.User.Id}/disable");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await fixture.WithDatabaseAsync(async database =>
        {
            var connection = await Mine(database, actor).SingleAsync();

            Assert.Equal(CalendarConnectionStatus.Disconnected, connection.Status);
            Assert.Null(connection.SealedCredential);
        });
    }

    [Fact]
    public async Task Disabling_an_account_that_never_connected_a_calendar_is_unaffected()
    {
        // Most accounts. Having nothing to withdraw is an ordinary state, not a failure — and
        // this is the test that would catch the calendar work breaking account administration
        // for every deployment that never turned the feature on.
        var (client, user) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _client = client;

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var response = await admin.PostAsync($"/api/staff-accounts/{user.Id}/disable");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_withdrawn_authorization_ends_rather_than_pausing()
    {
        // The grant is gone rather than suspended. Note what this test does NOT claim: there is
        // no account re-enable in this product — `User` has Disable() and no Enable(), which
        // StaffAccountEndpoints has recorded since staff-google-guard. So the assertion is about
        // the state left behind, not about a restoration path that does not exist.
        var actor = await ConfiguredProfessionalAsync();
        using var _client = actor.Client;

        await CompleteAsync(actor.Client);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        await admin.PostAsync($"/api/staff-accounts/{actor.User.Id}/disable");

        await fixture.WithDatabaseAsync(async database =>
        {
            var connection = await Mine(database, actor).SingleAsync();

            // The row remembers there was a connection and holds nothing that could still be
            // used, so the only way back is the professional authorizing again.
            Assert.Equal(CalendarConnectionStatus.Disconnected, connection.Status);
            Assert.False(connection.IsUsable);
        });
    }

    [Fact]
    public async Task Restoring_an_account_does_not_restore_its_calendar()
    {
        // The question K16 left for whoever built the account restore, now answerable because it
        // exists. The grant was handed back to Google and the credential destroyed, so there is
        // nothing to resume — and silently re-acquiring write access to somebody's personal
        // calendar would be the wrong default even if it were possible.
        var actor = await ConfiguredProfessionalAsync();
        using var _client = actor.Client;

        await CompleteAsync(actor.Client);

        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        await admin.PostAsync($"/api/staff-accounts/{actor.User.Id}/disable");
        var restored = await admin.PostAsync($"/api/staff-accounts/{actor.User.Id}/enable");

        Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync());

        await fixture.WithDatabaseAsync(async database =>
        {
            var connection = await Mine(database, actor).SingleAsync();

            Assert.Equal(CalendarConnectionStatus.Disconnected, connection.Status);
            Assert.Null(connection.SealedCredential);
            Assert.False(connection.IsUsable);

            // And the consent stays withdrawn: restoring an account is not a new grant of
            // permission over somebody's calendar.
            var consent = await database.Consents.SingleAsync(candidate =>
                candidate.UserId == actor.User.Id && candidate.Type == ConsentType.CalendarSync);

            Assert.False(consent.IsActive);
        });
    }

    // --- Who may reach any of this ------------------------------------------------------

    [Theory]
    [InlineData(Role.FrontDesk)]
    [InlineData(Role.Administrator)]
    [InlineData(Role.Patient)]
    public async Task No_other_role_can_reach_any_calendar_endpoint(Role role)
    {
        var (client, _) = await fixture.AsRoleAsync(role);
        using var _client = client;

        // Including the administrator, deliberately: an administrator connecting a calendar on a
        // professional's behalf is the one thing a consent cannot be.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{CalendarPath}/connection")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{CalendarPath}/connect")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{CalendarPath}/connection/check")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{CalendarPath}/connection/disconnect")).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_by_the_default_policy()
    {
        using var client = fixture.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{CalendarPath}/connection")).StatusCode);
    }

    [Fact]
    public async Task One_professional_cannot_reach_another_professionals_connection()
    {
        var first = await ConfiguredProfessionalAsync();
        using var _first = first.Client;

        await CompleteAsync(first.Client);

        var second = await ConfiguredProfessionalAsync();
        using var _second = second.Client;

        var response = await second.Client.GetAsync($"{CalendarPath}/connection");
        var body = await ReadAsync(response);

        // There is no request shape by which the second professional could name the first's
        // connection — no id in any route (design K11). So the strongest assertion available is
        // that they see their own state, which is "never connected".
        Assert.Equal("NotConnected", body.GetProperty("status").GetString());

        await fixture.WithDatabaseAsync(async database =>
        {
            Assert.Single(await Mine(database, first).ToListAsync());
            Assert.Empty(await Mine(database, second).ToListAsync());
        });
    }

    [Fact]
    public async Task A_professional_with_no_clinical_configuration_is_refused_the_same_way_S3_refuses_them()
    {
        // A claimed invitation that no administrator has configured yet — a real state (design
        // E1), answered by following S3's precedent rather than inventing a second answer.
        var (client, _) = await fixture.AsRoleAsync(Role.Professional);
        using var _client = client;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{CalendarPath}/connection")).StatusCode);

        var start = await client.GetAsync($"{CalendarPath}/connect");

        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Contains("config.not_found", start.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patient_cannot_grant_themselves_a_calendar_consent()
    {
        // GrantConsent parsed ANY consent type under the patient policy, so a patient could grant
        // themselves a calendar consent that meant nothing. Harmless while the value was unused;
        // not harmless now that it corresponds to a real authorization (design K12).
        var (client, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _client = client;

        var response = await client.PostAsync(
            $"/api/patients/me/consents/{nameof(ConsentType.CalendarSync)}/grant", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_second_connection_for_the_same_professional_is_refused_by_the_database()
    {
        // Written straight past the application, the same way AppointmentSchemaTests proves the
        // exclusion constraints are real: the handler updating one row is a convention, and the
        // unique index is what makes "one connection per professional" a guarantee 6b's
        // dispatcher can rely on without asking which row is the real one (design K10).
        var actor = await ConfiguredProfessionalAsync();
        using var _client = actor.Client;

        await CompleteAsync(actor.Client);

        await fixture.WithDatabaseAsync(async database =>
        {
            database.CalendarConnections.Add(CalendarConnection.Establish(
                actor.ProfessionalId,
                CalendarProvider.Google,
                "primary",
                "v1.a.second-row-that-must-not-exist",
                DateTimeOffset.UtcNow));

            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());

            Assert.Contains(
                "ix_calendar_connections_professional_id",
                failure.InnerException?.Message ?? string.Empty,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_professional_can_see_the_calendar_consent_they_granted()
    {
        // identity-session requires a consent to be visible to the user it belongs to. Consents
        // are otherwise read through P7, a patient screen — so without this a professional's
        // calendar consent would be recorded and unviewable, which is the requirement true on
        // paper and false in the product.
        var actor = await ConfiguredProfessionalAsync();
        using var _client = actor.Client;

        await CompleteAsync(actor.Client);

        var body = await ReadAsync(await actor.Client.GetAsync($"{CalendarPath}/connection"));

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("consentVersion").GetString()));
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("consentGrantedAtUtc").ValueKind);

        await actor.Client.PostAsync($"{CalendarPath}/connection/disconnect");

        var afterWithdrawal = await ReadAsync(await actor.Client.GetAsync($"{CalendarPath}/connection"));

        // Withdrawn, so there is no consent in force to report — and the screen must not go on
        // showing one.
        Assert.Equal(JsonValueKind.Null, afterWithdrawal.GetProperty("consentVersion").ValueKind);
    }

    // --- Nothing about scheduling changed -----------------------------------------------

    [Fact]
    public async Task Connecting_a_calendar_writes_nothing_to_any_calendar()
    {
        var actor = await ConfiguredProfessionalAsync();
        var client = actor.Client;
        using var _client = client;

        await CompleteAsync(client);

        // The double records every revocation it is asked for and would throw on an unexpected
        // unreachable call. Establishing a connection contacts Google exactly once — to exchange
        // the code — and never again. No event is created, updated or deleted in this change.
        Assert.Empty(fixture.Calendar.Revoked);

        await fixture.WithDatabaseAsync(async database =>
            Assert.Empty(await database.Appointments
                .Where(appointment => appointment.ProfessionalId == actor.ProfessionalId)
                .ToListAsync()));
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>A professional whose clinical configuration exists, as S7 would have created it.</summary>
    private async Task<Actor> ConfiguredProfessionalAsync()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var (client, user) = await fixture.AsRoleAsync(Role.Professional);

        var specialty = await CreateSpecialtyAsync(admin);

        // The Professional row is born on the administrator's first save (design E1) — the same
        // path S7 takes, rather than writing the row directly.
        var response = await admin.PostAsync(
            $"/api/config/professionals/{user.Id}/specialties", new { specialtyId = specialty });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var professionalId = Guid.Empty;

        await fixture.WithDatabaseAsync(async database =>
            professionalId = await database.Professionals
                .Where(professional => professional.UserId == user.Id)
                .Select(professional => professional.Id)
                .SingleAsync());

        return new Actor(client, user, professionalId);
    }

    /// <summary>
    /// A configured professional and the two ids every assertion here is scoped by.
    /// </summary>
    private sealed record Actor(TestClient Client, User User, Guid ProfessionalId);

    /// <summary>
    /// This actor's connections, and only theirs.
    /// </summary>
    /// <remarks>
    /// Every database assertion goes through here. The fixture does not reset between tests, so
    /// an assertion over the whole table would pass or fail depending on what ran before it —
    /// the same trap <c>ApiFixture</c> warns about for identity data (design A13).
    /// </remarks>
    private static IQueryable<CalendarConnection> Mine(
        Clinic.Api.Infrastructure.Persistence.ClinicDbContext database,
        Actor actor) =>
        database.CalendarConnections.Where(connection => connection.ProfessionalId == actor.ProfessionalId);

    private async Task<Guid> CreateSpecialtyAsync(TestClient admin)
    {
        var response = await admin.PostAsync(
            "/api/config/specialties", new { name = $"Calendar-{Guid.NewGuid():N}"[..24] });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Starts the flow and returns the <c>state</c> the app issued.</summary>
    private async Task<string> StartAsync(TestClient client)
    {
        var response = await client.GetAsync($"{CalendarPath}/connect");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        return System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["state"]!;
    }

    /// <summary>Runs the whole flow: start, then the callback Google would send.</summary>
    private async Task<HttpResponseMessage> CompleteAsync(TestClient client)
    {
        var state = await StartAsync(client);

        return await client.GetAsync($"{CalendarPath}/connect/callback?code=an-authorization-code&state={state}");
    }

    private async Task<string?> StoredCredentialAsync(Actor actor)
    {
        string? stored = null;

        await fixture.WithDatabaseAsync(async database =>
            stored = await Mine(database, actor)
                .Select(connection => connection.SealedCredential)
                .SingleAsync());

        return stored;
    }

    private async Task<DateTimeOffset> ObservedAtAsync(Actor actor)
    {
        var observed = default(DateTimeOffset);

        await fixture.WithDatabaseAsync(async database =>
            observed = await Mine(database, actor)
                .Select(connection => connection.StateObservedAtUtc)
                .SingleAsync());

        return observed;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetString();

    /// <summary>
    /// The codes this suite asserts on, spelled out rather than referenced.
    /// </summary>
    /// <remarks>
    /// Deliberate duplication of <c>ErrorCodes</c>: a test that reads the same constant as the
    /// handler cannot notice the constant's VALUE changing, and these strings are a contract the
    /// frontend translates against. Spelling them out is what makes a rename fail here.
    /// </remarks>
    private static class ErrorCodesForTest
    {
        internal const string NotConnected = "calendar.not_connected";
        internal const string ConsentRevoked = "calendar.consent_revoked";
        internal const string SyncFailed = "calendar.sync_failed";
        internal const string ScopeDeclined = "calendar.scope_declined";
        internal const string ConnectFailed = "calendar.connect_failed";
        internal const string GoogleFailed = "auth.google_failed";
    }
}
