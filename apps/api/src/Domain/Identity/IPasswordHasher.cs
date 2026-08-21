namespace Clinic.Domain.Identity;

/// <summary>Whether a stored verifier matched, and whether it should be replaced.</summary>
public enum PasswordVerificationOutcome
{
    Failed = 0,

    Succeeded = 1,

    /// <summary>
    /// Correct, but produced by an older hashing configuration — the caller should re-hash
    /// so the stored verifier keeps up with the current work factor.
    /// </summary>
    SucceededButNeedsRehash = 2,
}

/// <summary>
/// The port through which the domain asks about passwords without knowing how they are
/// hashed (design A7).
/// </summary>
/// <remarks>
/// A plain interface, so the protected core keeps its no-infrastructure guarantee: the
/// implementation in <c>Api</c> delegates to ASP.NET's <c>PasswordHasher&lt;T&gt;</c>, used
/// as a standalone hasher with none of the Identity store around it. Hand-rolling iteration
/// counts and a versioned hash format is the wrong kind of originality (design A1).
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>Produces the verifier to store for a plaintext password.</summary>
    string Hash(string password);

    /// <summary>Checks a plaintext password against a stored verifier.</summary>
    PasswordVerificationOutcome Verify(string passwordHash, string providedPassword);
}
