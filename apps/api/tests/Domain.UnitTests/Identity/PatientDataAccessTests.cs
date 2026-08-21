using Clinic.Domain;
using Clinic.Domain.Identity;

namespace Clinic.Domain.UnitTests.Identity;

/// <summary>
/// Covers the ownership rule (design A8) and the consent record that P7 exposes.
/// </summary>
public sealed class PatientDataAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid PatientA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PatientB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Staff = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    [Fact]
    public void A_patient_reaches_their_own_data_without_being_logged()
    {
        var decision = PatientDataAccess.Evaluate(Role.Patient, PatientA, PatientA);

        Assert.Equal(PatientDataAccessDecision.AllowedAsOwner, decision);
        Assert.True(decision.IsAllowed());
        Assert.False(decision.RequiresAccessRecord());
    }

    [Fact]
    public void A_patient_is_refused_another_patients_data()
    {
        var decision = PatientDataAccess.Evaluate(Role.Patient, PatientA, PatientB);

        Assert.Equal(PatientDataAccessDecision.Denied, decision);
        Assert.False(decision.IsAllowed());
    }

    [Theory]
    [InlineData(Role.FrontDesk)]
    [InlineData(Role.Administrator)]
    public void Staff_reach_patient_data_and_the_access_is_recorded(Role role)
    {
        var decision = PatientDataAccess.Evaluate(role, Staff, PatientA);

        Assert.Equal(PatientDataAccessDecision.AllowedAsStaff, decision);
        Assert.True(decision.IsAllowed());
        Assert.True(decision.RequiresAccessRecord());
    }

    [Fact]
    public void A_professional_has_no_blanket_access_to_patient_data()
    {
        // Least privilege until a requirement says otherwise: change 5 grants this with the
        // scoping that makes it defensible ("patients I have appointments with").
        var decision = PatientDataAccess.Evaluate(Role.Professional, Staff, PatientA);

        Assert.Equal(PatientDataAccessDecision.Denied, decision);
    }

    [Fact]
    public void Registering_a_patient_falls_back_to_the_email_local_part_for_a_missing_name()
    {
        var patient = Patient.Register(PatientA, fullName: null, contactEmail: "Jo.Doe@Example.test", Now);

        Assert.Equal("jo.doe", patient.FullName);
        Assert.Equal("jo.doe@example.test", patient.ContactEmail);

        // Minimization: a phone number has no purpose until an appointment exists (change 5).
        Assert.Null(patient.ContactPhone);
    }

    [Fact]
    public void A_patient_can_correct_their_own_details_and_clear_what_they_volunteered()
    {
        var patient = Patient.Register(PatientA, "Jo Doe", "jo@example.test", Now);

        patient.UpdateContactDetails("Josephine Doe", " +55 81 90000-0000 ");
        Assert.Equal("+55 81 90000-0000", patient.ContactPhone);

        patient.UpdateContactDetails("Josephine Doe", "   ");
        Assert.Null(patient.ContactPhone);
    }

    [Fact]
    public void A_patient_must_have_a_name()
    {
        var patient = Patient.Register(PatientA, "Jo Doe", "jo@example.test", Now);

        Assert.Throws<DomainRuleViolationException>(() => patient.UpdateContactDetails("  ", null));
    }

    [Fact]
    public void Revoking_a_consent_keeps_the_grant_on_the_record()
    {
        var consent = Consent.Grant(PatientA, ConsentType.DataProcessing, "2026-08", Now);

        consent.Revoke(Now.AddDays(6));

        Assert.False(consent.IsActive);
        Assert.Equal(Now, consent.GrantedAtUtc);
        Assert.Equal(Now.AddDays(6), consent.RevokedAtUtc);
    }

    [Fact]
    public void A_consent_cannot_be_revoked_twice_or_before_it_was_granted()
    {
        var consent = Consent.Grant(PatientA, ConsentType.DataProcessing, "2026-08", Now);

        Assert.Throws<DomainRuleViolationException>(() => consent.Revoke(Now.AddDays(-1)));

        consent.Revoke(Now);
        Assert.Throws<DomainRuleViolationException>(() => consent.Revoke(Now));
    }

    [Fact]
    public void A_consent_records_which_version_was_agreed_to()
    {
        Assert.Throws<DomainRuleViolationException>(
            () => Consent.Grant(PatientA, ConsentType.DataProcessing, " ", Now));
    }

    [Fact]
    public void An_access_record_names_the_actor_the_patient_and_the_action()
    {
        var entry = AccessLog.Record(Staff, PatientA, PatientDataAction.Viewed, Now);

        Assert.Equal(Staff, entry.ActorUserId);
        Assert.Equal(PatientA, entry.PatientId);
        Assert.Equal(PatientDataAction.Viewed, entry.Action);
        Assert.Equal(Now, entry.OccurredAtUtc);
    }
}
