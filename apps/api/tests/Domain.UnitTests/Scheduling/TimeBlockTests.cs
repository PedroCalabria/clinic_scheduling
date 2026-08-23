using Clinic.Domain.Scheduling;
using NodaTime;

namespace Clinic.Domain.UnitTests.Scheduling;

/// <summary>
/// The internal time block's one rule, and the rule it deliberately does not have (design F9, F10).
/// </summary>
public sealed class TimeBlockTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AProfessional = Guid.NewGuid();

    private static Instant At(int hour, int minute = 0) =>
        Instant.FromUtc(2026, 8, 24, hour, minute);

    private static TimeBlock Block(Instant start, Instant end) =>
        TimeBlock.ForProfessional(AProfessional, start, end, Recorded);

    [Fact]
    public void A_forward_range_is_accepted_and_internally_sourced()
    {
        var block = Block(At(14), At(15));

        Assert.Equal(At(14), block.StartsAt);
        Assert.Equal(At(15), block.EndsAt);
        Assert.Equal(TimeBlockSource.Internal, block.Source);
        Assert.True(block.IsActive);
    }

    [Fact]
    public void A_reversed_range_is_refused()
    {
        Assert.Throws<DomainRuleViolationException>(() => Block(At(15), At(14)));
    }

    [Fact]
    public void A_zero_length_range_is_refused()
    {
        // The same predicate as the reversed case, on purpose: a block of no length is not a
        // block, and one rule cannot be half applied.
        Assert.Throws<DomainRuleViolationException>(() => Block(At(15), At(15)));
    }

    [Fact]
    public void Rescheduling_re_checks_the_same_rule_and_leaves_a_bad_range_unstored()
    {
        var block = Block(At(14), At(15));

        block.Reschedule(At(16), At(17));

        Assert.Equal(At(16), block.StartsAt);

        Assert.Throws<DomainRuleViolationException>(() => block.Reschedule(At(18), At(18)));

        // The stored range survives a refused edit. A partially applied change would be worse
        // than a refused one.
        Assert.Equal(At(16), block.StartsAt);
        Assert.Equal(At(17), block.EndsAt);
    }

    [Fact]
    public void Overlapping_blocks_are_accepted()
    {
        // This test exists so the ABSENCE of an overlap rule is a decision on record rather than
        // an omission nobody noticed (design F10). Overlapping working HOURS are refused, because
        // two rules covering one moment leave real ambiguity about which applies. Two blocks
        // covering one moment have none — both say busy — so refusing them would be arbitrary.
        var first = Block(At(14), At(16));
        var second = Block(At(15), At(17));

        Assert.True(first.IsActive);
        Assert.True(second.IsActive);

        var intervals = TimeBlock.BusyIntervalsOf([first, second]);

        Assert.Equal(2, intervals.Count);
    }

    [Fact]
    public void Retiring_preserves_the_block_and_removes_it_from_the_busy_set()
    {
        var kept = Block(At(14), At(15));
        var retired = Block(At(16), At(17));

        retired.Retire(Recorded);

        Assert.False(retired.IsActive);
        Assert.Equal(At(16), retired.StartsAt);

        // The active predicate lives on the type, so "a retired block subtracts nothing" is a
        // fact about the domain rather than about whichever query was written last.
        var intervals = TimeBlock.BusyIntervalsOf([kept, retired]);

        Assert.Single(intervals);
        Assert.Equal(At(14), intervals[0].Start);
    }

    [Fact]
    public void Restoring_puts_the_block_back_in_force()
    {
        var block = Block(At(14), At(15));

        block.Retire(Recorded);
        block.Restore();

        Assert.True(block.IsActive);
        Assert.Single(TimeBlock.BusyIntervalsOf([block]));
    }

    [Fact]
    public void Touching_intervals_do_not_overlap()
    {
        var interval = BusyInterval.Between(At(14), At(15));

        // Half-open, and this is the case a naive implementation refuses: a visit that runs
        // right up to when the professional steps out.
        Assert.False(interval.Overlaps(At(13), At(14)));
        Assert.False(interval.Overlaps(At(15), At(16)));
        Assert.True(interval.Overlaps(At(14, 30), At(15, 30)));
        Assert.True(interval.Overlaps(At(13), At(16)));
    }

    [Fact]
    public void An_empty_busy_interval_cannot_be_constructed()
    {
        // The value that would silently subtract nothing. NodaTime's own Interval permits it,
        // which is why this type exists.
        Assert.Throws<DomainRuleViolationException>(() => BusyInterval.Between(At(14), At(14)));
    }
}
