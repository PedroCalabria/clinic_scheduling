using System.Net;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The federated path: the flow, the token validation, and the provisioning rule
/// (spec: Google sign-in, deterministic provisioning, request-forgery defences).
/// </summary>
/// <remarks>
/// Every test here drives the real endpoints and the real validator. Only Google's token
/// endpoint and its signing keys are substituted (design A4), so a token this suite mints is
/// checked for signature, issuer, audience, expiry, and nonce by exactly the code that will
/// check Google's.
///
/// The clients do not follow redirects, which is what lets these tests read the flow's
/// <c>Location</c> headers — the flow's decisions are visible there rather than in a body.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class GoogleSignInTests(ApiFixture fixture)
{
    [Fact]
    public async Task The_authorization_request_asks_for_identity_scopes_only()
    {
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/api/auth/google/start");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.ToString();
        var scope = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["scope"];

        // Asserted on the decoded parameter rather than the raw string, so the test is about
        // which scopes are requested and not about how a space happens to be encoded.
        Assert.Equal("openid email profile", scope);
        Assert.Contains("response_type=code", location, StringComparison.Ordinal);
        Assert.Contains("nonce=", location, StringComparison.Ordinal);
        Assert.Contains("state=", location, StringComparison.Ordinal);

        // The change-6 boundary, asserted rather than trusted: no calendar scope, and no
        // offline access, so this flow cannot come back with a refresh token (design A6).
        Assert.DoesNotContain("calendar", location, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_type", location, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("include_granted_scopes", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_email_on_the_patient_portal_is_provisioned_as_a_patient()
    {
        var email = $"new-patient-{Guid.NewGuid():N}@example.test";
        var subject = $"sub-{Guid.NewGuid():N}";

        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, pending.Nonce, fullName: "Jo Doe");

        var response = await CompleteFlowAsync(client, pending.State);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Email == email);

            Assert.Equal(Role.Patient, user.Role);
            Assert.Equal(AuthProvider.Google, user.AuthProvider);
            Assert.Equal(subject, user.ExternalSubjectId);
            Assert.Null(user.PasswordHash);

            // Minimal PII, and the consent that makes processing it lawful, in the same act.
            var patient = await database.Patients.SingleAsync(candidate => candidate.UserId == user.Id);
            Assert.Equal("Jo Doe", patient.FullName);
            Assert.Null(patient.ContactPhone);

            var consent = await database.Consents.SingleAsync(candidate => candidate.UserId == user.Id);
            Assert.Equal(ConsentType.DataProcessing, consent.Type);
            Assert.True(consent.IsActive);
        });

        // The session works, and looks like any other session.
        var session = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task A_pre_invited_professional_claims_the_prepared_account()
    {
        var email = $"dr-{Guid.NewGuid():N}@example.test";
        var subject = $"sub-{Guid.NewGuid():N}";

        // An administrator prepared this account. That is the whole invite-first mechanism: the
        // role exists before the identity provider is ever consulted (design A5).
        await fixture.WithDatabaseAsync(async database =>
        {
            database.Users.Add(User.InviteProfessional(email, DateTimeOffset.UtcNow));
            await database.SaveChangesAsync();
        });

        var (client, pending) = await StartFlowAsync("/staff");
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, pending.Nonce);

        var response = await CompleteFlowAsync(client, pending.State);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/staff", response.Headers.Location!.ToString());

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Email == email);

            Assert.Equal(Role.Professional, user.Role);
            Assert.Equal(subject, user.ExternalSubjectId);
            Assert.Equal(UserStatus.Active, user.Status);

            // Claimed, not duplicated — and no patient record for someone who is not a patient.
            Assert.Equal(1, await database.Users.CountAsync(candidate => candidate.Email == email));
            Assert.False(await database.Patients.AnyAsync(candidate => candidate.UserId == user.Id));
        });

        // The path `staff-google-guard` must not break while closing the one beside it: the
        // claim ends in a working session, on the surface the claim was made from.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/staff")]
    public async Task An_internal_account_cannot_be_claimed_through_google(string returnTo)
    {
        // The refusal that stops controlling a staff mailbox from being enough to sign in as
        // staff (design A5).
        //
        // Asserted from BOTH surfaces, and the same code from each: this is a rule about the
        // provider, not about which door you came through, so `staff-google-guard` must not have
        // turned it into `auth.not_provisioned` on S0. The two refusals have different remedies
        // — "use your password instead" versus "ask administration" — so collapsing them would
        // send a front-desk user who clicked the wrong button to the wrong place entirely.
        var staff = await fixture.SeedUserAsync(Role.FrontDesk);

        var (client, pending) = await StartFlowAsync(returnTo);
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken($"sub-{Guid.NewGuid():N}", staff.Email, pending.Nonce);

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.google_failed");

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Id == staff.Id);

            Assert.Equal(AuthProvider.Internal, user.AuthProvider);
            Assert.Null(user.ExternalSubjectId);
            Assert.Equal(Role.FrontDesk, user.Role);
        });

        await AssertNoSessionAsync(client);
    }

    [Fact]
    public async Task Signing_in_again_reuses_the_same_user()
    {
        var email = $"returning-{Guid.NewGuid():N}@example.test";
        var subject = $"sub-{Guid.NewGuid():N}";

        await SignInWithGoogleAsync(subject, email);
        await SignInWithGoogleAsync(subject, email);

        await fixture.WithDatabaseAsync(async database =>
        {
            Assert.Equal(1, await database.Users.CountAsync(candidate => candidate.ExternalSubjectId == subject));

            var user = await database.Users.SingleAsync(candidate => candidate.ExternalSubjectId == subject);

            Assert.Equal(1, await database.Patients.CountAsync(candidate => candidate.UserId == user.Id));
            Assert.Equal(Role.Patient, user.Role);
        });
    }

    [Fact]
    public async Task An_unverified_email_is_refused()
    {
        // Load-bearing, not a formality: the invite-claim rule matches on email, so an
        // unverified address would make prepared accounts claimable by anyone (design A4).
        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(
            $"sub-{Guid.NewGuid():N}",
            $"unverified-{Guid.NewGuid():N}@example.test",
            pending.Nonce,
            emailVerified: false);

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.google_failed");
        await AssertNoSessionAsync(client);
    }

    [Fact]
    public async Task A_token_signed_by_the_wrong_key_is_refused()
    {
        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        using var untrusted = GoogleTestDouble.UntrustedKey();

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(
            $"sub-{Guid.NewGuid():N}",
            $"forged-{Guid.NewGuid():N}@example.test",
            pending.Nonce,
            signingKey: untrusted);

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.google_failed");
        await AssertNoSessionAsync(client);
    }

    [Fact]
    public async Task A_token_for_another_client_is_refused()
    {
        // The confused-deputy case: a perfectly valid Google token, minted for a different
        // application, must not sign anyone in here.
        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(
            $"sub-{Guid.NewGuid():N}",
            $"other-audience-{Guid.NewGuid():N}@example.test",
            pending.Nonce,
            audience: "some-other-app.apps.googleusercontent.test");

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.google_failed");
    }

    [Fact]
    public async Task A_token_from_the_wrong_issuer_is_refused()
    {
        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(
            $"sub-{Guid.NewGuid():N}",
            $"wrong-issuer-{Guid.NewGuid():N}@example.test",
            pending.Nonce,
            issuer: "https://accounts.evil.test");

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.google_failed");
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(
            $"sub-{Guid.NewGuid():N}",
            $"expired-{Guid.NewGuid():N}@example.test",
            pending.Nonce,
            // Beyond the two-minute clock skew the validator allows.
            expiresAtUtc: DateTime.UtcNow.AddMinutes(-10));

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.google_failed");
    }

    [Fact]
    public async Task A_token_bound_to_a_different_sign_in_is_refused()
    {
        // The nonce is what ties the token to THIS authorization request, so a token obtained
        // through some other flow cannot be injected into this one.
        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(
            $"sub-{Guid.NewGuid():N}",
            $"wrong-nonce-{Guid.NewGuid():N}@example.test",
            nonce: "a-nonce-from-somewhere-else");

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.google_failed");
    }

    [Fact]
    public async Task A_callback_with_a_mismatched_state_is_refused()
    {
        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(
            $"sub-{Guid.NewGuid():N}",
            $"bad-state-{Guid.NewGuid():N}@example.test",
            pending.Nonce);

        var response = await CompleteFlowAsync(client, state: "not-the-state-we-issued");

        AssertRefused(response, "auth.google_failed");
        await AssertNoSessionAsync(client);
    }

    [Fact]
    public async Task A_callback_arriving_without_a_pending_sign_in_is_refused()
    {
        // No start, so no state cookie: an injected callback.
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/api/auth/google/callback?code=whatever&state=whatever");

        AssertRefused(response, "auth.google_failed");
    }

    [Fact]
    public async Task Replaying_a_consumed_callback_is_refused()
    {
        var email = $"replay-{Guid.NewGuid():N}@example.test";
        var subject = $"sub-{Guid.NewGuid():N}";

        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, pending.Nonce);

        var first = await CompleteFlowAsync(client, pending.State);
        Assert.Equal("/", first.Headers.Location!.ToString());

        // Same code, same state, same token. The state cookie was cleared when it was consumed,
        // so there is nothing left to match (design A3).
        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, pending.Nonce);
        var replay = await CompleteFlowAsync(client, pending.State);

        AssertRefused(replay, "auth.google_failed");
    }

    [Fact]
    public async Task A_failing_token_exchange_is_refused()
    {
        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.FailExchange = true;

        try
        {
            var response = await CompleteFlowAsync(client, pending.State);

            AssertRefused(response, "auth.google_failed");
        }
        finally
        {
            fixture.Google.FailExchange = false;
        }
    }

    [Fact]
    public async Task A_deployment_without_a_google_client_reports_that_it_is_unavailable()
    {
        // Configuration, not a request, so it needs its own host (design A14).
        using var host = fixture.CreateHost(new Dictionary<string, string>
        {
            ["Auth:Google:ClientId"] = string.Empty,
            ["Auth:Google:ClientSecret"] = string.Empty,
            ["Auth:Google:RedirectUri"] = string.Empty,
        });

        using var client = fixture.CreateClientFor(host);

        var response = await client.GetAsync("/api/auth/google/start");

        AssertRefused(response, "auth.google_unavailable");

        // The internal path still works: a missing Google client degrades one login path
        // rather than stopping the system.
        var staff = await fixture.SeedUserAsync(Role.FrontDesk);

        var signIn = await client.PostAsync("/api/auth/sign-in", new
        {
            email = staff.Email,
            password = ApiFixture.SeededPassword,
        });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    }

    [Fact]
    public async Task The_return_destination_cannot_be_pointed_off_this_origin()
    {
        // An open redirect here would hand a freshly-signed-in user to an attacker's page.
        var (client, pending) = await StartFlowAsync("//evil.example/steal");
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(
            $"sub-{Guid.NewGuid():N}",
            $"redirect-{Guid.NewGuid():N}@example.test",
            pending.Nonce);

        var response = await CompleteFlowAsync(client, pending.State);

        Assert.Equal("/", response.Headers.Location!.ToString());
    }

    // --- The staff surface is claim-only (staff-google-guard) --------------------------
    //
    // Every test below exists because of one incident: with a real Google client configured, a
    // professional signed in on S0 before being invited, the shared flow found no record for
    // their address, and change 2's rule turned them into a PATIENT — after which they could not
    // be invited at all, because their address was taken.

    [Theory]
    [InlineData("/staff")]
    [InlineData("/staff/")]
    [InlineData("/staff/users")]
    public async Task An_unknown_email_on_the_staff_surface_is_refused_and_creates_nothing(string returnTo)
    {
        var email = $"uninvited-{Guid.NewGuid():N}@example.test";
        var subject = $"sub-{Guid.NewGuid():N}";

        var (client, pending) = await StartFlowAsync(returnTo);
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, pending.Nonce, fullName: "Un Invited");

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.not_provisioned");

        // The refusal is not the point. THIS is the point.
        await fixture.WithDatabaseAsync(async database =>
        {
            Assert.False(await database.Users.AnyAsync(candidate => candidate.Email == email));
            Assert.False(await database.Users.AnyAsync(candidate => candidate.ExternalSubjectId == subject));
            Assert.False(await database.Patients.AnyAsync(candidate => candidate.ContactEmail == email));
        });

        await AssertNoSessionAsync(client);
    }

    [Fact]
    public async Task An_existing_patient_is_refused_on_the_staff_surface()
    {
        // Admitting a genuine patient here would establish a real session for a user every staff
        // screen forbids — a console that looks broken instead of an answer that is true. And the
        // refusal names the door that IS theirs rather than telling them to ask administration:
        // nothing about their access needs registering, they are simply at the wrong entrance.
        var patient = await fixture.SeedUserAsync(Role.Patient);

        var subject = patient.ExternalSubjectId!;

        var (client, pending) = await StartFlowAsync("/staff/");
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, patient.Email, pending.Nonce);

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.use_patient_sign_in");
        await AssertNoSessionAsync(client);

        // Their own account is left exactly as it was — the refusal touches nothing.
        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Id == patient.Id);

            Assert.Equal(Role.Patient, user.Role);
            Assert.Equal(UserStatus.Active, user.Status);
            Assert.Equal(subject, user.ExternalSubjectId);
            Assert.True(await database.Patients.AnyAsync(candidate => candidate.UserId == user.Id));
            Assert.Equal(1, await database.Consents.CountAsync(candidate => candidate.UserId == user.Id));
        });
    }

    [Fact]
    public async Task The_same_identity_is_a_patient_on_the_portal_and_is_turned_away_on_the_staff_surface()
    {
        // The property no single-surface test can show, and the one the whole design rests on:
        // the divergence is the door the flow STARTED at, not anything in the token. The token
        // here is minted from the same subject and address both times.
        var email = $"both-doors-{Guid.NewGuid():N}@example.test";
        var subject = $"sub-{Guid.NewGuid():N}";

        var (portal, portalPending) = await StartFlowAsync("/profile");
        using var _portal = portal;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, portalPending.Nonce);

        var admitted = await CompleteFlowAsync(portal, portalPending.State);

        Assert.Equal("/profile", admitted.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await portal.GetAsync("/api/auth/session")).StatusCode);

        var (console, consolePending) = await StartFlowAsync("/staff/");
        using var _console = console;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, consolePending.Nonce);

        var refused = await CompleteFlowAsync(console, consolePending.State);

        AssertRefused(refused, "auth.use_patient_sign_in");
        await AssertNoSessionAsync(console);
    }

    [Fact]
    public async Task A_professional_is_refused_on_the_patient_portal()
    {
        // The mirror of the S0 hole, found by running this change's own validation guide. Before
        // this, a professional signing in on the portal GOT A SESSION, and then P7 told them
        // "no such patient record" — because a professional has no patient row and never will.
        // A session in which every screen fails is not an answer; being sent to the right door
        // is.
        var professional = await fixture.SeedUserAsync(Role.Professional);
        var subject = professional.ExternalSubjectId!;

        var (client, pending) = await StartFlowAsync("/profile");
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, professional.Email, pending.Nonce);

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.use_staff_sign_in");
        await AssertNoSessionAsync(client);

        // And no patient record is conjured up to make the portal work for them.
        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Id == professional.Id);

            Assert.Equal(Role.Professional, user.Role);
            Assert.False(await database.Patients.AnyAsync(candidate => candidate.UserId == user.Id));
            Assert.False(await database.Consents.AnyAsync(candidate => candidate.UserId == user.Id));
        });
    }

    [Fact]
    public async Task An_invitation_cannot_be_claimed_through_the_patient_portal()
    {
        // Quieter than the broken screen and worse: the invitation used to be CLAIMED on the way
        // in. The professional's account would be bound to their Google subject by a sign-in on
        // the portal — a write performed on the wrong surface, which is exactly what the
        // refuse-before-you-write ordering exists to prevent.
        var email = $"dr-wrong-door-{Guid.NewGuid():N}@example.test";
        var subject = $"sub-{Guid.NewGuid():N}";

        await fixture.WithDatabaseAsync(async database =>
        {
            database.Users.Add(User.InviteProfessional(email, DateTimeOffset.UtcNow));
            await database.SaveChangesAsync();
        });

        var (client, pending) = await StartFlowAsync("/profile");
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, pending.Nonce);

        var response = await CompleteFlowAsync(client, pending.State);

        AssertRefused(response, "auth.use_staff_sign_in");
        await AssertNoSessionAsync(client);

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Email == email);

            // Still waiting for the sign-in that comes through the right door.
            Assert.Null(user.ExternalSubjectId);
            Assert.Equal(UserStatus.PendingClaim, user.Status);
            Assert.True(user.AwaitsClaim);
        });

        // Then the right door, and the invitation claims normally.
        var (console, consolePending) = await StartFlowAsync("/staff/");
        using var _console = console;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, consolePending.Nonce);

        Assert.Equal("/staff/", (await CompleteFlowAsync(console, consolePending.State)).Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await console.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task A_claimed_professional_signs_in_again_on_the_staff_surface()
    {
        // Claim-only admits an already-claimed professional as well as one claiming for the
        // first time — otherwise the guard would lock out every professional on their second
        // visit, which is the obvious way to get this wrong.
        var professional = await fixture.SeedUserAsync(Role.Professional);

        var subject = professional.ExternalSubjectId!;

        var (client, pending) = await StartFlowAsync("/staff/");
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, professional.Email, pending.Nonce);

        var response = await CompleteFlowAsync(client, pending.State);

        Assert.Equal("/staff/", response.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task The_incident_that_motivated_this_change_now_ends_differently()
    {
        // The whole sequence, forward: refused on S0, the administrator finds nothing holding
        // the address, invites it, and the same Google identity claims it. This is the check the
        // change-4 validation run failed.
        var email = $"dr-late-{Guid.NewGuid():N}@example.test";
        var subject = $"sub-{Guid.NewGuid():N}";

        var (refusedClient, refusedPending) = await StartFlowAsync("/staff/");
        using var _refused = refusedClient;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, refusedPending.Nonce);

        AssertRefused(await CompleteFlowAsync(refusedClient, refusedPending.State), "auth.not_provisioned");

        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        // Nothing to clean up, because nothing was created. Under change 2's rule this lookup
        // would have found a patient account standing in the way.
        var holder = await administrator.GetAsync($"/api/staff-accounts/by-email?email={Uri.EscapeDataString(email)}");
        Assert.Equal(HttpStatusCode.NotFound, holder.StatusCode);

        var invited = await administrator.PostAsync("/api/staff-accounts", new
        {
            email,
            role = nameof(Role.Professional),
        });

        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);

        var (claimClient, claimPending) = await StartFlowAsync("/staff/");
        using var _claim = claimClient;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, claimPending.Nonce);

        var claimed = await CompleteFlowAsync(claimClient, claimPending.State);

        Assert.Equal("/staff/", claimed.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await claimClient.GetAsync("/api/auth/session")).StatusCode);

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Email == email);

            Assert.Equal(Role.Professional, user.Role);
            Assert.Equal(UserStatus.Active, user.Status);
            Assert.Equal(subject, user.ExternalSubjectId);
            Assert.False(await database.Patients.AnyAsync(candidate => candidate.UserId == user.Id));
        });
    }

    /// <summary>Starts the flow and reads back the state and nonce the server issued.</summary>
    /// <remarks>
    /// Reading them from the cookie is legitimate rather than a shortcut: neither value is a
    /// secret — both travel to Google in the authorization URL, visible in the address bar.
    /// What matters is that a third party cannot guess them and that the cookie is single-use.
    /// </remarks>
    private async Task<(TestClient Client, GoogleOAuthStateView Pending)> StartFlowAsync(string returnTo = "/")
    {
        var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync($"/api/auth/google/start?returnTo={Uri.EscapeDataString(returnTo)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith($"{AuthCookies.OAuthState}=", StringComparison.Ordinal));

        var value = cookie[(AuthCookies.OAuthState.Length + 1)..].Split(';')[0];
        var parts = value.Split('.');

        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        return (client, new GoogleOAuthStateView(parts[0], parts[1]));
    }

    private static Task<HttpResponseMessage> CompleteFlowAsync(TestClient client, string state) =>
        client.GetAsync($"/api/auth/google/callback?code=test-code&state={Uri.EscapeDataString(state)}");

    private async Task SignInWithGoogleAsync(string subject, string email)
    {
        var (client, pending) = await StartFlowAsync();
        using var _client = client;

        fixture.Google.NextIdToken = fixture.Google.MintIdToken(subject, email, pending.Nonce);

        var response = await CompleteFlowAsync(client, pending.State);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
    }

    /// <summary>A refusal is a redirect back to the app carrying the code, never a raw body.</summary>
    private static void AssertRefused(HttpResponseMessage response, string expectedCode)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.ToString();

        Assert.StartsWith("/", location, StringComparison.Ordinal);
        Assert.DoesNotContain("//", location, StringComparison.Ordinal);
        Assert.Contains($"authError={expectedCode}", location, StringComparison.Ordinal);
    }

    private static async Task AssertNoSessionAsync(TestClient client)
    {
        var session = await client.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    private sealed record GoogleOAuthStateView(string State, string Nonce);
}
