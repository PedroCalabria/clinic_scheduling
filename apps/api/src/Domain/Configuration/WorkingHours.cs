using NodaTime;

namespace Clinic.Domain.Configuration;

/// <summary>
/// A span of wall-clock time within a day, and the rules that make one usable.
/// </summary>
/// <remarks>
/// <see cref="LocalTime"/> rather than <see cref="TimeOnly"/> or a <see cref="TimeSpan"/>
/// because the type is the documentation: a <c>LocalTime</c> cannot accidentally be treated as
/// an instant, which is the entire point of design E3. Nothing here knows about a date, a
/// timezone, or UTC — supplying a date is change 4's job.
/// </remarks>
public readonly record struct WorkingHoursSpan
{
    private WorkingHoursSpan(LocalTime start, LocalTime end)
    {
        Start = start;
        End = end;
    }

    public LocalTime Start { get; }

    public LocalTime End { get; }

    /// <summary>
    /// Creates a span, refusing anything that is not a forward stretch of one day.
    /// </summary>
    /// <remarks>
    /// One predicate covers both refusals the spec names. <c>End &lt;= Start</c> is a
    /// zero-length span when they are equal, and a midnight-crossing one when the end is
    /// earlier — 22:00 to 02:00 arrives here as "end before start" because neither value
    /// carries a date.
    ///
    /// Midnight-crossing is refused rather than split into two spans. An overnight shift is not
    /// a case an outpatient clinic has, and turning one input into two records silently is the
    /// system deciding on the administrator's behalf — the same instinct 3a rejected for
    /// cascade-reactivation. If a genuine 24-hour service ever appears, that is a modelled case
    /// with its own representation, not a relaxed validation.
    /// </remarks>
    /// <exception cref="CatalogRuleViolationException">The span is zero-length or crosses midnight.</exception>
    public static WorkingHoursSpan Between(LocalTime start, LocalTime end)
    {
        if (end <= start)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.WorkingHoursInvalid,
                $"A working-hour span must end after it starts; got {start:HH:mm} to {end:HH:mm}.");
        }

        return new WorkingHoursSpan(start, end);
    }

    /// <summary>Whether two spans share any minute. Touching endpoints do not overlap.</summary>
    /// <remarks>
    /// Half-open comparison, so 08:00–12:00 and 12:00–17:00 are adjacent rather than
    /// conflicting — a clinic that works through with no gap is ordinary.
    /// </remarks>
    public bool Overlaps(WorkingHoursSpan other) => Start < other.End && other.Start < End;

    public override string ToString() => $"{Start:HH:mm}-{End:HH:mm}";
}

/// <summary>
/// The dates over which a recurring pattern applies. An open end means "until further notice".
/// </summary>
public readonly record struct EffectivePeriod
{
    private EffectivePeriod(LocalDate from, LocalDate? to)
    {
        From = from;
        To = to;
    }

    public LocalDate From { get; }

    /// <summary>Inclusive last day, or null for an open-ended pattern.</summary>
    public LocalDate? To { get; }

    /// <exception cref="CatalogRuleViolationException">The period ends before it begins.</exception>
    public static EffectivePeriod Between(LocalDate from, LocalDate? to)
    {
        if (to is { } end && end < from)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.WorkingHoursInvalid,
                $"An effective period must not end before it starts; got {from} to {end}.");
        }

        return new EffectivePeriod(from, to);
    }

    /// <summary>
    /// Whether this period is in force on <paramref name="date"/>. An open end never expires.
    /// </summary>
    /// <remarks>
    /// Added by <c>availability-read</c>, which is where the effective-date dimension stops being
    /// stored data and starts deciding answers (design F3). The half that is easy to get wrong is
    /// the open end: a null <c>To</c> means "until further notice", not "expired".
    /// </remarks>
    public bool Covers(LocalDate date) => From <= date && (To is null || date <= To);

    /// <summary>Whether two periods share any day. Open ends extend forever.</summary>
    public bool Overlaps(EffectivePeriod other)
    {
        var thisEndsBeforeOtherStarts = To is { } end && end < other.From;
        var otherEndsBeforeThisStarts = other.To is { } otherEnd && otherEnd < From;

        return !thisEndsBeforeOtherStarts && !otherEndsBeforeThisStarts;
    }

    public override string ToString() => To is { } end ? $"{From}..{end}" : $"{From}..";
}

