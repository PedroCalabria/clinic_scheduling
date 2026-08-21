using Clinic.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using DomainPasswordHasher = Clinic.Domain.Identity.IPasswordHasher;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Implements the domain's password port with ASP.NET's <see cref="PasswordHasher{TUser}"/>
/// — the hasher alone, with none of the Identity store around it (design A1, A7).
/// </summary>
/// <remarks>
/// <para>
/// The whole of ASP.NET Core Identity was rejected for this change: it would bring its own
/// user schema, colliding with the <see cref="User"/> entity 02-domain-model.md specifies,
/// and about ten tables to serve two internal roles. But the hashing itself is not where to
/// be original — iteration counts, salt handling, and a versioned hash format are exactly
/// the kind of code that is subtly wrong for years. So this borrows that one class.
/// </para>
/// <para>
/// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> is surfaced rather than
/// swallowed: it is how the framework says "correct password, but hashed with an older work
/// factor". The sign-in slice re-hashes on that outcome, so stored verifiers keep up with
/// the current defaults without anyone having to run a migration over them.
/// </para>
/// </remarks>
internal sealed class PasswordHasherAdapter(PasswordHasher<User> hasher) : DomainPasswordHasher
{
    public string Hash(string password) => hasher.HashPassword(user: null!, password);

    public PasswordVerificationOutcome Verify(string passwordHash, string providedPassword) =>
        hasher.VerifyHashedPassword(user: null!, passwordHash, providedPassword) switch
        {
            PasswordVerificationResult.Success => PasswordVerificationOutcome.Succeeded,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerificationOutcome.SucceededButNeedsRehash,
            _ => PasswordVerificationOutcome.Failed,
        };
}
