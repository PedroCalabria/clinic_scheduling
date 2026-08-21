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
}
