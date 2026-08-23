using NodaTime;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// Where a block came from (02-domain-model.md §2).
/// </summary>
/// <remarks>
/// One value today. Change 7 adds <c>External</c> for blocks arriving from a professional's
/// Google Calendar, along with the columns only that case needs. A discriminator with a single
/// value is honest here because the second value is designed rather than speculated — the
/// alternative is renaming a table in change 7 (design F9).
/// </remarks>
public enum TimeBlockSource
{
    /// <summary>Entered by the professional in S3.</summary>
    Internal = 1,
}

/// <summary>
/// A period in which a professional is unavailable (02-domain-model.md §2, design F9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Instants, deliberately unlike working hours.</b> "Every Monday 09:00" is a rule and is
/// stored as wall clock because it generates events only once a date is supplied. "I am out on
/// 25 August from 14:00" is an event: the date is already supplied, so there is exactly one
/// instant and storing anything else would be storing less. Both halves of
/// <c>00-context.md</c> §5 are therefore visible in this capability, and both have a schema
/// test.
/// </para>
/// <para>
/// The conversion from what the professional typed into an instant happens at the edge, in the
/// slice, using the configured clinic timezone — the same place and the same resolver the
/// solver uses, so a block and a working hour can never disagree about what a wall-clock time
/// meant.
/// </para>
/// <para>
/// Overlapping blocks for one professional are allowed (design F10). They union for
/// availability, so both mean the same thing: busy. The contrast with working hours is the
/// point — overlapping <em>hours</em> are refused because two rules covering one moment leave
/// genuine ambiguity about which applies, and two blocks covering one moment have no ambiguity
/// to resolve.
/// </para>
/// </remarks>
public sealed class TimeBlock
{
    /// <summary>EF materialization only.</summary>
    private TimeBlock()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProfessionalId { get; private set; }

    public Instant StartsAt { get; private set; }

    public Instant EndsAt { get; private set; }

    public TimeBlockSource Source { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Null while in force; set when retired. Soft-delete only (I10).</summary>
    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    /// <summary>The block as the solver consumes it — one busy interval among many.</summary>
    public BusyInterval Interval => BusyInterval.Between(StartsAt, EndsAt);

    /// <summary>
    /// Records a professional's own unavailability.
    /// </summary>
    /// <exception cref="DomainRuleViolationException">
    /// The professional is missing, or the range does not move forward.
    /// </exception>
    public static TimeBlock ForProfessional(
        Guid professionalId,
        Instant startsAt,
        Instant endsAt,
        DateTimeOffset createdAtUtc)
    {
        if (professionalId == Guid.Empty)
        {
            // Not reachable from a request: the slice takes this from the session, never from
            // the body, which is what makes "a block cannot be aimed at someone else"
            // structural rather than a rule somebody has to remember.
            throw new DomainRuleViolationException("A time block requires a professional.");
        }

        var block = new TimeBlock
        {
            Id = Guid.NewGuid(),
            ProfessionalId = professionalId,
            Source = TimeBlockSource.Internal,
            CreatedAtUtc = createdAtUtc,
        };

        block.Reschedule(startsAt, endsAt);

        return block;
    }

    /// <summary>
    /// Moves the block, re-checking the one rule it has.
    /// </summary>
    /// <remarks>
    /// One predicate covers both refusals: <c>end &lt;= start</c> is zero-length when they are
    /// equal and reversed when the end is earlier. The same shape as
    /// <c>WorkingHoursSpan.Between</c>, and for the same reason — a single rule cannot be half
    /// applied.
    /// </remarks>
    /// <exception cref="DomainRuleViolationException">The range does not move forward.</exception>
    public void Reschedule(Instant startsAt, Instant endsAt)
    {
        if (endsAt <= startsAt)
        {
            throw new DomainRuleViolationException(
                $"A time block must end after it starts; got {startsAt} to {endsAt}.");
        }

        StartsAt = startsAt;
        EndsAt = endsAt;
    }

    /// <summary>
    /// The busy intervals a set of blocks contributes to availability — active ones only.
    /// </summary>
    /// <remarks>
    /// The active predicate lives here, in the domain, rather than being spelled out at each
    /// call site. That is the mistake 3a and 3b both guarded against in the other direction:
    /// a rule applied to the wrong subset passes every unit test and is wrong in production.
    /// Putting it on the type means "a retired block subtracts nothing" is a unit-testable fact
    /// rather than a property of whichever query happened to be written last.
    /// </remarks>
    public static IReadOnlyList<BusyInterval> BusyIntervalsOf(IEnumerable<TimeBlock> blocks) =>
        blocks.Where(block => block.IsActive).Select(block => block.Interval).ToList();

    /// <summary>Retires the block, which stops it removing availability (I10).</summary>
    public void Retire(DateTimeOffset retiredAtUtc) => DeactivatedAtUtc ??= retiredAtUtc;

    /// <summary>
    /// Puts the block back in force.
    /// </summary>
    /// <remarks>
    /// Offered because retirement is reversible everywhere else in this system (design D1), and
    /// a block points at nothing that could have been retired underneath it — so unlike a
    /// catalog entity, there is no reference to re-validate.
    /// </remarks>
    public void Restore() => DeactivatedAtUtc = null;
}
