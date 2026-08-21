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
        group.MapPost("/", CreateAsync).WithName("CreateStaffAccount");
        group.MapPost("/{userId:guid}/disable", DisableAsync).WithName("DisableStaffAccount");

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
