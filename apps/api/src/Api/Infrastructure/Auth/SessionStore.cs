using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Who the caller is, resolved from a session. Everything downstream of authentication sees
/// this and nothing about how the user signed in (Decision J).
/// </summary>
internal sealed record SessionPrincipal(
    Guid UserId,
    Guid SessionId,
    Role Role,
    bool MustChangePassword);

/// <summary>
/// Issues, resolves, and revokes sessions — the authority the cookie merely points at
/// (design A1).
/// </summary>
/// <remarks>
/// <para>
/// Every authenticated request lands in <see cref="ResolveAsync"/>. That is one indexed
/// lookup, and it is what makes revocation true rather than eventual: there is no signed
/// copy of the principal in the cookie that could still be believed after the row says
/// otherwise. The cost is a database round-trip per request; the alternative was the same
/// round-trip plus a stale-copy failure mode (design A1). Revisit on measurement, and not
/// with a cache — a cache reintroduces exactly the staleness this design exists to avoid.
/// </para>
/// <para>
/// CAVEAT (recorded, not overlooked): expiry is enforced on read and nothing deletes expired
/// rows, so the table grows with traffic. Bounded by session lifetime and this project's
/// volume, which is negligible. The revisit trigger is Hangfire arriving in change 6b (6a is
/// request/response throughout and adds no scheduler), when a
/// sweep costs almost nothing to add — see the non-goal in this change's design.
/// </para>
/// </remarks>
internal sealed class SessionStore(
    ClinicDbContext database,
    TimeProvider clock,
    IOptions<AuthOptions> options)
{
    /// <summary>
    /// Issues a session for a user and returns the raw token for the cookie.
    /// </summary>
    /// <remarks>
    /// Refuses an account that cannot authenticate, so "disabled" cannot be bypassed by a
    /// caller that reached this method through some other path.
    /// </remarks>
    public async Task<(string Token, DateTimeOffset ExpiresAtUtc)> IssueAsync(
        User user,
        CancellationToken cancellationToken)
    {
        if (!user.CanAuthenticate)
        {
            throw new InvalidOperationException(
                "A session cannot be issued for an account that is not active.");
        }

        var now = clock.GetUtcNow();
        var (session, token) = Session.Issue(user.Id, now, options.Value.SessionLifetime);

        database.Sessions.Add(session);
        await database.SaveChangesAsync(cancellationToken);

        return (token, session.ExpiresAtUtc);
    }

    /// <summary>
    /// Resolves a presented token, or null when it is unknown, expired, revoked, or belongs
    /// to an account that may no longer authenticate.
    /// </summary>
    /// <remarks>
    /// The user's status is re-read here rather than trusted from when the session was
    /// issued. That is what makes "disabling an account ends its access" hold for sessions
    /// that already exist, without the disable path having to find every one of them —
    /// though it revokes them too, so the effect does not depend on this check alone.
    /// </remarks>
    public async Task<SessionPrincipal?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenHash = Session.HashToken(token);
        var now = clock.GetUtcNow();

        // One indexed lookup, joined to the user so status is never stale. Projected to a
        // record so no entity is tracked on the read path.
        var candidate = await database.Sessions
            .AsNoTracking()
            .Where(session => session.TokenHash == tokenHash)
            .Join(
                database.Users.AsNoTracking(),
                session => session.UserId,
                user => user.Id,
                (session, user) => new
                {
                    session.Id,
                    session.ExpiresAtUtc,
                    session.RevokedAtUtc,
                    UserId = user.Id,
                    user.Role,
                    user.Status,
                    user.MustChangePassword,
                    user.DeletedAtUtc,
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            return null;
        }

        var sessionUsable = candidate.RevokedAtUtc is null && candidate.ExpiresAtUtc > now;
        var accountUsable = candidate.DeletedAtUtc is null && candidate.Status == UserStatus.Active;

        if (!sessionUsable || !accountUsable)
        {
            return null;
        }

        return new SessionPrincipal(
            candidate.UserId,
            candidate.Id,
            candidate.Role,
            candidate.MustChangePassword);
    }

    /// <summary>Revokes one session by the token that was presented (sign-out).</summary>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var tokenHash = Session.HashToken(token);

        var session = await database.Sessions
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, cancellationToken);

        if (session is null)
        {
            return;
        }

        session.Revoke(clock.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Revokes every session a user holds — what disabling an account does, so the effect is
    /// immediate rather than waiting for each session to expire.
    /// </summary>
    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var sessions = await database.Sessions
            .Where(session => session.UserId == userId && session.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.Revoke(now);
        }

        if (sessions.Count > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }
    }
}
