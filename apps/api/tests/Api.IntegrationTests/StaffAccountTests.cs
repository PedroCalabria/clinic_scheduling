using System.Net;
using System.Text.Json;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// S11's API: staff accounts and professional invitations (spec: administrators manage staff
/// accounts and professional invitations).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StaffAccountTests(ApiFixture fixture)
{
    [Fact]
    public async Task An_administrator_creates_an_internal_staff_account_that_can_sign_in()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var email = $"desk-{Guid.NewGuid():N}@clinic.test";
        const string password = "a-long-enough-password";

        var created = await administrator.PostAsync("/api/staff-accounts", new
        {
            email,
            role = nameof(Role.FrontDesk),
            password,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(nameof(Role.FrontDesk), document.RootElement.GetProperty("role").GetString());
        Assert.Equal(nameof(AuthProvider.Internal), document.RootElement.GetProperty("authProvider").GetString());

        // The account is real: it signs in, and it is made to own its password (design A6).
        using var newcomer = fixture.CreateAnonymousClient();

        var signIn = await newcomer.PostAsync("/api/auth/sign-in", new { email, password });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        using var session = JsonDocument.Parse(await signIn.Content.ReadAsStringAsync());
        Assert.True(session.RootElement.GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task An_administrator_registers_a_professional_for_the_google_sign_in_to_claim()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var email = $"dr-{Guid.NewGuid():N}@example.test";

        var created = await administrator.PostAsync("/api/staff-accounts", new
        {
            email,
            role = nameof(Role.Professional),
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("awaitsClaim").GetBoolean());

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Email == email);

            // No credential of any kind: this account is reachable only by the Google sign-in
            // that claims it.
            Assert.Null(user.PasswordHash);
            Assert.Null(user.ExternalSubjectId);
            Assert.Equal(UserStatus.PendingClaim, user.Status);
            Assert.False(user.CanAuthenticate);
        });
    }

    [Fact]
    public async Task A_duplicate_email_is_refused()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var existing = await fixture.SeedUserAsync(Role.FrontDesk);

        var response = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = existing.Email,
            role = nameof(Role.Administrator),
            password = "a-long-enough-password",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("auth.email_already_in_use", await InternalSignInTests.ReadCodeAsync(response));
    }

    [Fact]
    public async Task An_email_differing_only_in_case_is_still_a_duplicate()
    {
        // Normalization is what makes this true, and it is the same normalization the
        // invite-claim rule depends on (design A5).
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var existing = await fixture.SeedUserAsync(Role.FrontDesk);

        var response = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = existing.Email.ToUpperInvariant(),
            role = nameof(Role.FrontDesk),
            password = "a-long-enough-password",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_patient_role_cannot_be_created_here()
    {
        // Patients come into existence by signing in, never by an administrator making one.
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var response = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = $"patient-{Guid.NewGuid():N}@example.test",
            role = nameof(Role.Patient),
            password = "a-long-enough-password",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.invalid_format", await InternalSignInTests.ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_short_password_is_refused_with_the_minimum_stated()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var response = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = $"desk-{Guid.NewGuid():N}@clinic.test",
            role = nameof(Role.FrontDesk),
            password = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parameters = document.RootElement.GetProperty("params");

        // The frontend translates the code and interpolates the minimum, so the message can say
        // what is actually required (Decision I).
        Assert.Equal("password", parameters.GetProperty("field").GetString());
        Assert.Equal(12, parameters.GetProperty("minimumLength").GetInt32());
    }

    [Fact]
    public async Task Disabling_an_account_ends_a_session_it_already_holds()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var (victim, victimUser) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _victim = victim;

        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/auth/session")).StatusCode);

        var disabled = await administrator.PostAsync($"/api/staff-accounts/{victimUser.Id}/disable");
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);

        // Next request, not next expiry.
        var afterDisable = await victim.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, afterDisable.StatusCode);

        // And no new session can be established either.
        using var reattempt = fixture.CreateAnonymousClient();
        var signIn = await reattempt.PostAsync("/api/auth/sign-in", new
        {
            email = victimUser.Email,
            password = ApiFixture.SeededPassword,
        });

        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        Assert.Equal("auth.account_disabled", await InternalSignInTests.ReadCodeAsync(signIn));
    }

    [Fact]
    public async Task Disabling_an_account_that_does_not_exist_is_reported_as_such()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var response = await administrator.PostAsync($"/api/staff-accounts/{Guid.NewGuid()}/disable");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("auth.account_not_found", await InternalSignInTests.ReadCodeAsync(response));
    }

    [Fact]
    public async Task The_listing_shows_staff_and_invitations_but_not_patients()
    {
        var (administrator, adminUser) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var patient = await fixture.SeedUserAsync(Role.Patient);

        var response = await administrator.GetAsync("/api/staff-accounts");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(adminUser.Email, body, StringComparison.Ordinal);
        Assert.DoesNotContain(patient.Email, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_soft_deleted_accounts_email_can_be_used_again()
    {
        // Soft-delete is the only deletion (I10), so the unique index on email is filtered to
        // live rows — otherwise one removed account would reserve that address forever.
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var retired = await fixture.SeedUserAsync(Role.FrontDesk);

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Id == retired.Id);
            user.SoftDelete(DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        });

        var response = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = retired.Email,
            role = nameof(Role.FrontDesk),
            password = "a-long-enough-password",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await fixture.WithDatabaseAsync(async database =>
        {
            // Both rows exist: the deleted one is still there, marked.
            var rows = await database.Users
                .Where(candidate => candidate.Email == retired.Email)
                .ToListAsync();

            Assert.Equal(2, rows.Count);
            Assert.Single(rows, row => row.DeletedAtUtc is not null);
        });
    }

    // --- Recovery: deactivate, then invite anew (staff-google-guard) -------------------
    //
    // `00-context.md` §5 has always said this is how a mistakenly-created account is fixed — a
    // role never changes, so the account is retired and the address invited afresh. Until now
    // nothing in the product could do the retiring: `disable` turns an account off while KEEPING
    // its address, and S11 lists staff only, so a patient created by mistake was invisible to the
    // administrator who had to clear it.

    [Fact]
    public async Task Deactivating_a_patient_account_frees_its_address_for_a_professional_invitation()
    {
        // The recovery path end to end, and the reason the filtered unique index is filtered.
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var mistake = await fixture.SeedUserAsync(Role.Patient);

        // While it is still live the address is taken, whatever role wants it.
        var blocked = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = mistake.Email,
            role = nameof(Role.Professional),
        });

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("auth.email_already_in_use", await InternalSignInTests.ReadCodeAsync(blocked));

        var deactivated = await administrator.PostAsync($"/api/staff-accounts/{mistake.Id}/deactivate");
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        var invited = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = mistake.Email,
            role = nameof(Role.Professional),
        });

        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);

        await fixture.WithDatabaseAsync(async database =>
        {
            var rows = await database.Users
                .Where(candidate => candidate.Email == mistake.Email)
                .ToListAsync();

            // A NEW account, not a mutated one. That is what keeps the access log honest about
            // who held which role when, and it is why "promote this user" is not a feature.
            Assert.Equal(2, rows.Count);

            var retired = Assert.Single(rows, row => row.DeletedAtUtc is not null);
            Assert.Equal(Role.Patient, retired.Role);

            var fresh = Assert.Single(rows, row => row.DeletedAtUtc is null);
            Assert.Equal(Role.Professional, fresh.Role);
            Assert.NotEqual(mistake.Id, fresh.Id);
            Assert.True(fresh.AwaitsClaim);

            // Soft-delete only (I10): the patient's own record and consent are still there.
            Assert.True(await database.Patients.AnyAsync(candidate => candidate.UserId == retired.Id));
            Assert.True(await database.Consents.AnyAsync(candidate => candidate.UserId == retired.Id));
        });
    }

    [Fact]
    public async Task Disabling_an_account_keeps_its_address_while_deactivating_releases_it()
    {
        // The two actions differ in exactly one way, and it is the way that matters here. Pinned
        // by test rather than left to their names, because the names alone will not stop someone
        // from folding one into the other (design D4).
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var account = await fixture.SeedUserAsync(Role.FrontDesk);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await administrator.PostAsync($"/api/staff-accounts/{account.Id}/disable")).StatusCode);

        var stillTaken = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = account.Email,
            role = nameof(Role.FrontDesk),
            password = "a-long-enough-password",
        });

        Assert.Equal(HttpStatusCode.Conflict, stillTaken.StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await administrator.PostAsync($"/api/staff-accounts/{account.Id}/deactivate")).StatusCode);

        var nowFree = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = account.Email,
            role = nameof(Role.FrontDesk),
            password = "a-long-enough-password",
        });

        Assert.Equal(HttpStatusCode.Created, nowFree.StatusCode);
    }

    [Fact]
    public async Task Deactivating_an_account_ends_a_session_it_already_holds()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var (victim, victimUser) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _victim = victim;

        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/auth/session")).StatusCode);

        var deactivated = await administrator.PostAsync($"/api/staff-accounts/{victimUser.Id}/deactivate");
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        // Next request, not next expiry — releasing the address must not be the only effect.
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task An_administrator_cannot_deactivate_their_own_account()
    {
        // Otherwise the clinic can lock itself out of the one screen that creates accounts.
        var (administrator, self) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var response = await administrator.PostAsync($"/api/staff-accounts/{self.Id}/deactivate");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await InternalSignInTests.ReadCodeAsync(response));

        // Still working, which is the point.
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Deactivating_is_administrator_only()
    {
        var (frontDesk, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _frontDesk = frontDesk;

        var target = await fixture.SeedUserAsync(Role.Patient);

        var response = await frontDesk.PostAsync($"/api/staff-accounts/{target.Id}/deactivate");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await fixture.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.SingleAsync(candidate => candidate.Id == target.Id);
            Assert.Null(user.DeletedAtUtc);
        });
    }

    [Fact]
    public async Task Deactivating_an_account_twice_reports_that_it_is_gone()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var account = await fixture.SeedUserAsync(Role.FrontDesk);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await administrator.PostAsync($"/api/staff-accounts/{account.Id}/deactivate")).StatusCode);

        var again = await administrator.PostAsync($"/api/staff-accounts/{account.Id}/deactivate");

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("auth.account_not_found", await InternalSignInTests.ReadCodeAsync(again));
    }

    [Fact]
    public async Task The_address_lookup_finds_a_patient_the_listing_deliberately_hides()
    {
        // The listing hides patients on purpose, so without this an administrator could not
        // reach the account most likely to be blocking an invitation.
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var patient = await fixture.SeedUserAsync(Role.Patient);

        var found = await administrator.GetAsync(
            $"/api/staff-accounts/by-email?email={Uri.EscapeDataString(patient.Email)}");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);

        using var document = JsonDocument.Parse(await found.Content.ReadAsStringAsync());
        Assert.Equal(patient.Id, document.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(nameof(Role.Patient), document.RootElement.GetProperty("role").GetString());
        Assert.Equal(nameof(UserStatus.Active), document.RootElement.GetProperty("status").GetString());

        // Only what the administrator already typed, plus the account's shape. No name, no phone.
        Assert.False(document.RootElement.TryGetProperty("fullName", out _));
        Assert.False(document.RootElement.TryGetProperty("contactPhone", out _));
    }

    [Fact]
    public async Task The_address_lookup_normalizes_the_address_it_is_given()
    {
        // The same normalization the uniqueness rule and the invite-claim rule use, or the
        // recovery flow would report "nobody holds it" about an address that is taken.
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var patient = await fixture.SeedUserAsync(Role.Patient);

        var found = await administrator.GetAsync(
            $"/api/staff-accounts/by-email?email={Uri.EscapeDataString(patient.Email.ToUpperInvariant())}");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
    }

    [Fact]
    public async Task The_address_lookup_reports_an_unused_address_as_not_found()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var response = await administrator.GetAsync(
            $"/api/staff-accounts/by-email?email=nobody-{Guid.NewGuid():N}%40example.test");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("auth.account_not_found", await InternalSignInTests.ReadCodeAsync(response));
    }

    [Fact]
    public async Task The_address_lookup_ignores_an_account_that_is_already_deactivated()
    {
        // "Is this address taken?" is a question about live accounts only — the same filter the
        // uniqueness rule applies. A deactivated row must not look like an obstacle.
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var account = await fixture.SeedUserAsync(Role.FrontDesk);

        await administrator.PostAsync($"/api/staff-accounts/{account.Id}/deactivate");

        var response = await administrator.GetAsync(
            $"/api/staff-accounts/by-email?email={Uri.EscapeDataString(account.Email)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_address_lookup_is_administrator_only()
    {
        var (frontDesk, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _frontDesk = frontDesk;

        var patient = await fixture.SeedUserAsync(Role.Patient);

        var response = await frontDesk.GetAsync(
            $"/api/staff-accounts/by-email?email={Uri.EscapeDataString(patient.Email)}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_restores_a_disabled_account()
    {
        // The other half of disabling, missing until calendar-connection: 00-context.md §5 has
        // called disabling "a reversible off-switch" since change 2 while nothing could reverse it.
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var (client, user) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _client = client;

        await Ok(admin.PostAsync($"/api/staff-accounts/{user.Id}/disable"));

        // Access really ended — the session this client holds is refused on its next request.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);

        await Ok(admin.PostAsync($"/api/staff-accounts/{user.Id}/enable"));

        await fixture.WithDatabaseAsync(async database =>
        {
            var restored = await database.Users.SingleAsync(candidate => candidate.Id == user.Id);

            Assert.Equal(UserStatus.Active, restored.Status);
            Assert.True(restored.CanAuthenticate);
        });

        // Sessions are NOT resurrected: restoring makes the account able to sign in again, it
        // does not hand back the sessions that were revoked.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Restoring_an_unclaimed_invitation_leaves_it_claimable()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var email = $"dr.restore-{Guid.NewGuid():N}@example.test";
        var created = await Ok(admin.PostAsync(
            "/api/staff-accounts", new { email, role = nameof(Role.Professional) }));

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetGuid();

        await Ok(admin.PostAsync($"/api/staff-accounts/{id}/disable"));
        await Ok(admin.PostAsync($"/api/staff-accounts/{id}/enable"));

        await fixture.WithDatabaseAsync(async database =>
        {
            var restored = await database.Users.SingleAsync(candidate => candidate.Id == id);

            // Not Active: an invitation restored as active would be an account that may hold a
            // session while having no identity behind it.
            Assert.Equal(UserStatus.PendingClaim, restored.Status);
            Assert.True(restored.AwaitsClaim);
        });
    }

    [Fact]
    public async Task A_deactivated_account_cannot_be_restored()
    {
        // Deactivation released the address, so it may already belong to a live account.
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var (_, user) = await fixture.AsRoleAsync(Role.FrontDesk);

        await Ok(admin.PostAsync($"/api/staff-accounts/{user.Id}/deactivate"));

        var response = await admin.PostAsync($"/api/staff-accounts/{user.Id}/enable");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_an_administrator_may_restore_an_account()
    {
        var (admin, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _admin = admin;

        var (desk, user) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _desk = desk;

        await Ok(admin.PostAsync($"/api/staff-accounts/{user.Id}/disable"));

        var (other, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _other = other;

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await other.PostAsync($"/api/staff-accounts/{user.Id}/enable")).StatusCode);
    }

    /// <summary>Asserts the call succeeded, reporting the body when it did not.</summary>
    private static async Task<HttpResponseMessage> Ok(Task<HttpResponseMessage> call)
    {
        var response = await call;

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        return response;
    }
}
