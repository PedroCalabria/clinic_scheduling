using System.Security.Claims;
using System.Text.Json.Serialization;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DomainPasswordHasher = Clinic.Domain.Identity.IPasswordHasher;

namespace Clinic.Api.Features.StaffAccounts;

/// <summary>
/// The API behind S11 — staff accounts and professional invitations, administrator only.
/// </summary>
/// <remarks>
/// <para>
/// S11 is load-bearing in this change rather than a stub, because the invite-first rule makes
/// it the only way a professional identity comes into existence (design A5). "Register the
/// professional by the email they will sign in with" is the whole mechanism that keeps a role
/// from being guessed at the identity provider.
/// </para>
/// <para>
/// What it deliberately does not do: manage patients. Patient records are created by the
/// sign-in flow, and a patient-search surface belongs with front-desk booking in change 5,
/// where it has a purpose and an <c>AccessLog</c> reason.
/// </para>
/// </remarks>
internal static class StaffAccountEndpoints
{
    internal static IEndpointRouteBuilder MapStaffAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/staff-accounts")
            // One policy for the whole group: every action here is structural configuration,
            // which is exactly the line between administrator and front desk
            // (01-requirements.md §Roles).
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/", ListAsync).WithName("ListStaffAccounts");
        group.MapGet("/by-email", FindByEmailAsync).WithName("FindStaffAccountByEmail");
        group.MapPost("/", CreateAsync).WithName("CreateStaffAccount");
        group.MapPost("/{userId:guid}/disable", DisableAsync).WithName("DisableStaffAccount");
        group.MapPost("/{userId:guid}/deactivate", DeactivateAsync).WithName("DeactivateStaffAccount");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var accounts = await database.Users
            .AsNoTracking()
            .Where(user => user.Role != Role.Patient && user.DeletedAtUtc == null)
            .OrderBy(user => user.Email)
            .Select(user => new StaffAccountResponse(
                user.Id,
                user.Email,
                user.Role.ToString(),
                user.Status.ToString(),
                user.AuthProvider.ToString(),
                user.Status == UserStatus.PendingClaim && user.ExternalSubjectId == null))
            .ToListAsync(cancellationToken);

