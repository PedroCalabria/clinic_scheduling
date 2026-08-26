using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Infrastructure.Calendar;

/// <summary>
/// What a withdrawal did.
/// </summary>
/// <param name="HadConnection">
/// False when there was nothing to withdraw — an ordinary outcome, not a failure. Most accounts
/// have never connected a calendar, and most that are disabled never will have.
/// </param>
/// <param name="RevokedAtProvider">
/// Whether the grant is confirmed gone from Google's side. False when the provider could not be
/// reached, when the credential could not be opened, or when there was nothing to hand back.
/// <b>Never a reason to refuse the withdrawal</b> — see the type's remarks.
/// </param>
internal sealed record CalendarWithdrawalOutcome(bool HadConnection, bool RevokedAtProvider);

/// <summary>
/// Withdraws a professional's calendar authorization: here, and at Google (design K9).
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, three callers.</b> The professional withdrawing it themselves on S2,
/// an administrator disabling their account, and an administrator deactivating it must all mean
/// the same thing — consent revoked, credential destroyed, grant handed back. Three copies of
/// that sequence is how one of them quietly stops revoking at Google, and the failure would be
/// invisible from inside this system: everything here would look withdrawn while the clinic kept
/// live write access to somebody's personal calendar. A shared primitive, in
/// <c>Infrastructure</c> beside <c>SessionStore</c>, for the same reason and by the same
/// precedent.
/// </para>
/// <para>
/// <b>The local withdrawal is unconditional; the provider call is best effort.</b> Whoever asked
/// for this — the professional, or an administrator ending their access — is entitled to have it
/// happen. Refusing because Google is unreachable would leave a connection alive against a stated
/// decision, and a retry that keeps failing while Google is down is not a remedy. So the outcome
/// reports whether the provider confirmed, and the caller says so rather than claiming a success
/// it cannot vouch for.
/// </para>
/// <para>
/// <b>Called for accounts of any role, including ones that never had a calendar.</b> That is why
/// <see cref="CalendarWithdrawalOutcome.HadConnection"/> exists: the disable path cannot know in
/// advance, and asking it to check first would put the "is there a connection" question in two
/// places.
/// </para>
/// </remarks>
internal sealed class CalendarWithdrawal(
    ClinicDbContext database,
    GoogleCalendarTokens tokens,
    CalendarTokenProtector protector,
    TimeProvider clock,
    ILogger<CalendarWithdrawal> logger)
{
    /// <summary>
    /// Withdraws the calendar authorization belonging to a user, by their user id.
    /// </summary>
    /// <remarks>
    /// Takes the <em>user</em> id rather than the professional id, because the callers that need
    /// it most — disable and deactivate — act on accounts and may be acting on somebody who is
    /// not a professional at all. Resolving the professional record is this type's job, not
    /// theirs.
    /// </remarks>
    internal async Task<CalendarWithdrawalOutcome> WithdrawForUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var professionalId = await database.Professionals
            .Where(professional => professional.UserId == userId)
            .Select(professional => (Guid?)professional.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // No professional record, so no connection can exist. Deliberately NOT filtered on
        // DeactivatedAtUtc: a professional whose configuration was retired may still hold a live
        // grant, and that is exactly the grant this is here to take back.
        return professionalId is null
            ? new CalendarWithdrawalOutcome(HadConnection: false, RevokedAtProvider: false)
            : await WithdrawAsync(professionalId.Value, userId, cancellationToken);
    }

    /// <summary>
    /// Withdraws a known professional's authorization, saving connection and consent together.
    /// </summary>
    internal async Task<CalendarWithdrawalOutcome> WithdrawAsync(
        Guid professionalId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var connection = await database.CalendarConnections
            .FirstOrDefaultAsync(candidate => candidate.ProfessionalId == professionalId, cancellationToken);

        if (connection is null)
        {
            return new CalendarWithdrawalOutcome(HadConnection: false, RevokedAtProvider: false);
        }

        // Read and hand back before the withdrawal clears the material the revoke call needs.
        var revokedAtProvider = false;

        if (connection.SealedCredential is { } stored)
        {
            try
            {
                revokedAtProvider = await tokens.RevokeAsync(protector.Open(stored), cancellationToken);
            }
            catch (CalendarTokenProtectionException exception)
            {
                // Unreadable credential — almost always a changed encryption key. It cannot be
                // handed back, and the withdrawal proceeds anyway: anything else would trap
                // somebody in a connection whose key we lost.
                logger.LogError(exception, "A stored calendar credential could not be opened for revocation.");
            }
        }

        var now = clock.GetUtcNow();

        connection.Disconnect(now);

        var consents = await database.Consents
            .Where(consent => consent.UserId == userId
                && consent.Type == ConsentType.CalendarSync
                && consent.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var consent in consents)
        {
            consent.Revoke(now);
        }

        // One save, so the cleared credential, the status and the withdrawn consent are a single
        // fact. A consent revoked without the credential being cleared would be the worst of the
        // possible partial states.
        await database.SaveChangesAsync(cancellationToken);

        return new CalendarWithdrawalOutcome(HadConnection: true, revokedAtProvider);
    }
}
