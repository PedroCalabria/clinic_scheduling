using System.Security.Cryptography;
using System.Text;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// A server-side session — the single source of truth behind the cookie (design A1).
/// </summary>
/// <remarks>
/// <para>
/// This lives in <c>Api</c> rather than <c>Domain</c> on purpose (design A7): a session is
/// how a caller reaches the system, not something the clinic domain reasons about. The
/// protected core knows about users, roles, and ownership; it has no opinion on cookies.
/// </para>
/// <para>
/// The row stores a <em>hash</em> of the session token, never the token itself. The cookie
/// carries the token; a lookup hashes what was presented and matches on that. The cost is
/// one SHA-256 per request and the benefit is that a leaked database dump — or a backup, or
/// a careless query in a support session — yields no usable sessions. This is the same
/// reasoning that applies to any opaque bearer credential, and it does not weaken A1:
/// the row is still the only authority, and revocation still takes effect on the next
/// request.
/// </para>
/// </remarks>
internal sealed class Session
{
    /// <summary>Length of the raw token in bytes before base64url encoding.</summary>
    private const int TokenByteLength = 32;

    /// <summary>EF materialization only.</summary>
    private Session()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>SHA-256 of the token the cookie carries, hex-encoded.</summary>
    public string TokenHash { get; private set; } = null!;

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>
    /// Whether this session may still authenticate a request, as of <paramref name="nowUtc"/>.
    /// </summary>
    /// <remarks>
    /// Expiry is evaluated here, on read, rather than by a job that deletes stale rows: there
    /// is no scheduler until Hangfire arrives (change 6), and correctness must not wait for
    /// one. A sweep is the documented revisit trigger — see <c>SessionStore</c>.
    /// </remarks>
    public bool IsUsableAt(DateTimeOffset nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;

    /// <summary>
    /// Issues a session and returns it together with the raw token to put in the cookie.
    /// </summary>
    /// <remarks>
    /// The raw token exists only in this return value and in the response cookie. It is
    /// never stored, never logged, and cannot be recovered from the row.
    /// </remarks>
    public static (Session Session, string Token) Issue(Guid userId, DateTimeOffset nowUtc, TimeSpan lifetime)
    {
        var token = GenerateToken();

        var session = new Session
        {
            Id = Guid.NewGuid(),
            TokenHash = HashToken(token),
            UserId = userId,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.Add(lifetime),
        };

        return (session, token);
    }

    /// <summary>Flags the row. Effective on the very next request that presents it.</summary>
    public void Revoke(DateTimeOffset revokedAtUtc) => RevokedAtUtc ??= revokedAtUtc;

    /// <summary>Hashes a presented token into the form stored on the row.</summary>
    internal static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string GenerateToken() =>
        // Base64url so the value is cookie-safe without escaping.
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenByteLength))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
