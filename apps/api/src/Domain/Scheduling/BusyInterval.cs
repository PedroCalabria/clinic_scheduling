using NodaTime;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// Why a professional or a room is busy, for the one caller that has to know.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added by <c>booking-core</c>, and change 4's design F5 predicted exactly this.</b> It said
/// the subtraction deliberately does not record why somebody is busy, and that "the place that
/// genuinely needs to know is the write path, where an I7 refusal has to name the cause". This is
/// that place: a patient told "someone just booked it" when the professional had in fact blocked
/// their own afternoon would go looking for a race that did not happen, so
/// <c>booking.slot_blocked</c> and <c>booking.slot_taken</c> are separate codes and something has
/// to tell them apart.
/// </para>
/// <para>
/// <b>What F5 promised is still true</b>: the subtraction ignores this value entirely. One list,
/// one comparison, one code path, whatever the origin. Only the refusal reads it, which is why
/// the discriminator lives on the value rather than splitting the list into three — three lists
/// would be three paths to the same union, which is the thing F5 rejected.
/// </para>
/// </remarks>
public enum BusyCause
{
    /// <summary>An internal <see cref="TimeBlock"/> the professional entered themselves (S3).</summary>
    InternalBlock = 1,

    /// <summary>A live <see cref="Appointment"/>. Also the only thing that occupies a room.</summary>
    Appointment = 2,

    /// <summary>
    /// A block synced from the professional's external calendar. Reached in change 7; declared
    /// now because the third producer is designed rather than speculated, and because a
    /// collision with one is a reconciliation conflict rather than a synchronous refusal.
    /// </summary>
    ExternalBlock = 3,
}

/// <summary>
/// A stretch of real time in which a professional cannot see a patient, or a room cannot be used
/// (design F5).
/// </summary>
/// <remarks>
/// <para>
/// The solver subtracts one list of these and does not care whether an interval came from a block
/// the professional entered, an appointment somebody booked, or an event synced out of Google.
/// Three separately-typed sources would be three code paths to the same union.
/// </para>
/// <para>
/// <see cref="Cause"/> exists for the write path only — see <see cref="BusyCause"/>. Nothing in
/// the overlap arithmetic below reads it, and a test asserts that two intervals differing only in
/// cause subtract identically, so the claim is checked rather than merely stated.
/// </para>
/// <para>
/// NodaTime's own <c>Interval</c> was considered. It permits an empty interval, which is exactly
/// the value that would silently subtract nothing, and it has no half-open overlap predicate —
/// so a local type that refuses empties at construction and names its comparison is the clearer
/// choice, and it mirrors <c>WorkingHoursSpan</c>.
/// </para>
/// </remarks>
public readonly record struct BusyInterval
{
    private BusyInterval(Instant start, Instant end, BusyCause cause)
    {
        Start = start;
        End = end;
        Cause = cause;
    }

    public Instant Start { get; }

    public Instant End { get; }

    /// <summary>
    /// What made this time busy. Read by the booking refusal, ignored by the subtraction.
    /// </summary>
    public BusyCause Cause { get; }

    /// <summary>
    /// Creates an interval, naming what made it busy.
    /// </summary>
    /// <remarks>
    /// The cause is required rather than defaulted. A default would be silently wrong for exactly
    /// one of the producers, and the failure — a blocked slot refused as "just taken" — is a
    /// message a patient acts on incorrectly rather than an error anybody would see.
    /// </remarks>
    /// <exception cref="DomainRuleViolationException">The interval is empty or reversed.</exception>
    public static BusyInterval Between(Instant start, Instant end, BusyCause cause)
    {
        if (end <= start)
        {
            throw new DomainRuleViolationException(
                $"A busy interval must end after it starts; got {start} to {end}.");
        }

        return new BusyInterval(start, end, cause);
    }

    /// <summary>
    /// Whether this interval shares any instant with <paramref name="otherStart"/> to
    /// <paramref name="otherEnd"/>.
    /// </summary>
    /// <remarks>
    /// Half-open: touching endpoints do not overlap, so an appointment ending exactly when a
    /// block begins is offerable. The alternative refuses the most ordinary schedule there is —
    /// a visit that runs right up to lunch.
    ///
    /// This is also the comparison the database's exclusion constraints have to agree with, which
    /// is why the <c>tstzrange</c> column stores <c>[)</c> ranges: a closed range there would
    /// refuse abutting appointments the solver had offered.
    /// </remarks>
    public bool Overlaps(Instant otherStart, Instant otherEnd) =>
        Overlaps(otherStart, otherEnd, Duration.Zero);

    /// <summary>
    /// Overlap where this interval is treated as extending <paramref name="trailing"/> past its
    /// end.
    /// </summary>
    /// <remarks>
    /// The turnaround buffer (02-domain-model.md, decision F1): a room is not free the instant an
    /// appointment ends, it is free once it has been cleaned. Trailing only — a buffer before the
    /// start would be prep time nobody has asked for, and doubling it would silently halve a
    /// small clinic's capacity.
    ///
    /// Kept here rather than in the solver so all interval arithmetic in this system lives in one
    /// type. A buffer applied in two places is a buffer applied inconsistently.
    /// </remarks>
    public bool Overlaps(Instant otherStart, Instant otherEnd, Duration trailing) =>
        Start < otherEnd && otherStart < End + trailing;

    public override string ToString() => $"{Start}..{End} ({Cause})";
}
