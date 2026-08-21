using System.Net;
using System.Text.Json;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The internal-account login path, end to end (spec: internal accounts, session authority,
/// endpoints authenticated by default).
/// </summary>
/// <remarks>
/// These drive the real endpoints rather than the fixture's session-minting shortcut. That is
/// deliberate and not redundant: <c>AsRoleAsync</c> skips session issuance, so without these
/// tests a bug in the code that ISSUES sessions would pass every other test in the suite
/// (design A13).
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InternalSignInTests(ApiFixture fixture)
{
    [Fact]
    public async Task Valid_credentials_establish_a_session()
    {
        var staff = await fixture.SeedUserAsync(Role.FrontDesk);
        using var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsync("/api/auth/sign-in", new
        {
            email = staff.Email,
            password = ApiFixture.SeededPassword,
        });

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(nameof(Role.FrontDesk), document.RootElement.GetProperty("role").GetString());

        // The body must not carry the credential material or the session id.
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionId", body, StringComparison.OrdinalIgnoreCase);

        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith($"{AuthCookies.Session}=", StringComparison.Ordinal));

        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);

        // And the session works on the next request.
        var session = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_email_are_indistinguishable()
    {
        var staff = await fixture.SeedUserAsync(Role.FrontDesk);

        using var wrongPasswordClient = fixture.CreateAnonymousClient();
        var wrongPassword = await wrongPasswordClient.PostAsync("/api/auth/sign-in", new
        {
            email = staff.Email,
            password = "definitely-not-the-password",
        });

        using var unknownEmailClient = fixture.CreateAnonymousClient();
        var unknownEmail = await unknownEmailClient.PostAsync("/api/auth/sign-in", new
        {
            email = "nobody-at-all@clinic.test",
            password = "definitely-not-the-password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        // Same status AND same body: the pair must not answer whether the account exists.
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(),
            await unknownEmail.Content.ReadAsStringAsync());

        Assert.Equal("auth.invalid_credentials", await ReadCodeAsync(wrongPassword));
    }

    [Fact]
    public async Task Correct_credentials_on_a_disabled_account_are_refused()
    {
        var staff = await fixture.SeedUserAsync(Role.FrontDesk);

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Id == staff.Id);
            user.Disable();
            await database.SaveChangesAsync();
        });

        using var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsync("/api/auth/sign-in", new
        {
            email = staff.Email,
            password = ApiFixture.SeededPassword,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.account_disabled", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_and_then_the_right_password_is_refused()
    {
        var staff = await fixture.SeedUserAsync(Role.FrontDesk);
        using var client = fixture.CreateAnonymousClient();

        // The configured threshold is 5 (AuthOptions default).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await client.PostAsync("/api/auth/sign-in", new
            {
                email = staff.Email,
                password = "wrong",
            });

            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var afterLock = await client.PostAsync("/api/auth/sign-in", new
        {
            email = staff.Email,
            password = ApiFixture.SeededPassword,
        });

        Assert.Equal(HttpStatusCode.Forbidden, afterLock.StatusCode);
        Assert.Equal("auth.account_disabled", await ReadCodeAsync(afterLock));
    }

    [Fact]
    public async Task A_federated_account_cannot_sign_in_with_a_password()
    {
        var patient = await fixture.SeedUserAsync(Role.Patient);
        using var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsync("/api/auth/sign-in", new
        {
            email = patient.Email,
            password = ApiFixture.SeededPassword,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.invalid_credentials", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Signing_out_makes_the_session_unusable_immediately()
    {
        var (client, _) = await fixture.AsRoleAsync(Role.FrontDesk);

        using var _client = client;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);

        var signOut = await client.PostAsync("/api/auth/sign-out");
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);

        // Revocation is effective on the very next request — the row is the authority, so there
        // is no cached principal to keep believing (design A1).
        var afterSignOut = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, afterSignOut.StatusCode);
        Assert.Equal("auth.session_expired", await ReadCodeAsync(afterSignOut));
    }

    [Fact]
    public async Task Revoking_a_session_out_of_band_is_effective_on_the_next_request()
    {
        var (client, user) = await fixture.AsRoleAsync(Role.Administrator);
        using var _client = client;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);

        await fixture.WithDatabaseAsync(async database =>
        {
            var sessions = await database.Sessions
                .Where(session => session.UserId == user.Id)
                .ToListAsync();

            foreach (var session in sessions)
            {
                session.Revoke(DateTimeOffset.UtcNow);
            }

            await database.SaveChangesAsync();
        });

        var response = await client.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.session_expired", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task An_expired_session_is_refused_even_though_the_row_still_exists()
    {
        var (client, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _client = client;

        await fixture.WithDatabaseAsync(async database =>
        {
            // Expiry is evaluated on read, which is what lets this be true without a sweep job.
            await database.Sessions
                .Where(session => session.UserId == user.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    session => session.ExpiresAtUtc,
                    DateTimeOffset.UtcNow.AddMinutes(-1)));
        });

        var response = await client.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.session_expired", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_forged_session_credential_is_refused_and_discloses_nothing()
    {
        using var client = fixture.CreateAnonymousClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
        request.Headers.Add("Cookie", $"{AuthCookies.Session}=not-a-real-session-token");

        var response = await client.Raw.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.session_expired", await ReadCodeAsync(response));

        // Same answer as an expired or revoked session: the response never says which.
        Assert.DoesNotContain("not found", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unknown", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_protected_endpoint_refuses_an_unauthenticated_request_with_the_catalogue_code()
    {
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/api/patients/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.session_expired", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task The_health_endpoint_is_still_anonymous()
    {
        // Change 1's promise, re-asserted now that authentication exists and defaults to
        // required. This is the test that would catch the fallback policy swallowing it.
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_sign_in_endpoint_is_reachable_without_a_session()
    {
        using var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsync("/api/auth/sign-in", new { email = "x@y.test", password = "z" });

        // Refused on the credentials, not on the absence of a session: 401 with the credentials
        // code rather than the session code proves the endpoint was actually reached.
        Assert.Equal("auth.invalid_credentials", await ReadCodeAsync(response));
    }

    internal static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
