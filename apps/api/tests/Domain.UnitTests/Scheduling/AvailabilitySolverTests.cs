using Clinic.Domain.Configuration;
using Clinic.Domain.Scheduling;
using NodaTime;

namespace Clinic.Domain.UnitTests.Scheduling;

/// <summary>
/// The availability computation (design F1-F8).
/// </summary>
/// <remarks>
/// Unit tests, because the solver is a pure function of facts the caller supplies. The
/// integration tier proves the slice <em>gathers</em> those facts correctly — which professionals
/// are eligible, which blocks fall in the window — and that is the half that actually breaks.
/// </remarks>
public sealed class AvailabilitySolverTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid Pro = Guid.NewGuid();
    private static readonly Guid OtherPro = Guid.NewGuid();
    private static readonly Guid VisitType = Guid.NewGuid();

    /// <summary>No daylight saving since 2019 — the zone whose tests cannot fail (task 4.9).</summary>
    private static readonly DateTimeZone SaoPaulo = DateTimeZoneProviders.Tzdb["America/Sao_Paulo"];

    /// <summary>Observes daylight saving, which is the whole point (00-context.md §6).</summary>
    private static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];

    private static readonly LocalDate Monday = new(2026, 8, 24);
    private static readonly LocalDate NextMonday = new(2026, 8, 31);
    private static readonly LocalDate Effective = new(2026, 1, 1);

    /// <summary>Well before every date under test, so lead time and horizon stay out of the way.</summary>
    private static readonly Instant LongAgo = Instant.FromUtc(2026, 1, 1, 0, 0);

    // --- Fixtures --------------------------------------------------------------------

    private static WorkingHoursTemplate Segment(
        int fromHour,
        int toHour,
        IsoDayOfWeek day = IsoDayOfWeek.Monday,
        LocalDate? effectiveFrom = null,
        LocalDate? effectiveTo = null,
        Guid? professionalId = null) =>
        WorkingHoursTemplate.Define(
            professionalId ?? Pro,
            day,
            new LocalTime(fromHour, 0),
            new LocalTime(toHour, 0),
            effectiveFrom ?? Effective,
            effectiveTo,
            existing: [],
            Recorded);

    private static WorkingHoursException DayOff(LocalDate date, Guid? professionalId = null) =>
        WorkingHoursException.Unavailable(professionalId ?? Pro, date, Recorded);

    private static WorkingHoursException OtherHours(LocalDate date, int fromHour, int toHour) =>
        WorkingHoursException.DifferentHours(
            Pro, date, new LocalTime(fromHour, 0), new LocalTime(toHour, 0), Recorded);

    private static ProfessionalSchedule Schedule(
        IReadOnlyList<WorkingHoursTemplate> segments,
        IReadOnlyList<WorkingHoursException>? exceptions = null,
        IReadOnlyList<BusyInterval>? busy = null,
        int durationMinutes = 60,
        Guid? professionalId = null) =>
        new(professionalId ?? Pro, durationMinutes, segments, exceptions ?? [], busy ?? []);

    private static readonly Guid RoomA = Guid.NewGuid();
    private static readonly Guid RoomB = Guid.NewGuid();

    private static ResourceCandidate Room(
        Guid id,
        int bufferMinutes = 0,
        IReadOnlyList<BusyInterval>? busy = null) =>
        new(id, bufferMinutes, busy ?? []);

    private static AvailabilityInputs Inputs(
        IReadOnlyList<ProfessionalSchedule> professionals,
        LocalDate? from = null,
        LocalDate? to = null,
        int stepMinutes = 60,
        int leadTimeMinutes = 0,
        int horizonDays = 3650,
        IReadOnlyList<ResourceCandidate>? resources = null,
        DateTimeZone? zone = null,
        Instant? now = null) =>
        new(
            VisitType,
            from ?? Monday,
            to ?? (from ?? Monday),
            zone ?? SaoPaulo,
            now ?? LongAgo,
            resources ?? [Room(RoomA)],
            SchedulingParameters.Of(stepMinutes, leadTimeMinutes, horizonDays),
            professionals);

    /// <summary>What a slot's start reads as on the clinic's clock, for legible assertions.</summary>
    private static string LocalStart(AvailabilitySlot slot, DateTimeZone zone) =>
        slot.Start.InZone(zone).LocalDateTime.ToString("HH:mm", null);

    // --- 4.1 The effective-date dimension (F3) ---------------------------------------

    [Fact]
    public void A_segment_effective_only_later_does_not_apply_to_an_earlier_date()
    {
        var segments = new[] { Segment(9, 12, effectiveFrom: new LocalDate(2026, 9, 1)) };

        var slots = AvailabilitySolver.Solve(Inputs([Schedule(segments)]));

        Assert.Empty(slots);
    }

    [Fact]
    public void A_segment_whose_period_has_ended_does_not_apply_afterwards()
    {
        var segments = new[] { Segment(9, 12, effectiveTo: new LocalDate(2026, 8, 1)) };

        var slots = AvailabilitySolver.Solve(Inputs([Schedule(segments)]));

        Assert.Empty(slots);
    }

    [Fact]
    public void An_open_ended_period_applies_from_its_start_onward()
    {
        var slots = AvailabilitySolver.Solve(Inputs([Schedule([Segment(9, 12)])]));

        Assert.NotEmpty(slots);
    }

    [Fact]
    public void A_schedule_change_mid_window_is_honoured_on_both_sides()
    {
        // The case that made the effective-date dimension worth using rather than storing
        // (design F3): filtering on "currently effective" would answer both Mondays the same.
        var segments = new[]
        {
            Segment(9, 12, effectiveTo: Monday),
            Segment(14, 17, effectiveFrom: Monday.PlusDays(1)),
        };

        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule(segments)], from: Monday, to: NextMonday));

        var first = slots.Where(slot => slot.Start.InZone(SaoPaulo).Date == Monday).ToList();
        var second = slots.Where(slot => slot.Start.InZone(SaoPaulo).Date == NextMonday).ToList();

        Assert.All(first, slot => Assert.InRange(LocalStart(slot, SaoPaulo), "09:00", "11:00"));
        Assert.All(second, slot => Assert.InRange(LocalStart(slot, SaoPaulo), "14:00", "16:00"));
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
    }

    [Fact]
    public void A_split_day_contributes_both_segments_and_nothing_in_the_gap()
    {
        var segments = new[] { Segment(9, 12), Segment(13, 17) };

        var slots = AvailabilitySolver.Solve(Inputs([Schedule(segments)]));

        var starts = slots.Select(slot => LocalStart(slot, SaoPaulo)).ToList();

        Assert.Equal(["09:00", "10:00", "11:00", "13:00", "14:00", "15:00", "16:00"], starts);
        Assert.DoesNotContain("12:00", starts);
    }

    [Fact]
    public void A_retired_segment_contributes_nothing()
    {
        var retired = Segment(9, 12);
        retired.Retire(Recorded);

        var slots = AvailabilitySolver.Solve(Inputs([Schedule([retired])]));

        Assert.Empty(slots);
    }

    // --- 4.2 Exceptions replace the day (F4) -----------------------------------------

    [Fact]
    public void A_day_off_removes_the_date_entirely()
    {
        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12), Segment(13, 17)], [DayOff(Monday)])]));

        Assert.Empty(slots);
    }

    [Fact]
    public void Different_hours_replace_the_recurring_hours_rather_than_adding_to_them()
    {
        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12), Segment(13, 17)], [OtherHours(Monday, 14, 16)])]));

        // Not a union and not a subtraction: exactly what the exception says, instead of the
        // day's ordinary hours.
        Assert.Equal(["14:00", "15:00"], slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void An_exception_does_not_affect_neighbouring_dates()
    {
        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)], [DayOff(Monday)])], from: Monday, to: NextMonday));

        Assert.NotEmpty(slots);
        Assert.All(slots, slot => Assert.Equal(NextMonday, slot.Start.InZone(SaoPaulo).Date));
    }

    [Fact]
    public void An_exception_does_not_affect_another_professional()
    {
        var mine = Schedule([Segment(9, 12)], [DayOff(Monday)]);
        var theirs = Schedule(
            [Segment(9, 12, professionalId: OtherPro)],
            professionalId: OtherPro);

        var slots = AvailabilitySolver.Solve(Inputs([mine, theirs]));

        Assert.NotEmpty(slots);
        Assert.All(slots, slot => Assert.Equal(OtherPro, slot.ProfessionalId));
    }

    [Fact]
    public void A_retired_exception_restores_the_recurring_hours()
    {
        var exception = DayOff(Monday);
        exception.Retire(Recorded);

        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)], [exception])]));

        Assert.Equal(["09:00", "10:00", "11:00"], slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    // --- 4.3 Slicing and the start step (F8) -----------------------------------------

    [Fact]
    public void Starts_step_at_the_configured_interval_independently_of_the_duration()
    {
        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)], durationMinutes: 40)], stepMinutes: 15));

        // Overlapping candidates, which is correct for a read whose slots are not reservations.
        Assert.Equal(
            ["09:00", "09:15", "09:30", "09:45", "10:00", "10:15", "10:30", "10:45", "11:00", "11:15"],
            slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void A_slot_that_would_run_past_the_candidate_hours_is_not_offered()
    {
        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)], durationMinutes: 40)], stepMinutes: 15));

        // 11:30 + 40 minutes would end at 12:10, so it is withheld even though 11:30 is inside
        // the hours. Every slot ends inside them, not merely starts inside them.
        var closes = SaoPaulo.AtStrictly(Monday.At(new LocalTime(12, 0))).ToInstant();

        Assert.All(slots, slot => Assert.True(slot.End <= closes));
        Assert.DoesNotContain("11:30", slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void Each_professional_gets_their_own_duration()
    {
        var mine = Schedule([Segment(9, 12)], durationMinutes: 40);
        var theirs = Schedule(
            [Segment(9, 12, professionalId: OtherPro)],
            durationMinutes: 50,
            professionalId: OtherPro);

        var slots = AvailabilitySolver.Solve(Inputs([mine, theirs]));

        // The entire reason Decision C put duration on a junction, in one assertion.
        Assert.All(
            slots.Where(slot => slot.ProfessionalId == Pro),
            slot => Assert.Equal(Duration.FromMinutes(40), slot.End - slot.Start));

        Assert.All(
            slots.Where(slot => slot.ProfessionalId == OtherPro),
            slot => Assert.Equal(Duration.FromMinutes(50), slot.End - slot.Start));
    }

    // --- Lead time and horizon (F8) --------------------------------------------------

    [Fact]
    public void Slots_inside_the_minimum_lead_time_are_withheld()
    {
        // "Now" is 08:05 local on the day itself, with a two-hour lead time, so nothing before
        // 10:05 may be offered: 09:00 and 10:00 are too soon, 11:00 is not.
        var now = SaoPaulo.AtStrictly(Monday.At(new LocalTime(8, 5))).ToInstant();

        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)])], leadTimeMinutes: 120, now: now));

        Assert.Equal(["11:00"], slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void Dates_beyond_the_horizon_offer_nothing_while_nearer_ones_still_do()
    {
        // A horizon of eight days from the Sunday before: the first Monday is inside it, the
        // second is not.
        var now = SaoPaulo.AtStrictly(Monday.PlusDays(-1).At(LocalTime.Midnight)).ToInstant();

        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)])], from: Monday, to: NextMonday, horizonDays: 8, now: now));

        Assert.NotEmpty(slots);
        Assert.All(slots, slot => Assert.Equal(Monday, slot.Start.InZone(SaoPaulo).Date));
    }

    // --- 4.4 Subtracting the busy set (F5) -------------------------------------------

    /// <summary>
    /// A busy interval on the target Monday.
    /// </summary>
    /// <remarks>
    /// The cause defaults to an internal block because that was the only producer when these
    /// tests were written, and the subtraction is cause-agnostic (F5) so every assertion below
    /// holds for any value. The tests that care which cause it was — the ones distinguishing
    /// `slot_blocked` from `slot_taken` — pass it explicitly.
    /// </remarks>
    private static BusyInterval Busy(
        int fromHour,
        int toHour,
        int fromMinute = 0,
        int toMinute = 0,
        BusyCause cause = BusyCause.InternalBlock) =>
        BusyInterval.Between(
            SaoPaulo.AtStrictly(Monday.At(new LocalTime(fromHour, fromMinute))).ToInstant(),
            SaoPaulo.AtStrictly(Monday.At(new LocalTime(toHour, toMinute))).ToInstant(),
            cause);

    [Fact]
    public void A_busy_interval_removes_the_slots_it_covers_and_leaves_the_abutting_ones()
    {
        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)], busy: [Busy(10, 11)])]));

        // 09:00-10:00 ends exactly as the block begins and 11:00-12:00 begins exactly as it
        // ends. Touching is not overlapping, so both survive — the half a naive implementation
        // gets wrong, and the most ordinary schedule there is.
        Assert.Equal(["09:00", "11:00"], slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void Overlapping_busy_intervals_subtract_their_union()
    {
        var split = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)], busy: [Busy(10, 10, toMinute: 30), Busy(10, 11, fromMinute: 15)])]));

        var whole = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)], busy: [Busy(10, 11)])]));

        Assert.Equal(
            whole.Select(slot => LocalStart(slot, SaoPaulo)),
            split.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void A_busy_interval_outside_the_working_hours_changes_nothing()
    {
        var withBlock = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)], busy: [Busy(20, 22)])]));

        var without = AvailabilitySolver.Solve(Inputs([Schedule([Segment(9, 12)])]));

        Assert.Equal(
            without.Select(slot => LocalStart(slot, SaoPaulo)),
            withBlock.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void A_busy_interval_reduces_only_its_own_professional()
    {
        var mine = Schedule([Segment(9, 12)], busy: [Busy(10, 11)]);
        var theirs = Schedule(
            [Segment(9, 12, professionalId: OtherPro)],
            professionalId: OtherPro);

        var slots = AvailabilitySolver.Solve(Inputs([mine, theirs]));

        Assert.Equal(2, slots.Count(slot => slot.ProfessionalId == Pro));
        Assert.Equal(3, slots.Count(slot => slot.ProfessionalId == OtherPro));
    }

    // --- 4.5 Resource-type feasibility (F6) ------------------------------------------

    [Fact]
    public void No_room_of_the_required_type_means_no_slots_however_free_the_professional_is()
    {
        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 17)])], resources: []));

        Assert.Empty(slots);
    }

    [Fact]
    public void A_slot_identifies_its_professional_appointment_type_and_room()
    {
        var slots = AvailabilitySolver.Solve(Inputs([Schedule([Segment(9, 12)])]));

        Assert.NotEmpty(slots);
        Assert.All(slots, slot =>
        {
            Assert.Equal(Pro, slot.ProfessionalId);
            Assert.Equal(VisitType, slot.AppointmentTypeId);

            // The (professional, resource) pair 02-domain-model.md §4 describes, completed.
            Assert.Equal(RoomA, slot.ResourceId);
        });
    }

    [Fact]
    public void The_first_free_room_in_the_callers_order_is_the_one_chosen()
    {
        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)])], resources: [Room(RoomA), Room(RoomB)]));

        // Deterministic rather than arbitrary: the caller's ordering IS the assignment policy, so
        // a second free room is never silently preferred.
        Assert.All(slots, slot => Assert.Equal(RoomA, slot.ResourceId));
    }

    [Fact]
    public void A_slot_falls_through_to_the_next_room_when_the_first_is_taken()
    {
        var slots = AvailabilitySolver.Solve(Inputs(
            [Schedule([Segment(9, 12)])],
            resources: [Room(RoomA, busy: [Busy(10, 11)]), Room(RoomB)]));

        // Still three slots: the clinic has somewhere to put the 10:00 visit. This is the whole
        // reason the resource check is per slot rather than per answer.
        Assert.Equal(["09:00", "10:00", "11:00"], slots.Select(slot => LocalStart(slot, SaoPaulo)));

        Assert.Equal(RoomB, slots.Single(slot => LocalStart(slot, SaoPaulo) == "10:00").ResourceId);
        Assert.Equal(RoomA, slots.Single(slot => LocalStart(slot, SaoPaulo) == "09:00").ResourceId);
    }

    [Fact]
    public void A_slot_is_withheld_when_every_room_is_taken()
    {
        var slots = AvailabilitySolver.Solve(Inputs(
            [Schedule([Segment(9, 12)])],
            resources: [Room(RoomA, busy: [Busy(10, 11)]), Room(RoomB, busy: [Busy(10, 11)])]));

        Assert.Equal(["09:00", "11:00"], slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void A_rooms_turnaround_buffer_is_kept_out_of_the_bookable_window()
    {
        // A room occupied 09:00-10:00 with a 15-minute turnaround is not free at 10:00; it is
        // free at 11:00 (02-domain-model.md, decision F1).
        var slots = AvailabilitySolver.Solve(Inputs(
            [Schedule([Segment(9, 13)])],
            resources: [Room(RoomA, bufferMinutes: 15, busy: [Busy(9, 10)])]));

        Assert.Equal(["11:00", "12:00"], slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void A_buffer_of_zero_leaves_an_abutting_slot_offerable()
    {
        // Zero is a legitimate buffer — a room needing no turnaround — and then the ordinary
        // half-open rule applies: 10:00 abuts the occupied interval rather than overlapping it.
        var slots = AvailabilitySolver.Solve(Inputs(
            [Schedule([Segment(9, 13)])],
            resources: [Room(RoomA, bufferMinutes: 0, busy: [Busy(9, 10)])]));

        Assert.Equal(["10:00", "11:00", "12:00"], slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    [Fact]
    public void A_professionals_busy_interval_carries_no_buffer()
    {
        // The asymmetry worth asserting: turnaround belongs to the room, not to the person
        // walking out of it. A doctor free at 10:00 is free at 10:00.
        var slots = AvailabilitySolver.Solve(Inputs(
            [Schedule([Segment(9, 13)], busy: [Busy(9, 10)])],
            resources: [Room(RoomA, bufferMinutes: 15)]));

        Assert.Equal(["10:00", "11:00", "12:00"], slots.Select(slot => LocalStart(slot, SaoPaulo)));
    }

    // --- 4.6 Any professional, one code path (F7) ------------------------------------

    [Fact]
    public void Any_professional_mode_unions_the_schedules_it_is_given()
    {
        var mine = Schedule([Segment(9, 11)]);
        var theirs = Schedule([Segment(9, 11, professionalId: OtherPro)], professionalId: OtherPro);

        var slots = AvailabilitySolver.Solve(Inputs([mine, theirs]));

        // The same time from two professionals is offered twice and stays distinguishable —
        // which is what makes "any professional" a real answer rather than a coin flip.
        Assert.Equal(4, slots.Count);
        Assert.Equal(2, slots.Count(slot => slot.ProfessionalId == Pro));
        Assert.Equal(2, slots.Count(slot => slot.ProfessionalId == OtherPro));

        var atNine = slots.Where(slot => LocalStart(slot, SaoPaulo) == "09:00").ToList();

        Assert.Equal(2, atNine.Count);
        Assert.Equal(2, atNine.Select(slot => slot.ProfessionalId).Distinct().Count());
    }

    [Fact]
    public void The_specific_professional_query_is_the_same_path_over_one_schedule()
    {
        var mine = Schedule([Segment(9, 11)]);
        var theirs = Schedule([Segment(9, 11, professionalId: OtherPro)], professionalId: OtherPro);

        var union = AvailabilitySolver.Solve(Inputs([mine, theirs]));
        var single = AvailabilitySolver.Solve(Inputs([mine]));

        // There is no second solver: the specific query is the union over a one-element set, so
        // its answer must be exactly the subset the union already contains. Whether a
        // professional is ELIGIBLE is the loading step's decision, covered in the integration
        // tier — the solver answers only for the schedules it was handed.
        Assert.Equal(
            union.Where(slot => slot.ProfessionalId == Pro).ToList(),
            single);
    }

    [Fact]
    public void Slots_are_ordered_by_when_they_start()
    {
        var mine = Schedule([Segment(13, 15)]);
        var theirs = Schedule([Segment(9, 11, professionalId: OtherPro)], professionalId: OtherPro);

        var slots = AvailabilitySolver.Solve(Inputs([mine, theirs]));

        Assert.Equal(slots.OrderBy(slot => slot.Start).Select(slot => slot.Start), slots.Select(slot => slot.Start));
    }

    // --- 4.7 Daylight saving, in a zone where it can fail (00-context.md §6) ---------

    /// <summary>2026's spring-forward Sunday in New York: 02:00 becomes 03:00.</summary>
    private static readonly LocalDate SpringForward = new(2026, 3, 8);

    /// <summary>2026's fall-back Sunday in New York: 02:00 becomes 01:00.</summary>
    private static readonly LocalDate FallBack = new(2026, 11, 1);

    private static IReadOnlyList<AvailabilitySlot> SolveOn(
        LocalDate date,
        int fromHour,
        int toHour,
        DateTimeZone zone,
        int stepMinutes = 60,
        int durationMinutes = 60)
    {
        var segment = Segment(fromHour, toHour, day: date.DayOfWeek);

        return AvailabilitySolver.Solve(Inputs(
            [Schedule([segment], durationMinutes: durationMinutes)],
            from: date,
            to: date,
            stepMinutes: stepMinutes,
            zone: zone));
    }

    [Fact]
    public void A_date_that_loses_an_hour_yields_a_shorter_interval()
    {
        // 01:00 to 05:00 spans the transition, so it is three real hours rather than four.
        // Offering four would offer an hour that does not exist.
        var slots = SolveOn(SpringForward, 1, 5, NewYork);

        Assert.Equal(3, slots.Count);
        Assert.Equal(NewYork.AtStrictly(SpringForward.At(new LocalTime(1, 0))).ToInstant(), slots[0].Start);
        Assert.Equal(Duration.FromHours(3), slots[^1].End - slots[0].Start);
    }

    [Fact]
    public void A_start_time_that_does_not_exist_resolves_forward_past_the_gap()
    {
        // 02:00 is skipped entirely on this date. Leniently resolved, the day opens at the first
        // instant after the gap — which is 03:00 local, the same instant 02:00 would have been.
        var slots = SolveOn(SpringForward, 2, 6, NewYork);

        var afterTheGap = NewYork.AtStrictly(SpringForward.At(new LocalTime(3, 0))).ToInstant();

        Assert.NotEmpty(slots);
        Assert.Equal(afterTheGap, slots[0].Start);

        // And it does not fail: a legal clock change must not take availability down for a day,
        // which is why the lenient resolver is chosen over the strict one.
        Assert.Equal(3, slots.Count);
    }

    [Fact]
    public void A_date_that_gains_an_hour_yields_a_longer_interval()
    {
        // 00:00 to 05:00 spans the repeated hour, so it is six real hours rather than five. This
        // is the half a wall-clock implementation loses entirely: it would offer five.
        var slots = SolveOn(FallBack, 0, 5, NewYork);

        Assert.Equal(6, slots.Count);
        Assert.Equal(Duration.FromHours(6), slots[^1].End - slots[0].Start);
    }

    [Fact]
    public void An_ambiguous_start_time_takes_the_earlier_occurrence()
    {
        // 01:00 happens twice on this date. The earlier one is 05:00 UTC; taking the later would
        // silently shorten the clinic's day by an hour.
        var slots = SolveOn(FallBack, 1, 5, NewYork);

        Assert.Equal(Instant.FromUtc(2026, 11, 1, 5, 0), slots[0].Start);
        Assert.Equal(5, slots.Count);
    }

    // --- 4.8 The assertions that catch slicing-before-conversion (F2) ----------------

    [Fact]
    public void Spring_forward_produces_no_duplicate_slot_starts()
    {
        // THE test for design F2. Slicing the wall clock and converting each start maps several
        // distinct local times inside the gap onto the same instant — 02:00, 02:15, 02:30 and
        // 03:00, 03:15, 03:30 collapse in pairs. Slicing the converted interval cannot.
        var slots = SolveOn(SpringForward, 1, 6, NewYork, stepMinutes: 15, durationMinutes: 30);

        Assert.NotEmpty(slots);
        Assert.Equal(slots.Count, slots.Select(slot => slot.Start).Distinct().Count());
    }

    [Fact]
    public void Fall_back_offers_the_repeated_hour_rather_than_losing_it()
    {
        // The other half of F2, and the one duplicate-detection cannot catch: a wall-clock
        // implementation produces distinct instants here, just an hour's worth too few.
        var slots = SolveOn(FallBack, 0, 5, NewYork, stepMinutes: 15, durationMinutes: 30);

        var wallClockHours = 5;
        var realHours = 6;

        // Starts every 15 minutes across the real interval, minus the tail a 30-minute slot
        // cannot fill.
        Assert.Equal((realHours * 60 - 30) / 15 + 1, slots.Count);
        Assert.NotEqual((wallClockHours * 60 - 30) / 15 + 1, slots.Count);
    }

    [Fact]
    public void No_slot_starts_inside_the_skipped_local_interval()
    {
        // A direct statement of the requirement. It holds by construction once slicing happens
        // after conversion — a gap contains no instants at all — so unlike the two tests above
        // this one has no teeth on its own. It is here because the requirement is worth saying
        // out loud, not because it is the guard.
        var slots = SolveOn(SpringForward, 1, 6, NewYork, stepMinutes: 15, durationMinutes: 30);

        Assert.All(slots, slot =>
        {
            var local = slot.Start.InZone(NewYork).LocalDateTime;

            Assert.False(local.Hour == 2 && local.Date == SpringForward);
        });
    }

    // --- 4.9 The same fixtures where they cannot fail --------------------------------

    [Theory]
    [InlineData(3, 8)]
    [InlineData(11, 1)]
    public void The_same_dates_are_unremarkable_in_a_zone_without_daylight_saving(int month, int day)
    {
        // Brazil abolished daylight saving in 2019, so America/Sao_Paulo is UTC-3 all year and
        // these dates hold no transition. Four wall-clock hours are four real hours.
        //
        // This test asserts that it passes TRIVIALLY, which is the point of 00-context.md §6:
        // the project's own configured zone cannot catch a broken conversion, so a reader who
        // finds only São Paulo fixtures should not mistake them for coverage.
        var date = new LocalDate(2026, month, day);

        var slots = SolveOn(date, 1, 5, SaoPaulo);

        Assert.Equal(4, slots.Count);
        Assert.Equal(Duration.FromHours(4), slots[^1].End - slots[0].Start);
    }

    // --- Window guards ---------------------------------------------------------------

    [Fact]
    public void A_window_whose_end_precedes_its_start_yields_nothing()
    {
        var slots = AvailabilitySolver.Solve(
            Inputs([Schedule([Segment(9, 12)])], from: NextMonday, to: Monday));

        Assert.Empty(slots);
    }

    [Fact]
    public void No_eligible_professional_yields_nothing_rather_than_failing()
    {
        var slots = AvailabilitySolver.Solve(Inputs([]));

        Assert.Empty(slots);
    }
}
