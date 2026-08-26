using System.Reflection;
using Clinic.Domain;
using Clinic.Domain.Identity;

namespace Clinic.Domain.UnitTests.Identity;

/// <summary>
/// Covers the identity invariants the whole authorization story rests on (design A5).
/// </summary>
/// <remarks>
/// These are unit tests on purpose: the rules are decisions the protected core makes with
/// no database in sight, so proving them needs no container. The integration tier proves
/// the endpoints honour them.
/// </remarks>
public sealed class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Role_has_no_publicly_reachable_setter()
    {
        // The invariant is "role never changes after creation". A behavioural test can only
        // show that the methods which exist do not change it; this shows there is no setter
        // for a future caller to reach for either.
        var role = typeof(User).GetProperty(nameof(User.Role))!;

        Assert.Null(role.SetMethod?.IsPublic == true ? role.SetMethod : null);
    }

    [Fact]
    public void AuthProvider_has_no_publicly_reachable_setter()
    {
        var provider = typeof(User).GetProperty(nameof(User.AuthProvider))!;

        Assert.Null(provider.SetMethod?.IsPublic == true ? provider.SetMethod : null);
    }

    [Fact]
    public void No_public_method_assigns_role_or_auth_provider()
    {
        // Guards the invariant against a future method that quietly mutates either value:
        // if someone adds SetRole or Promote, this fails and they have to argue for it.
        var suspicious = typeof(User)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            // Property accessors are compiler-generated methods; get_Role is not a mutation.
            .Where(method => !method.IsSpecialName)
            .Where(method => method.Name.Contains("Role", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Promote", StringComparison.OrdinalIgnoreCase))
            .Select(method => method.Name)
            .ToArray();

        Assert.Empty(suspicious);
    }

    [Theory]
    [InlineData(Role.Patient)]
    [InlineData(Role.Professional)]
    public void Internal_accounts_are_refused_for_federated_roles(Role role)
    {
        var thrown = Assert.Throws<DomainRuleViolationException>(
            () => User.CreateInternalStaff("someone@clinic.test", "hash", role, Now));

        Assert.Contains("staff", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(Role.FrontDesk)]
    [InlineData(Role.Administrator)]
    public void Internal_staff_accounts_start_active(Role role)
    {
        var user = User.CreateInternalStaff("Front.Desk@Clinic.test ", "hash", role, Now);

        Assert.Equal(AuthProvider.Internal, user.AuthProvider);
        Assert.Equal(role, user.Role);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.True(user.CanAuthenticate);

        // Normalized on the way in, so the stored form is the only one ever compared.
        Assert.Equal("front.desk@clinic.test", user.Email);
    }

    [Fact]
    public void An_internal_account_requires_a_password_hash()
    {
        Assert.Throws<DomainRuleViolationException>(
            () => User.CreateInternalStaff("desk@clinic.test", "  ", Role.FrontDesk, Now));
    }

    [Fact]
    public void A_just_in_time_google_user_is_a_patient()
    {
        var user = User.RegisterGooglePatient("patient@example.test", "google-sub-1", Now);

        Assert.Equal(Role.Patient, user.Role);
        Assert.Equal(AuthProvider.Google, user.AuthProvider);
        Assert.Equal("google-sub-1", user.ExternalSubjectId);
        Assert.Null(user.PasswordHash);
        Assert.True(user.CanAuthenticate);
    }

    [Fact]
    public void An_invited_professional_awaits_a_claim_and_cannot_yet_authenticate()
    {
        var user = User.InviteProfessional("dr.a@example.test", Now);

        Assert.Equal(Role.Professional, user.Role);
        Assert.Equal(AuthProvider.Google, user.AuthProvider);
        Assert.True(user.AwaitsClaim);
        Assert.Null(user.ExternalSubjectId);

        // No password and no subject id, so neither login path can reach it until claimed.
        Assert.Null(user.PasswordHash);
        Assert.False(user.CanAuthenticate);
    }

    [Fact]
    public void Claiming_an_invitation_keeps_the_role_the_administrator_set()
    {
        var user = User.InviteProfessional("dr.a@example.test", Now);

        user.ClaimWithGoogleIdentity("google-sub-2");

        Assert.Equal(Role.Professional, user.Role);
        Assert.Equal("google-sub-2", user.ExternalSubjectId);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.False(user.AwaitsClaim);
    }

    [Fact]
    public void An_internal_account_cannot_be_claimed_through_google()
    {
        // The security rule behind the refusal: otherwise controlling a front-desk mailbox
        // at the provider would be enough to sign in as staff (design A5).
        var user = User.CreateInternalStaff("desk@clinic.test", "hash", Role.FrontDesk, Now);

        Assert.Throws<DomainRuleViolationException>(() => user.ClaimWithGoogleIdentity("google-sub-3"));

        Assert.Null(user.ExternalSubjectId);
        Assert.Equal(AuthProvider.Internal, user.AuthProvider);
    }

    [Fact]
    public void An_already_bound_account_cannot_be_claimed_again()
    {
        var user = User.RegisterGooglePatient("patient@example.test", "google-sub-4", Now);

        Assert.Throws<DomainRuleViolationException>(() => user.ClaimWithGoogleIdentity("google-sub-other"));

        Assert.Equal("google-sub-4", user.ExternalSubjectId);
    }

    [Fact]
    public void A_deleted_invitation_cannot_be_claimed()
    {
        var user = User.InviteProfessional("dr.gone@example.test", Now);
        user.SoftDelete(Now);

        Assert.Throws<DomainRuleViolationException>(() => user.ClaimWithGoogleIdentity("google-sub-5"));
    }

    [Fact]
    public void Failed_attempts_lock_the_account_at_the_configured_threshold()
    {
        var user = User.CreateInternalStaff("desk@clinic.test", "hash", Role.FrontDesk, Now);

        user.RecordFailedSignIn(lockoutThreshold: 3);
        user.RecordFailedSignIn(lockoutThreshold: 3);

        Assert.Equal(UserStatus.Active, user.Status);

        user.RecordFailedSignIn(lockoutThreshold: 3);

        Assert.Equal(UserStatus.Locked, user.Status);
        Assert.False(user.CanAuthenticate);
    }

    [Fact]
    public void A_successful_sign_in_clears_the_failed_streak()
    {
        var user = User.CreateInternalStaff("desk@clinic.test", "hash", Role.FrontDesk, Now);
        user.RecordFailedSignIn(lockoutThreshold: 5);
        user.RecordFailedSignIn(lockoutThreshold: 5);

        user.RecordSuccessfulSignIn();

        Assert.Equal(0, user.FailedSignInCount);
    }

    [Fact]
    public void Setting_a_password_clears_the_forced_change_and_unlocks()
    {
        var user = User.CreateInternalStaff(
            "admin@clinic.test", "bootstrap-hash", Role.Administrator, Now, mustChangePassword: true);

        user.RecordFailedSignIn(lockoutThreshold: 1);
        Assert.Equal(UserStatus.Locked, user.Status);

        user.SetPassword("new-hash");

        Assert.False(user.MustChangePassword);
        Assert.Equal("new-hash", user.PasswordHash);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(0, user.FailedSignInCount);
    }

    [Fact]
    public void A_federated_user_has_no_password_to_set()
    {
        var user = User.RegisterGooglePatient("patient@example.test", "google-sub-6", Now);

        Assert.Throws<DomainRuleViolationException>(() => user.SetPassword("hash"));
    }

    [Fact]
    public void Disabling_and_deleting_both_end_the_ability_to_authenticate()
    {
        var disabled = User.CreateInternalStaff("a@clinic.test", "hash", Role.FrontDesk, Now);
        disabled.Disable();

        var deleted = User.CreateInternalStaff("b@clinic.test", "hash", Role.FrontDesk, Now);
        deleted.SoftDelete(Now);

        Assert.False(disabled.CanAuthenticate);
        Assert.False(deleted.CanAuthenticate);

        // Soft-delete only (I10) — the row is still there, marked.
        Assert.True(deleted.IsDeleted);
        Assert.NotNull(deleted.DeletedAtUtc);
    }

    [Fact]
    public void Restoring_a_disabled_staff_account_makes_it_usable_again()
    {
        var user = User.CreateInternalStaff("desk@clinic.test", "hash", Role.FrontDesk, Now);
        user.Disable();

        user.Enable();

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.True(user.CanAuthenticate);
    }

    [Fact]
    public void Restoring_an_unclaimed_invitation_leaves_it_claimable_rather_than_active()
    {
        // The state is derived rather than remembered, and this is the case that makes the
        // derivation worth having: restoring an invitation as Active would produce an account
        // that may hold a session while having no identity behind it.
        var invitation = User.InviteProfessional("dr.a@example.test", Now);
        invitation.Disable();

        invitation.Enable();

        Assert.Equal(UserStatus.PendingClaim, invitation.Status);
        Assert.True(invitation.AwaitsClaim);
        Assert.False(invitation.CanAuthenticate);
    }

    [Fact]
    public void Restoring_a_claimed_professional_returns_it_to_active()
    {
        var professional = User.InviteProfessional("dr.b@example.test", Now);
        professional.ClaimWithGoogleIdentity("google-subject-1");
        professional.Disable();

        professional.Enable();

        Assert.Equal(UserStatus.Active, professional.Status);
        Assert.True(professional.CanAuthenticate);
    }

    [Fact]
    public void Restoring_clears_the_failed_attempt_streak_so_it_cannot_immediately_relock()
    {
        // Leaving the count in place would let the next bad password re-lock the account — a
        // restore that looks broken. An administrator acting now outranks a stale streak.
        var user = User.CreateInternalStaff("desk@clinic.test", "hash", Role.FrontDesk, Now);
        user.RecordFailedSignIn(lockoutThreshold: 2);
        user.RecordFailedSignIn(lockoutThreshold: 2);

        Assert.Equal(UserStatus.Locked, user.Status);

        user.Disable();
        user.Enable();

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(0, user.FailedSignInCount);
    }

    [Fact]
    public void A_deactivated_account_cannot_be_restored()
    {
        // Deactivation releases the address, so it may already belong to a live account.
        // Restoring would produce two live accounts on one address, or fail against the filtered
        // unique index — a database error standing in for the rule that means it.
        var user = User.CreateInternalStaff("desk@clinic.test", "hash", Role.FrontDesk, Now);
        user.SoftDelete(Now);

        Assert.Throws<DomainRuleViolationException>(user.Enable);
        Assert.True(user.IsDeleted);
        Assert.False(user.CanAuthenticate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@leading")]
    [InlineData("trailing@")]
    [InlineData("two@at@signs")]
    public void Structurally_unusable_emails_are_refused(string email)
    {
        Assert.Throws<DomainRuleViolationException>(() => EmailAddress.Normalize(email));
    }
}
