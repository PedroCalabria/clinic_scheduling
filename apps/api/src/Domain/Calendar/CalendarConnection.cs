namespace Clinic.Domain.Calendar;

/// <summary>
/// Which external calendar provider a connection is to.
/// </summary>
/// <remarks>
/// An enum with one member, which is a deliberate shape rather than an unfinished one: the
/// connection is stored per provider so that adding a second one is a new member and a new
/// adapter, not a schema change. Outlook is anti-scope (<c>01-requirements.md</c>), so this
/// stays at one member until that changes.
/// </remarks>
public enum CalendarProvider
{
    Google = 1,
}

/// <summary>
/// What is true of a professional's calendar authorization right now — as far as we last saw.
/// </summary>
/// <remarks>
/// Three states, and the difference between the last two is who ended it. <b>Revoked</b> means
/// the professional (or Google) withdrew the grant on Google's side and we observed it;
/// <b>Disconnected</b> means they withdrew it here. The remedy is the same — reconnect — but the
/// sentence a screen should say is not, and a single "not connected" state would make S2 tell
/// somebody they never connected when in fact their permission lapsed.
/// </remarks>
public enum CalendarConnectionStatus
{
    Connected = 1,
    Revoked = 2,
    Disconnected = 3,
}

/// <summary>
/// A professional's authorization to their external calendar: one per professional, holding
/// the sealed long-lived credential and the state we last observed it in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The states are a domain rule; the sealing is not</b> (design K7). This type takes the
/// credential as an opaque string it never interprets, so the domain's rule is "there is
/// credential material" rather than "it is AES-GCM". A crypto reference here would be exactly
/// the boundary erosion <c>DomainBoundaryTests</c> exists to catch — and it would also make
/// the interesting rule below untestable without a key.
/// </para>
/// <para>
/// <b>The invariant worth stating out loud: a connection cannot be <see cref="CalendarConnectionStatus.Connected"/>
/// without credential material.</b> It is enforced in the type rather than at the call site,
/// because it is what makes the status trustworthy — 6b will read this status to decide whether
/// to enqueue an outbox row, and a connection that reads as connected while holding nothing
/// would produce work that can never succeed.
/// </para>
/// <para>
/// <b>Status carries a companion fact: when it was observed.</b> Nothing calls Google on a
/// schedule in this change (design K15), so a status is a memory of the last look rather than
/// current truth, and a screen that shows one without the other overstates what it knows. The
/// pairing is in the aggregate rather than in the response DTO so that no caller can report a
/// status while forgetting its date.
/// </para>
/// <para>
/// <b>No soft-delete column</b>, consistent with <c>Appointment</c>: <see cref="CalendarConnectionStatus.Disconnected"/>
/// is a richer fact than a deleted flag, and a second way for a row to stop counting is how a
/// rule becomes decorative. The row survives a withdrawal; the credential does not
/// (<see cref="Disconnect"/>).
/// </para>
/// </remarks>
public sealed class CalendarConnection
{
    private CalendarConnection()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The professional this authorization belongs to. Never changes.</summary>
    public Guid ProfessionalId { get; private set; }

    public CalendarProvider Provider { get; private set; }

    /// <summary>
    /// The calendar an event would be written to (design K13).
    /// </summary>
    /// <remarks>
    /// Recorded rather than assumed, so 6b addresses a column instead of hard-coding
    /// <c>"primary"</c> in a dispatcher — the version of this that cannot be changed later
    /// without a migration.
    /// </remarks>
    public string TargetCalendarId { get; private set; } = null!;

    /// <summary>
    /// The sealed long-lived credential, or null once withdrawn.
    /// </summary>
    /// <remarks>
    /// Opaque here on purpose (design K7). Null is a real, expected state — a disconnected
    /// connection holds nothing — which is why the column is nullable rather than carrying a
    /// sentinel that would be a second way to say "none".
    /// </remarks>
    public string? SealedCredential { get; private set; }

    public CalendarConnectionStatus Status { get; private set; }

    /// <summary>When this connection was first established.</summary>
    public DateTimeOffset ConnectedAtUtc { get; private set; }

    /// <summary>
    /// When <see cref="Status"/> was last observed to be what it says.
    /// </summary>
    /// <remarks>
    /// The honesty half of the pair described in the type's remarks. S2 renders it beside the
    /// status; 6b will move it as a side effect of dispatching, at which point the gap between
    /// observation and truth closes on its own.
    /// </remarks>
    public DateTimeOffset StateObservedAtUtc { get; private set; }

    /// <summary>True only when this connection could actually be used.</summary>
    public bool IsUsable =>
        Status == CalendarConnectionStatus.Connected && SealedCredential is not null;

