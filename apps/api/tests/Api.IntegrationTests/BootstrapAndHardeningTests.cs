using System.Net;
using System.Text.Json;
using Clinic.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The administrator bootstrap and the login-path brakes (spec: an administrator exists
/// before any administrator can sign in; the sign-in path resists automated guessing; the
/// request-forgery defences).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BootstrapAndHardeningTests(ApiFixture fixture)
{
    [Fact]
    public async Task The_configured_administrator_exists_and_must_replace_its_password()
    {
        // Its own host with its own administrator: the bootstrap account is durable state, and
        // two tests sharing one would pass or fail depending on which ran first.
        var (host, email, password) = await BootstrapHostAsync();
        using var _host = host;
        using var client = fixture.CreateClientFor(host);

        var signIn = await client.PostAsync("/api/auth/sign-in", new { email, password });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        using var document = JsonDocument.Parse(await signIn.Content.ReadAsStringAsync());
        Assert.Equal(nameof(Role.Administrator), document.RootElement.GetProperty("role").GetString());
        Assert.True(document.RootElement.GetProperty("mustChangePassword").GetBoolean());

        // And it is held to that: anything else is refused until the credential is replaced.
        var blocked = await client.GetAsync("/api/staff-accounts");

        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("auth.password_change_required", await InternalSignInTests.ReadCodeAsync(blocked));
    }

    [Fact]
    public async Task A_wrong_current_password_is_reported_as_the_current_password_not_as_a_sign_in_failure()
    {
        // The change-password screen has no email field, so answering with
        // auth.invalid_credentials ("email and password do not match") sent the user looking
        // for a field that is not on screen. The code has to name the one thing they can fix.
        var (host, email, password) = await BootstrapHostAsync();
        using var _host = host;
        using var client = fixture.CreateClientFor(host);

        await client.PostAsync("/api/auth/sign-in", new { email, password });

        var refused = await client.PostAsync("/api/auth/password", new
        {
            currentPassword = "not-the-current-password",
            newPassword = "a-genuinely-new-password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(
            "auth.current_password_invalid",
            await InternalSignInTests.ReadCodeAsync(refused));

        // And the hold is still in place, because nothing was changed.
        var held = await client.GetAsync("/api/staff-accounts");

        Assert.Equal(HttpStatusCode.Forbidden, held.StatusCode);
        Assert.Equal("auth.password_change_required", await InternalSignInTests.ReadCodeAsync(held));
    }

    [Fact]
    public async Task Changing_the_bootstrap_password_lifts_the_hold()
    {
        var (host, email, password) = await BootstrapHostAsync();
        using var _host = host;
        using var client = fixture.CreateClientFor(host);

        await client.PostAsync("/api/auth/sign-in", new { email, password });

        var changed = await client.PostAsync("/api/auth/password", new
        {
            currentPassword = password,
            newPassword = "a-genuinely-new-password",
        });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // The caller continues on a fresh session rather than being signed out by their own
        // password change, and the hold is gone.
        var allowed = await client.GetAsync("/api/staff-accounts");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task The_old_bootstrap_password_stops_working_once_changed()
    {
        var (host, email, password) = await BootstrapHostAsync();
        using var _host = host;
        using var client = fixture.CreateClientFor(host);

        await client.PostAsync("/api/auth/sign-in", new { email, password });
        await client.PostAsync("/api/auth/password", new
        {
            currentPassword = password,
            newPassword = "a-genuinely-new-password",
        });

        using var fresh = fixture.CreateClientFor(host);

        var withOldPassword = await fresh.PostAsync("/api/auth/sign-in", new { email, password });

        Assert.Equal(HttpStatusCode.Unauthorized, withOldPassword.StatusCode);
        Assert.Equal("auth.invalid_credentials", await InternalSignInTests.ReadCodeAsync(withOldPassword));
    }

    [Fact]
    public async Task Running_the_bootstrap_again_changes_nothing()
    {
        var (host, email, password) = await BootstrapHostAsync();
        using var _host = host;
        using var client = fixture.CreateClientFor(host);

        // An operator changes the supplied password, as they are required to.
        await client.PostAsync("/api/auth/sign-in", new { email, password });
        await client.PostAsync("/api/auth/password", new
        {
            currentPassword = password,
            newPassword = "an-operator-chosen-password",
        });

        Guid firstId = Guid.Empty;
        string? changedHash = null;

        await fixture.WithDatabaseAsync(async database =>
        {
            var administrator = await database.Users.SingleAsync(user => user.Email == email);

            firstId = administrator.Id;
            changedHash = administrator.PasswordHash;
        });

        // Now restart, twice.
        await ApiFixture.RunAdministratorBootstrapAsync(host);
        await ApiFixture.RunAdministratorBootstrapAsync(host);

        await fixture.WithDatabaseAsync(async database =>
        {
            var administrators = await database.Users
                .Where(user => user.Email == email)
                .ToListAsync();

            Assert.Single(administrators);
            Assert.Equal(firstId, administrators[0].Id);

            // The point of idempotency here is not just "no duplicate row": an operator who has
            // changed the password does not silently get the configured one put back.
            Assert.Equal(changedHash, administrators[0].PasswordHash);
            Assert.False(administrators[0].MustChangePassword);
        });
    }

    /// <summary>
    /// A host whose bootstrap administrator is unique to this test, already created.
    /// </summary>
    private async Task<(WebApplicationFactory<Program> Host, string Email, string Password)> BootstrapHostAsync()
    {
        var email = $"bootstrap-{Guid.NewGuid():N}@clinic.test";
        const string password = "bootstrap-password-123";

        var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Auth:BootstrapAdministrator:Email"] = email,
            ["Auth:BootstrapAdministrator:Password"] = password,
            ["Auth:LoginAttemptsPerMinute"] = "10000",
        });

        await ApiFixture.RunAdministratorBootstrapAsync(host);

        return (host, email, password);
    }

    [Fact]
    public async Task A_state_changing_request_without_the_csrf_header_is_refused()
    {
        var (client, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _client = client;

        var response = await client.PostWithoutCsrfAsync("/api/patients/me/consents/DataProcessing/revoke");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await InternalSignInTests.ReadCodeAsync(response));
    }

    [Fact]
    public async Task The_same_request_with_the_header_succeeds()
    {
        // The pair is the test: without both halves, the first assertion above could pass for
        // the wrong reason.
        var (client, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _client = client;

        var response = await client.PostAsync("/api/patients/me/consents/DataProcessing/revoke");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_safe_request_issues_the_csrf_cookie()
    {
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/api/health");

        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("clinic.csrf=", StringComparison.Ordinal));

        // Readable by scripts on purpose — echoing it in a header is the whole mechanism — but
        // still Secure, so it never crosses plain HTTP.
        Assert.DoesNotContain("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Too_many_login_attempts_are_refused_with_the_catalogue_code()
    {
        // The threshold is configuration, so this gets its own host rather than making every
        // other test in the suite work around a low limit (design A10).
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Auth:LoginAttemptsPerMinute"] = "3",
        });

        using var client = fixture.CreateClientFor(host);

        HttpResponseMessage? limited = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.PostAsync("/api/auth/sign-in", new
            {
                email = $"nobody-{attempt}@clinic.test",
                password = "whatever",
            });

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
                break;
            }
        }

        Assert.NotNull(limited);
        Assert.Equal("auth.rate_limited", await InternalSignInTests.ReadCodeAsync(limited));

        // A client told when to come back does not have to poll.
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task The_login_limit_does_not_apply_to_ordinary_requests()
    {
        // Scoped to the login endpoints only: a limiter that throttled the whole API would be a
        // denial of service with extra steps.
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Auth:LoginAttemptsPerMinute"] = "2",
        });

        var patient = await fixture.SeedUserAsync(Role.Patient);

        var token = await ApiFixture.IssueSessionOnAsync(host, patient);

        using var client = fixture.CreateClientFor(host, token);

        for (var request = 0; request < 10; request++)
        {
            var response = await client.GetAsync("/api/auth/session");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
