using NodaTime;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// An appointment's own stretch of real time — the value the database stores as a
/// <c>tstzrange</c> (design B3, B4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists when <see cref="BusyInterval"/> already holds two instants.</b> They are
/// different concepts that happen to share a shape: a busy interval is something the solver
/// subtracts, carrying a cause and answering overlap questions, and it is produced from blocks,
/// appointments and external events alike. This is the appointment's <em>identity in time</em> —
/// one persisted column, one aggregate, no cause, no arithmetic. Mapping the solver's type to a
/// schema column would tie the storage of one entity to a contract three producers share.
/// </para>
/// <para>
/// <b>Half-open, and it must stay that way.</b> The <c>tstzrange</c> column is written as
/// <c>[)</c> and <see cref="BusyInterval"/>'s overlap comparison is half-open, so an appointment
/// ending at 10:00 and one starting at 10:00 are accepted by the exclusion constraint and offered
/// by the solver. A closed range in either place would refuse the most ordinary schedule there is
/// and would make the read and the database disagree about the same two appointments.
/// </para>
/// <para>
/// Stored as one property rather than two so that EF maps it to the single range column the
/// constraint indexes; <c>StartsAt</c> and <c>EndsAt</c> on the aggregate read through to it.
/// </para>
/// </remarks>
public readonly record struct TimeRange
{
    private TimeRange(Instant start, Instant end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Inclusive lower bound.</summary>
    public Instant Start { get; }

    /// <summary>Exclusive upper bound.</summary>
    public Instant End { get; }

    public Duration Length => End - Start;

    /// <exception cref="DomainRuleViolationException">The range is empty or reversed.</exception>
    public static TimeRange Between(Instant start, Instant end)
    {
        if (end <= start)
        {
            throw new DomainRuleViolationException(
                $"A time range must end after it starts; got {start} to {end}.");
        }

        return new TimeRange(start, end);
    }

    public override string ToString() => $"[{Start}, {End})";
}