/// <summary>
/// A professional's recurring availability on one weekday (02-domain-model.md §2).
/// </summary>
/// <remarks>
/// Wall-clock throughout — no instant appears anywhere in this type. Change 4's solver is what
/// converts a segment against a concrete date, using the configured clinic timezone; recording
/// one converts nothing (design E3). The reason is not caution: "every Monday 09:00" is a rule
/// rather than an event, and under daylight saving it yields different UTC offsets across the
/// year, so there is no single instant to store.
/// </remarks>
public sealed class WorkingHoursTemplate
{
    /// <summary>EF materialization only.</summary>
    private WorkingHoursTemplate()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProfessionalId { get; private set; }

    public IsoDayOfWeek DayOfWeek { get; private set; }

    public LocalTime StartTime { get; private set; }

    public LocalTime EndTime { get; private set; }

    public LocalDate EffectiveFrom { get; private set; }

    public LocalDate? EffectiveTo { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    public WorkingHoursSpan Span => WorkingHoursSpan.Between(StartTime, EndTime);

    public EffectivePeriod Period => EffectivePeriod.Between(EffectiveFrom, EffectiveTo);

    /// <summary>
    /// Defines a segment, refusing an impossible span and refusing a conflict with what is
    /// already stored.
    /// </summary>
    /// <param name="existing">
    /// The professional's other ACTIVE segments. The caller loads them; this factory decides
    /// what conflicts. Passing retired segments would make a retired schedule block a new one —
    /// the predicate-on-the-dependent mistake 3a guarded against.
    /// </param>
    /// <exception cref="CatalogRuleViolationException">The span is invalid, or it conflicts.</exception>
    public static WorkingHoursTemplate Define(
        Guid professionalId,
        IsoDayOfWeek dayOfWeek,
        LocalTime startTime,
        LocalTime endTime,
        LocalDate effectiveFrom,
        LocalDate? effectiveTo,
        IEnumerable<WorkingHoursTemplate> existing,
        DateTimeOffset createdAtUtc)
    {
        if (professionalId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A working-hour segment requires a professional.");
        }

        if (dayOfWeek == IsoDayOfWeek.None)
        {
            throw new DomainRuleViolationException("A working-hour segment requires a weekday.");
        }

        // Validity before conflict: an impossible segment should be reported as impossible even
        // if it also happens to overlap something.
        var span = WorkingHoursSpan.Between(startTime, endTime);
        var period = EffectivePeriod.Between(effectiveFrom, effectiveTo);

        EnsureNoConflict(professionalId: professionalId, dayOfWeek, span, period, existing, excluding: null);

        return new WorkingHoursTemplate
        {
            Id = Guid.NewGuid(),
            ProfessionalId = professionalId,
            DayOfWeek = dayOfWeek,
            StartTime = startTime,
            EndTime = endTime,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// The conflict rule, in the one place it can be got wrong.
    /// </summary>
    /// <remarks>
    /// A conflict requires <b>three</b> things: the same weekday, overlapping effective
    /// periods, AND overlapping times of day (design E5). Dropping the third refuses the most
    /// common real schedule — a morning block and an afternoon block on the same day. Dropping
    /// the second refuses a legitimate schedule change that takes effect later in the year.
    ///
    /// The naive one-dimensional version passes every obvious test, which is exactly why the
    /// unit tests include the two cases that must be <em>allowed</em>.
    /// </remarks>
    /// <exception cref="CatalogRuleViolationException">A stored segment conflicts.</exception>
    public static void EnsureNoConflict(
        Guid professionalId,
        IsoDayOfWeek dayOfWeek,
        WorkingHoursSpan span,
        EffectivePeriod period,
        IEnumerable<WorkingHoursTemplate> existing,
        Guid? excluding)
    {
        foreach (var other in existing)
        {
            if (other.ProfessionalId != professionalId
                || other.DayOfWeek != dayOfWeek
                || !other.IsActive
                || other.Id == excluding)
            {
                continue;
            }

            if (other.Period.Overlaps(period) && other.Span.Overlaps(span))
            {
                throw new CatalogRuleViolationException(
                    CatalogRefusal.WorkingHoursOverlap,
                    $"{dayOfWeek} {span} overlaps an existing segment {other.Span} "
                    + $"effective {other.Period}.");
            }
        }
    }

    /// <summary>Changes a segment's times or period, re-checking the same rules.</summary>
    public void Adjust(
        LocalTime startTime,
        LocalTime endTime,
        LocalDate effectiveFrom,
        LocalDate? effectiveTo,
        IEnumerable<WorkingHoursTemplate> existing)
    {
        var span = WorkingHoursSpan.Between(startTime, endTime);
        var period = EffectivePeriod.Between(effectiveFrom, effectiveTo);

        // Excluding itself: a segment must not be found to conflict with its own stored row.
        EnsureNoConflict(ProfessionalId, DayOfWeek, span, period, existing, excluding: Id);

        StartTime = startTime;
        EndTime = endTime;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
    }

    /// <summary>Retires the segment (I10). A retired segment stops blocking new ones.</summary>
    public void Retire(DateTimeOffset retiredAtUtc) => DeactivatedAtUtc ??= retiredAtUtc;

    public void Restore() => DeactivatedAtUtc = null;
}

/// <summary>
/// A one-off override of a professional's recurring hours on a single date
/// (02-domain-model.md §2).
/// </summary>
/// <remarks>
/// Per-professional only (design E4). A clinic-wide closure is entered once per professional,
/// which is genuinely tedious for a large clinic and is the recorded trade-off: a shared clinic
/// calendar is a new first-class concept, needing its own screen and its own precedence rules
/// against individual exceptions, and nobody has budgeted it.
///
/// Two shapes in one entity: unavailable all day (no span), or working different hours (a
/// span). A third table for the second case would double the reads change 4 makes for no gain.
/// </remarks>
public sealed class WorkingHoursException
{
    /// <summary>EF materialization only.</summary>
    private WorkingHoursException()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProfessionalId { get; private set; }

    public LocalDate Date { get; private set; }

    /// <summary>Null when the professional is unavailable for the whole day.</summary>
    public LocalTime? StartTime { get; private set; }

    public LocalTime? EndTime { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    /// <summary>True when this date carries no working hours at all.</summary>
    public bool IsUnavailableAllDay => StartTime is null;

    /// <summary>The replacement hours, when there are any.</summary>
    public WorkingHoursSpan? Span =>
        StartTime is { } start && EndTime is { } end
            ? WorkingHoursSpan.Between(start, end)
            : null;

    /// <summary>The professional is not available at all on this date.</summary>
    public static WorkingHoursException Unavailable(
        Guid professionalId,
        LocalDate date,
        DateTimeOffset createdAtUtc) =>
        Create(professionalId, date, span: null, createdAtUtc);

    /// <summary>The professional works these hours instead of their recurring pattern.</summary>
    /// <remarks>
    /// The same validity rule as a recurring segment governs the span — the spec says so
    /// explicitly, because an exception is the likelier place for a hurried 22:00–02:00 entry.
    /// </remarks>
    public static WorkingHoursException DifferentHours(
        Guid professionalId,
        LocalDate date,
        LocalTime startTime,
        LocalTime endTime,
        DateTimeOffset createdAtUtc) =>
        Create(professionalId, date, WorkingHoursSpan.Between(startTime, endTime), createdAtUtc);

    private static WorkingHoursException Create(
        Guid professionalId,
        LocalDate date,
        WorkingHoursSpan? span,
        DateTimeOffset createdAtUtc)
    {
        if (professionalId == Guid.Empty)
        {
            throw new DomainRuleViolationException("An exception requires a professional.");
        }

        return new WorkingHoursException
        {
            Id = Guid.NewGuid(),
            ProfessionalId = professionalId,
            Date = date,
            StartTime = span?.Start,
            EndTime = span?.End,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// One active exception per professional per date, so a date never has two conflicting
    /// answers.
    /// </summary>
    /// <exception cref="CatalogRuleViolationException">An active exception already covers this date.</exception>
    public static void EnsureNoneFor(
        Guid professionalId,
        LocalDate date,
        bool activeExceptionAlreadyExists)
    {
        if (activeExceptionAlreadyExists)
        {
            throw new CatalogRuleViolationException(
                CatalogRefusal.WorkingHoursOverlap,
                $"An exception already covers {date} for this professional.");
        }
    }

    public void Retire(DateTimeOffset retiredAtUtc) => DeactivatedAtUtc ??= retiredAtUtc;

    public void Restore() => DeactivatedAtUtc = null;
}
