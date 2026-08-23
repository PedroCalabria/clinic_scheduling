using NodaTime;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// A stretch of real time in which a professional cannot see a patient (design F5).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately says nothing about <em>why</em>. The solver subtracts one list of these and does
/// not care whether an interval came from a block the professional entered, an appointment
/// somebody booked, or an event synced out of Google. Three separately-typed sources would be
/// three code paths to the same union, and the place that genuinely needs to know the cause is
/// the write path, where an I7 refusal has to name it.
/// </para>
/// <para>
/// Today only internal <see cref="TimeBlock"/>s reach here. Change 5 appends appointments and
/// change 7 external blocks, to this same list, without the subtraction changing.
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
    private BusyInterval(Instant start, Instant end)
    {
        Start = start;
        End = end;
    }

    public Instant Start { get; }

    public Instant End { get; }

    /// <exception cref="DomainRuleViolationException">The interval is empty or reversed.</exception>
    public static BusyInterval Between(Instant start, Instant end)
    {
        if (end <= start)
        {
            throw new DomainRuleViolationException(
                $"A busy interval must end after it starts; got {start} to {end}.");
        }

        return new BusyInterval(start, end);
    }

    /// <summary>
    /// Whether this interval shares any instant with <paramref name="otherStart"/> to
    /// <paramref name="otherEnd"/>.
    /// </summary>
    /// <remarks>
    /// Half-open: touching endpoints do not overlap, so an appointment ending exactly when a
    /// block begins is offerable. The alternative refuses the most ordinary schedule there is —
    /// a visit that runs right up to lunch.
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

    public override string ToString() => $"{Start}..{End}";
}