    /// <summary>
    /// Establishes a professional's first connection.
    /// </summary>
    public static CalendarConnection Establish(
        Guid professionalId,
        CalendarProvider provider,
        string targetCalendarId,
        string sealedCredential,
        DateTimeOffset atUtc)
    {
        if (professionalId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A calendar connection must belong to a professional.");
        }

        if (string.IsNullOrWhiteSpace(targetCalendarId))
        {
            throw new DomainRuleViolationException("A calendar connection must name the calendar it targets.");
        }

        if (string.IsNullOrWhiteSpace(sealedCredential))
        {
            throw new DomainRuleViolationException(
                "A calendar connection cannot be established without credential material.");
        }

        return new CalendarConnection
        {
            Id = Guid.NewGuid(),
            ProfessionalId = professionalId,
            Provider = provider,
            TargetCalendarId = targetCalendarId.Trim(),
            SealedCredential = sealedCredential,
            Status = CalendarConnectionStatus.Connected,
            ConnectedAtUtc = atUtc,
            StateObservedAtUtc = atUtc,
        };
    }

    /// <summary>
    /// Re-establishes an existing connection after a revocation or a withdrawal.
    /// </summary>
    /// <param name="sealedCredential">
    /// Fresh credential material, or <see langword="null"/> when the provider returned none.
    /// </param>
    /// <remarks>
    /// <b>The null case is the point of this method</b> (design K6). Google returns a refresh
    /// token only on the first grant for a client/user pair, so a professional who disconnects
    /// and reconnects can complete a perfectly successful authorization that carries no
    /// credential at all. A handler that wrote that through would replace a working credential
    /// with nothing and report success.
    /// <para>
    /// So null means "keep what is held", and holding nothing while being given nothing is
    /// refused rather than recorded as a connection nobody can use. Two guards were designed for
    /// this — a <c>prompt=consent</c> on the request, and this one. This is the one that survives
    /// a change in Google's behaviour, because it does not depend on Google.
    /// </para>
    /// </remarks>
    public void Reconnect(string? sealedCredential, string targetCalendarId, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(targetCalendarId))
        {
            throw new DomainRuleViolationException("A calendar connection must name the calendar it targets.");
        }

        var credential = string.IsNullOrWhiteSpace(sealedCredential) ? SealedCredential : sealedCredential;

        if (credential is null)
        {
            throw new DomainRuleViolationException(
                "The provider returned no credential and none is held, so this connection cannot " +
                "be re-established.");
        }

        SealedCredential = credential;
        TargetCalendarId = targetCalendarId.Trim();
        Status = CalendarConnectionStatus.Connected;
        StateObservedAtUtc = atUtc;

        // ConnectedAtUtc is deliberately not touched: it records when this professional first
        // gave the clinic access, which is the fact a consent trail cares about. Reconnecting
        // renews the authorization, not the relationship.
    }

    /// <summary>Records that the connection was seen working, without changing anything else.</summary>
    public void ObserveUsable(DateTimeOffset observedAtUtc)
    {
        if (SealedCredential is null)
        {
            throw new DomainRuleViolationException(
                "A connection holding no credential cannot have been observed working.");
        }

        Status = CalendarConnectionStatus.Connected;
        StateObservedAtUtc = observedAtUtc;
    }

    /// <summary>
    /// Records that the provider no longer honours this authorization.
    /// </summary>
    /// <remarks>
    /// The credential is kept rather than cleared. It is worthless to Google, but clearing it
    /// would erase the difference between "your permission lapsed" and "you never connected" —
    /// and <see cref="Reconnect"/>'s null case reads it. Withdrawal is what clears material, and
    /// withdrawal is a decision somebody makes (<see cref="Disconnect"/>).
    /// </remarks>
    public void ObserveRevoked(DateTimeOffset observedAtUtc)
    {
        Status = CalendarConnectionStatus.Revoked;
        StateObservedAtUtc = observedAtUtc;
    }

    /// <summary>
    /// Withdraws the connection here: the credential is destroyed, the record is not.
    /// </summary>
    /// <remarks>
    /// <b>On I10 (soft-delete only).</b> The row survives — the history of having been connected
    /// is worth keeping, the same argument <c>booking-core</c> made for appointments. Clearing
    /// the credential is not a violation of that rule but the other half of it: I10 governs
    /// records, and keeping a withdrawn professional's calendar key "for history" would be data
    /// minimisation failing at the one point it is easiest to get right (design K10).
    /// </remarks>
    public void Disconnect(DateTimeOffset atUtc)
    {
        SealedCredential = null;
        Status = CalendarConnectionStatus.Disconnected;
        StateObservedAtUtc = atUtc;
    }
}
