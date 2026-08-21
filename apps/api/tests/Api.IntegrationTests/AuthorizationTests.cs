using System.Net;
using System.Text.Json;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The two layers of authorization (spec: role-based authorization, ownership, access
/// recording).
/// </summary>
/// <remarks>
/// The refusals are the tests worth having. An authorization change that accidentally allows
/// everything still passes every "it works" test in a suite, which is why each rule here is
/// asserted from both sides.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AuthorizationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Front_desk_is_refused_an_administrator_action()
    {
        var (frontDesk, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _frontDesk = frontDesk;

        var response = await frontDesk.PostAsync("/api/staff-accounts", new
        {
            email = $"should-not-exist-{Guid.NewGuid():N}@clinic.test",
            role = nameof(Role.FrontDesk),
            password = "a-long-enough-password",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await InternalSignInTests.ReadCodeAsync(response));
    }

    [Fact]
    public async Task An_administrator_may_perform_the_same_action()
    {
        var (administrator, _) = await fixture.AsRoleAsync(Role.Administrator);
        using var _administrator = administrator;

        var response = await administrator.PostAsync("/api/staff-accounts", new
        {
            email = $"new-desk-{Guid.NewGuid():N}@clinic.test",
            role = nameof(Role.FrontDesk),
            password = "a-long-enough-password",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_and_forbidden_are_different_answers()
    {
        // 401 says "I do not know who you are"; 403 says "I know, and no". Collapsing the two
        // would make a permission bug look like a session bug.
        using var anonymous = fixture.CreateAnonymousClient();
        var (frontDesk, _) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _frontDesk = frontDesk;

        var withoutSession = await anonymous.GetAsync("/api/staff-accounts");
        var withInsufficientRole = await frontDesk.GetAsync("/api/staff-accounts");

        Assert.Equal(HttpStatusCode.Unauthorized, withoutSession.StatusCode);
        Assert.Equal("auth.session_expired", await InternalSignInTests.ReadCodeAsync(withoutSession));

        Assert.Equal(HttpStatusCode.Forbidden, withInsufficientRole.StatusCode);
        Assert.Equal("auth.forbidden", await InternalSignInTests.ReadCodeAsync(withInsufficientRole));
    }

    [Fact]
    public async Task A_patient_reads_their_own_profile()
    {
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var response = await patient.GetAsync("/api/patients/me");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        Assert.Equal("Test Patient", document.RootElement.GetProperty("fullName").GetString());
        Assert.Single(document.RootElement.GetProperty("consents").EnumerateArray());
    }

    [Fact]
    public async Task A_patient_is_refused_another_patients_profile()
    {
        var (patientA, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patientA = patientA;

        var patientBUser = await fixture.SeedUserAsync(Role.Patient);
        var patientBId = await PatientIdForAsync(patientBUser.Id);

        var response = await patientA.GetAsync($"/api/patients/{patientBId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.ownership_denied", await InternalSignInTests.ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_patient_is_refused_updating_another_patients_profile()
    {
        var (patientA, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patientA = patientA;

        var patientBUser = await fixture.SeedUserAsync(Role.Patient);
        var patientBId = await PatientIdForAsync(patientBUser.Id);

        var response = await patientA.PutAsync($"/api/patients/{patientBId}", new
        {
            fullName = "Renamed By Somebody Else",
            contactPhone = "+55 00 00000-0000",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.ownership_denied", await InternalSignInTests.ReadCodeAsync(response));

        // And nothing changed.
        await fixture.WithDatabaseAsync(async database =>
        {
            var patient = await database.Patients.SingleAsync(candidate => candidate.Id == patientBId);

            Assert.Equal("Test Patient", patient.FullName);
        });
    }

    [Fact]
    public async Task A_nonexistent_record_looks_the_same_to_a_patient_as_one_they_do_not_own()
    {
        // Otherwise the endpoint becomes a way to discover which patients exist.
        var (patientA, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patientA = patientA;

        var patientBUser = await fixture.SeedUserAsync(Role.Patient);
        var existsButNotMine = await PatientIdForAsync(patientBUser.Id);

        var notMine = await patientA.GetAsync($"/api/patients/{existsButNotMine}");
        var doesNotExist = await patientA.GetAsync($"/api/patients/{Guid.NewGuid()}");

        Assert.Equal(notMine.StatusCode, doesNotExist.StatusCode);
        Assert.Equal(
            await notMine.Content.ReadAsStringAsync(),
            await doesNotExist.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_patient_updates_their_own_profile()
    {
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var response = await patient.PutAsync("/api/patients/me", new
        {
            fullName = "Josephine Doe",
            contactPhone = " +55 81 90000-0000 ",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await fixture.WithDatabaseAsync(async database =>
        {
            var record = await database.Patients.SingleAsync(candidate => candidate.UserId == user.Id);

            Assert.Equal("Josephine Doe", record.FullName);
            Assert.Equal("+55 81 90000-0000", record.ContactPhone);
        });
    }

    [Fact]
    public async Task A_patient_reading_their_own_data_is_not_logged()
    {
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await patient.GetAsync("/api/patients/me");

        await fixture.WithDatabaseAsync(async database =>
        {
            var patientId = await database.Patients
                .Where(candidate => candidate.UserId == user.Id)
                .Select(candidate => candidate.Id)
                .SingleAsync();

            Assert.False(await database.AccessLog.AnyAsync(entry => entry.PatientId == patientId));
        });
    }

    [Fact]
    public async Task Staff_reading_a_patients_data_is_recorded()
    {
        var (frontDesk, actor) = await fixture.AsRoleAsync(Role.FrontDesk);
        using var _frontDesk = frontDesk;

        var patientUser = await fixture.SeedUserAsync(Role.Patient);
        var patientId = await PatientIdForAsync(patientUser.Id);

        var response = await frontDesk.GetAsync($"/api/patients/{patientId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await fixture.WithDatabaseAsync(async database =>
        {
            var entry = await database.AccessLog.SingleAsync(candidate => candidate.PatientId == patientId);

            Assert.Equal(actor.Id, entry.ActorUserId);
            Assert.Equal(PatientDataAction.Viewed, entry.Action);
        });
    }

    [Fact]
    public async Task A_professional_has_no_blanket_access_to_patient_data()
    {
        // Least privilege until change 5 gives them a scoped, defensible reason.
        var (professional, _) = await fixture.AsRoleAsync(Role.Professional);
        using var _professional = professional;

        var patientUser = await fixture.SeedUserAsync(Role.Patient);
        var patientId = await PatientIdForAsync(patientUser.Id);

        var response = await professional.GetAsync($"/api/patients/{patientId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.ownership_denied", await InternalSignInTests.ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_patient_may_revoke_their_own_consent_and_the_grant_survives()
    {
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var response = await patient.PostAsync($"/api/patients/me/consents/{nameof(ConsentType.DataProcessing)}/revoke");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await fixture.WithDatabaseAsync(async database =>
        {
            var consent = await database.Consents.SingleAsync(candidate => candidate.UserId == user.Id);

            Assert.False(consent.IsActive);
            Assert.NotNull(consent.RevokedAtUtc);

            // The grant is still on the record — revoked, not erased.
            Assert.NotEqual(default, consent.GrantedAtUtc);
        });
    }

    [Fact]
    public async Task Revoking_a_consent_that_is_not_active_is_reported_not_repeated()
    {
        var (patient, _) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        await patient.PostAsync($"/api/patients/me/consents/{nameof(ConsentType.DataProcessing)}/revoke");

        var second = await patient.PostAsync($"/api/patients/me/consents/{nameof(ConsentType.DataProcessing)}/revoke");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("auth.consent_required", await InternalSignInTests.ReadCodeAsync(second));
    }

    private async Task<Guid> PatientIdForAsync(Guid userId)
    {
        var patientId = Guid.Empty;

        await fixture.WithDatabaseAsync(async database =>
            patientId = await database.Patients
                .Where(candidate => candidate.UserId == userId)
                .Select(candidate => candidate.Id)
                .SingleAsync());

        return patientId;
    }
}