        return Results.Ok(accounts);
    }

    /// <summary>
    /// Which account holds an address — the lookup that makes the recovery path usable
    /// (design D4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because <see cref="ListAsync"/> deliberately shows staff only, so the account most
    /// likely to be blocking an invitation — a patient provisioned by mistake — is invisible to
    /// the administrator who has to clear it. This answers one question about one address the
    /// administrator has just typed into the invite form.
    /// </para>
    /// <para>
    /// Not a patient search, and not a step towards one. It takes an exact normalized address and
    /// returns the same shape the list returns: id, role, status, and whether an invitation is
    /// still unclaimed. No name, no contact details — nothing the administrator did not already
    /// supply. Browsing patients belongs to change 5, where it has a purpose and an
    /// <c>AccessLog</c> reason.
    /// </para>
    /// </remarks>
    private static async Task<IResult> FindByEmailAsync(
        string? email,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return ApiError.Result(
                ErrorCodes.ValidationRequired,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["field"] = "email" });
        }

        string normalized;

        try
        {
            normalized = EmailAddress.Normalize(email);
        }
        catch (DomainRuleViolationException)
        {
            return ApiError.Result(
                ErrorCodes.ValidationInvalidFormat,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["field"] = "email" });
        }

        // Live accounts only — the same filter the uniqueness rule uses, so this answers exactly
        // the question the administrator is really asking: is this address taken?
        var account = await database.Users
            .AsNoTracking()
            .Where(user => user.Email == normalized && user.DeletedAtUtc == null)
            .Select(user => new StaffAccountResponse(
                user.Id,
                user.Email,
                user.Role.ToString(),
                user.Status.ToString(),
                user.AuthProvider.ToString(),
                user.Status == UserStatus.PendingClaim && user.ExternalSubjectId == null))
            .SingleOrDefaultAsync(cancellationToken);

        return account is null
            ? ApiError.Result(ErrorCodes.AccountNotFound, StatusCodes.Status404NotFound)
            : Results.Ok(account);
    }

    private static async Task<IResult> CreateAsync(
        CreateStaffAccountRequest request,
        ClinicDbContext database,
        DomainPasswordHasher passwordHasher,
        IOptions<AuthOptions> options,
        TimeProvider clock,
        ILogger<StaffAccountMarker> logger,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<Role>(request.Role, ignoreCase: true, out var role) || role == Role.Patient)
        {
            return ApiError.Result(
                ErrorCodes.ValidationInvalidFormat,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["field"] = "role" });
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return ApiError.Result(
                ErrorCodes.ValidationRequired,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["field"] = "email" });
        }

        string email;

        try
        {
            email = EmailAddress.Normalize(request.Email);
        }
        catch (DomainRuleViolationException)
        {
            return ApiError.Result(
                ErrorCodes.ValidationInvalidFormat,
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["field"] = "email" });
        }

        // Checked before creating for a clear answer, and backed by the filtered unique index
        // in case two administrators race: the constraint is the floor, this is the message.
        var taken = await database.Users.AnyAsync(
            user => user.Email == email && user.DeletedAtUtc == null,
            cancellationToken);

        if (taken)
        {
            return ApiError.Result(ErrorCodes.EmailAlreadyInUse, StatusCodes.Status409Conflict);
        }

        var now = clock.GetUtcNow();
        User account;

        if (role == Role.Professional)
        {
            // An invitation: role and email, no credential, awaiting the Google sign-in that
            // claims it. A password would be meaningless — professionals authenticate through
            // Google (01-requirements.md §Hybrid identity model).
            account = User.InviteProfessional(email, now);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Password)
                || request.Password.Length < options.Value.MinimumPasswordLength)
            {
                return ApiError.Result(
                    ErrorCodes.ValidationInvalidFormat,
                    StatusCodes.Status400BadRequest,
                    new Dictionary<string, object?>
                    {
                        ["field"] = "password",
                        ["minimumLength"] = options.Value.MinimumPasswordLength,
                    });
            }

            account = User.CreateInternalStaff(
                email,
                passwordHasher.Hash(request.Password),
                role,
                now,
                // The administrator typed this password, so the account holder does not own it
                // yet — same reasoning as the bootstrap credential (design A6).
                mustChangePassword: true);
        }

        database.Users.Add(account);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Created {Role} account {UserId} ({Provider}).", account.Role, account.Id, account.AuthProvider);

        return Results.Created(
            $"/api/staff-accounts/{account.Id}",
            new StaffAccountResponse(
                account.Id,
                account.Email,
                account.Role.ToString(),
                account.Status.ToString(),
                account.AuthProvider.ToString(),
                account.AwaitsClaim));
    }

    private static async Task<IResult> DisableAsync(
        Guid userId,
        ClinicDbContext database,
        SessionStore sessions,
        ILogger<StaffAccountMarker> logger,
        CancellationToken cancellationToken)
    {
        var account = await database.Users.SingleOrDefaultAsync(
            user => user.Id == userId && user.DeletedAtUtc == null,
            cancellationToken);

        if (account is null)
        {
            return ApiError.Result(ErrorCodes.AccountNotFound, StatusCodes.Status404NotFound);
        }

        account.Disable();
        await database.SaveChangesAsync(cancellationToken);

        // Disabling has to end access that already exists, not only prevent new sign-ins.
        // Session resolution re-reads the account status too, so this is belt and braces —
        // deliberately, because "the account is off" should not depend on one mechanism.
        await sessions.RevokeAllForUserAsync(account.Id, cancellationToken);

        logger.LogWarning("Disabled account {UserId} and revoked its sessions.", account.Id);

        return Results.NoContent();
    }

    /// <summary>
    /// Retires an account: ends its access AND releases its address (design D4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recovery half of <c>staff-google-guard</c>. <c>00-context.md</c> §5 has always said the
    /// way to fix a mistakenly-created account is to deactivate it and invite the address anew —
    /// but nothing in the product could do the first half. <see cref="DisableAsync"/> turns an
    /// account off while keeping its address, so a patient created by mistake went on blocking
    /// the professional invitation forever.
    /// </para>
    /// <para>
    /// Releasing the address needs no new rule: <c>ix_users_email_live</c> is filtered to
    /// <c>deleted_at_utc IS NULL</c> and every by-email lookup filters the same way, so the
    /// address is free the moment the row is soft-deleted (I10 — the row and its history stay).
    /// </para>
    /// <para>
    /// Kept as a SECOND action rather than folded into <see cref="DisableAsync"/>, tempting as
    /// that is given there is no un-disable: a soft-deleted account is not found by the password
    /// sign-in lookup, so merging them would quietly turn <c>auth.account_disabled</c> into
    /// <c>auth.invalid_credentials</c> for a deactivated internal account — a behaviour change on
    /// a path this change is not meant to touch. Revisit after change 5: if <c>disable</c> is
    /// still unused by any real workflow, collapse the two deliberately.
    /// </para>
    /// <para>
    /// Reachable for an account of ANY role, patients included. That is the point — the account
    /// in the way is usually a patient, and <see cref="ListAsync"/> does not show those.
    /// </para>
    /// </remarks>
    private static async Task<IResult> DeactivateAsync(
        Guid userId,
        ClaimsPrincipal principal,
        ClinicDbContext database,
        SessionStore sessions,
        TimeProvider clock,
        ILogger<StaffAccountMarker> logger,
        CancellationToken cancellationToken)
    {
        // Before the lookup, because it needs no lookup: an administrator retiring their own
        // account would revoke their own session on the way out, and if they were the only one
        // left the clinic would have no way back into S11. The first destructive account action
        // this product has is not the place to leave that open.
        if (principal.UserId() == userId)
        {
            return ApiError.Result(ErrorCodes.Forbidden, StatusCodes.Status403Forbidden);
        }

        var account = await database.Users.SingleOrDefaultAsync(
            user => user.Id == userId && user.DeletedAtUtc == null,
            cancellationToken);

        if (account is null)
        {
            // Covers "no such account" and "already deactivated" with one answer, because from
            // the perspective of live data those are the same fact.
            return ApiError.Result(ErrorCodes.AccountNotFound, StatusCodes.Status404NotFound);
        }

        account.SoftDelete(clock.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);

        await sessions.RevokeAllForUserAsync(account.Id, cancellationToken);

        // Warning level with the actor named: this releases an identity, and six months from now
        // the question "who retired this account, and what was it?" needs an answer.
        logger.LogWarning(
            "Administrator {ActorId} deactivated {Role} account {UserId}, releasing its address, and revoked its sessions.",
            principal.UserId(), account.Role, account.Id);

        return Results.NoContent();
    }

    /// <summary>Anchor for the slice's logger category.</summary>
    private sealed class StaffAccountMarker;

    internal sealed record CreateStaffAccountRequest(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("password")] string? Password);

    internal sealed record StaffAccountResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("authProvider")] string AuthProvider,
        [property: JsonPropertyName("awaitsClaim")] bool AwaitsClaim);
}
