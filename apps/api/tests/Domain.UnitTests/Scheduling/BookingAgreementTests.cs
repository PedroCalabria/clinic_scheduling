using Clinic.Domain.Configuration;
using Clinic.Domain.Scheduling;
using NodaTime;

namespace Clinic.Domain.UnitTests.Scheduling;

/// <summary>
/// That the read and the write cannot disagree, and that each refusal names its own cause
/// (design B1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The agreement property is the protection; the named-cause tests are the documentation.</b>
/// A booking path built as its own list of validations would need a test per rule to stay in step
/// with the read, and a test suite can only cover the cases somebody thought of — the drift would
/// land in the case nobody did. So <c>Solve</c> and <c>Explain</c> share one walk, and what is
/// asserted here is the equivalence itself, in both directions, over a grid of configurations.
/// </para>
/// <para>
/// Why both directions. "Everything offered is bookable" alone would be satisfied by an
/// <c>Explain</c> that admitted everything; "everything bookable is offered" alone by one that
/// admitted nothing. Only the pair pins the behaviour.
/// </para>
/// </remarks>
public sealed class BookingAgreementTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid Pro = Guid.NewGuid();
    private static readonly Guid VisitType = Guid.NewGuid();
    private static readonly Guid RoomA = Guid.NewGuid();
    private static readonly Guid RoomB = Guid.NewGuid();

    private static readonly DateTimeZone SaoPaulo = DateTimeZoneProviders.Tzdb["America/Sao_Paulo"];

    /// <summary>Observes daylight saving, so the agreement is checked where conversion can fail.</summary>
    private static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];

    private static readonly LocalDate Monday = new(2026, 8, 24);
    private static readonly LocalDate Effective = new(2026, 1, 1);
    private static readonly Instant LongAgo = Instant.FromUtc(2026, 1, 1, 0, 0);

    // --- Fixtures --------------------------------------------------------------------

    private static WorkingHoursTemplate Segment(int fromHour, int toHour, IsoDayOfWeek day = IsoDayOfWeek.Monday) =>
        WorkingHoursTemplate.Define(
            Pro, day, new LocalTime(fromHour, 0), new LocalTime(toHour, 0),
            Effective, null, existing: [], Recorded);

    private static ResourceCandidate Room(
        Guid id,
        int bufferMinutes = 0,
        IReadOnlyList<BusyInterval>? busy = null) =>
        new(id, bufferMinutes, busy ?? []);

    private static Instant At(int hour, int minute = 0, DateTimeZone? zone = null, LocalDate? date = null) =>
        (zone ?? SaoPaulo).AtStrictly((date ?? Monday).At(new LocalTime(hour, minute))).ToInstant();

    private static BusyInterval Busy(int fromHour, int toHour, BusyCause cause) =>
        BusyInterval.Between(At(fromHour), At(toHour), cause);

    private static AvailabilityInputs Inputs(
        IReadOnlyList<WorkingHoursTemplate>? segments = null,
        IReadOnlyList<WorkingHoursException>? exceptions = null,
        IReadOnlyList<BusyInterval>? busy = null,
        IReadOnlyList<ResourceCandidate>? resources = null,
        int durationMinutes = 60,
        int stepMinutes = 60,
        int leadTimeMinutes = 0,
        int horizonDays = 3650,
        DateTimeZone? zone = null,
        LocalDate? from = null,
        LocalDate? to = null,
        Instant? now = null) =>
        new(
            VisitType,
            from ?? Monday,
            to ?? from ?? Monday,
            zone ?? SaoPaulo,
            now ?? LongAgo,
            resources ?? [Room(RoomA)],
            SchedulingParameters.Of(stepMinutes, leadTimeMinutes, horizonDays),
            [new ProfessionalSchedule(Pro, durationMinutes, segments ?? [Segment(9, 12)], exceptions ?? [], busy ?? [])]);

    // --- 5.4 The agreement property, both directions ---------------------------------

    /// <summary>
    /// A grid of genuinely different configurations, each exercising a different arm of the walk.
    /// </summary>
    /// <remarks>
    /// Hand-built rather than randomly generated, on purpose: a random generator would need its
    /// own seed discipline to be reproducible, and what makes this property meaningful is
    /// coverage of the *arms* — daylight saving, an exception, a block, an appointment, a buffer,
    /// exhausted rooms, an overlapping step — rather than volume.
    /// </remarks>
    public static TheoryData<string, AvailabilityInputs> Configurations() => new()
    {
        { "plain morning", Inputs() },
        { "overlapping step", Inputs(stepMinutes: 15, durationMinutes: 40) },
        { "split day", Inputs(segments: [Segment(9, 12), Segment(14, 18)]) },
        { "no hours at all", Inputs(segments: []) },
        {
            "day off",
            Inputs(exceptions: [WorkingHoursException.Unavailable(Pro, Monday, Recorded)])
        },
        {
            "different hours",
            Inputs(exceptions:
            [
                WorkingHoursException.DifferentHours(
                    Pro, Monday, new LocalTime(14, 0), new LocalTime(18, 0), Recorded),
            ])
        },
        { "one block", Inputs(busy: [Busy(10, 11, BusyCause.InternalBlock)]) },
        { "one appointment", Inputs(busy: [Busy(10, 11, BusyCause.Appointment)]) },
        {
            "a block and an appointment",
            Inputs(busy: [Busy(9, 10, BusyCause.InternalBlock), Busy(11, 12, BusyCause.Appointment)])
        },
        { "no rooms", Inputs(resources: []) },
        {
            "the only room is taken all morning",
            Inputs(resources: [Room(RoomA, busy: [Busy(9, 12, BusyCause.Appointment)])])
        },
        {
            "the first room is taken, the second is free",
            Inputs(resources: [Room(RoomA, busy: [Busy(9, 12, BusyCause.Appointment)]), Room(RoomB)])
        },
        {
            "a room with a turnaround buffer",
            Inputs(resources: [Room(RoomA, bufferMinutes: 15, busy: [Busy(9, 10, BusyCause.Appointment)])])
        },
        { "lead time bites", Inputs(now: At(9), leadTimeMinutes: 90) },
        { "horizon bites", Inputs(now: At(9) - Duration.FromDays(400), horizonDays: 30) },
        {
            "spring forward in a DST zone",
            Inputs(zone: NewYork, segments: [Segment(1, 6, IsoDayOfWeek.Sunday)],
                from: new LocalDate(2026, 3, 8), to: new LocalDate(2026, 3, 8),
                stepMinutes: 15, durationMinutes: 30)
        },
        {
            "fall back in a DST zone",
            Inputs(zone: NewYork, segments: [Segment(1, 6, IsoDayOfWeek.Sunday)],
                from: new LocalDate(2026, 11, 1), to: new LocalDate(2026, 11, 1),
                stepMinutes: 15, durationMinutes: 30)
        },
        { "a week-long window", Inputs(from: Monday, to: Monday.PlusDays(6)) },
    };

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Everything_the_read_offers_the_write_admits(string name, AvailabilityInputs inputs)
    {
        var slots = AvailabilitySolver.Solve(inputs);

        foreach (var slot in slots)
        {
            var verdict = AvailabilitySolver.Explain(inputs, slot.Start);

            Assert.True(
                verdict.IsOfferable,
                $"[{name}] {slot.Start} was offered but Explain refused it with {verdict.Refusal}.");

            // And it assigns the same room, so the read's explanation and the write's assignment
            // are the same decision rather than two decisions that happen to agree.
            Assert.Equal(slot.ResourceId, verdict.ResourceId);
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Everything_the_write_admits_the_read_offers(string name, AvailabilityInputs inputs)
    {
        var offered = AvailabilitySolver.Solve(inputs).Select(slot => slot.Start).ToHashSet();

        // Probe the whole window at the step's granularity, so off-grid and out-of-hours starts
        // are included — the cases where an over-permissive Explain would be caught.
        var windowStart = inputs.ClinicZone.AtStartOfDay(inputs.FromDate).ToInstant();
        var windowEnd = inputs.ClinicZone.AtStartOfDay(inputs.ToDate.PlusDays(1)).ToInstant();
        var probe = Duration.FromMinutes(5);

        var admitted = 0;

        for (var candidate = windowStart; candidate < windowEnd; candidate += probe)
        {
            if (!AvailabilitySolver.Explain(inputs, candidate).IsOfferable)
            {
                continue;
            }

            admitted++;

            Assert.True(
                offered.Contains(candidate),
                $"[{name}] Explain admitted {candidate}, which the read never offered.");
        }

        // Guards against the test passing because nothing was admitted anywhere. Configurations
        // that legitimately offer nothing are exercised by the other direction.
        if (offered.Count > 0)
        {
            Assert.True(admitted > 0, $"[{name}] the read offered slots but the probe admitted none.");
        }
    }

    // --- 5.5 Lead time and horizon move together on both paths -----------------------

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(150)]
    public void The_lead_time_moves_the_earliest_bookable_start_identically_on_both_paths(int leadMinutes)
    {
        var inputs = Inputs(stepMinutes: 30, durationMinutes: 30, now: At(9), leadTimeMinutes: leadMinutes);

        var earliestOffered = AvailabilitySolver.Solve(inputs).Select(slot => slot.Start).FirstOrDefault();
        var expected = At(9) + Duration.FromMinutes(leadMinutes);

        // The read withholds everything before now + lead time...
        Assert.True(earliestOffered >= expected);

        // ...and the write refuses exactly the same starts, naming the lead time rather than
        // reporting them as outside working hours.
        for (var candidate = At(9); candidate < expected; candidate += Duration.FromMinutes(30))
        {
            var verdict = AvailabilitySolver.Explain(inputs, candidate);

            Assert.False(verdict.IsOfferable);
            Assert.Equal(BookingRefusal.LeadTimeViolation, verdict.Refusal);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void The_horizon_moves_the_latest_bookable_start_identically_on_both_paths(int horizonDays)
    {
        var now = At(8);

        // Hours on every weekday, so each date in the window has grid starts to probe. With a
        // Monday-to-Sunday window, horizons of one, three and five days all fall INSIDE it —
        // which is what makes the assertion below about anything. A horizon past the window's end
        // would leave nothing beyond it and the test would assert only that the offered slots are
        // offered.
        var week = Enum.GetValues<IsoDayOfWeek>()
            .Where(day => day != IsoDayOfWeek.None)
            .Select(day => Segment(9, 12, day))
            .ToArray();

        var inputs = Inputs(
            segments: week,
            from: Monday,
            to: Monday.PlusDays(6),
            now: now,
            horizonDays: horizonDays);

        var latest = now + Duration.FromDays(horizonDays);
        var offered = AvailabilitySolver.Solve(inputs);

        Assert.All(offered, slot => Assert.True(slot.Start <= latest));

        // Probed at the grid starts the hours actually produce — 09:00, 10:00 and 11:00 on each
        // working date. Probing arbitrary instants would only ever report "outside working
        // hours", because Explain checks grid membership before it judges the time, so such a
        // probe would pass while asserting nothing about the horizon at all.
        var beyond = 0;

        foreach (var date in Enumerable.Range(0, 7).Select(Monday.PlusDays))
        {
            foreach (var hour in new[] { 9, 10, 11 })
            {
                var candidate = At(hour, date: date);
                var verdict = AvailabilitySolver.Explain(inputs, candidate);

                if (candidate > latest)
                {
                    // Refused as the horizon rather than as anything else — the drift that would
                    // otherwise let the read offer a day the write cannot accept.
                    Assert.Equal(BookingRefusal.HorizonExceeded, verdict.Refusal);
                    beyond++;
                }
                else
                {
                    Assert.True(verdict.IsOfferable, $"{candidate} is inside the horizon and should be bookable");
                }
            }
        }

        Assert.True(beyond > 0, "the window should extend past the horizon for this case to mean anything");
    }

    // --- 5.3 Each refusal names its own cause ----------------------------------------

    [Fact]
    public void A_start_the_professional_does_not_work_is_outside_working_hours()
    {
        var verdict = AvailabilitySolver.Explain(Inputs(), At(15));

        Assert.Equal(BookingRefusal.OutsideWorkingHours, verdict.Refusal);
    }

    [Fact]
    public void A_start_off_the_step_grid_is_outside_working_hours()
    {
        // Inside the hours, but never offered. One code with one remedy — pick a time the search
        // showed you — rather than a fourth code for a case the catalogue would then have to
        // explain.
        var verdict = AvailabilitySolver.Explain(Inputs(stepMinutes: 30), At(9, 17));

        Assert.Equal(BookingRefusal.OutsideWorkingHours, verdict.Refusal);
    }

    [Fact]
    public void A_start_whose_appointment_would_run_past_the_day_is_outside_working_hours()
    {
        // 11:00 with a two-hour visit in 09:00-12:00 hours: the start is inside, the visit is
        // not.
        var verdict = AvailabilitySolver.Explain(Inputs(durationMinutes: 120, stepMinutes: 60), At(11));

        Assert.Equal(BookingRefusal.OutsideWorkingHours, verdict.Refusal);
    }

    [Fact]
    public void A_start_a_day_off_removed_is_outside_working_hours()
    {
        var inputs = Inputs(exceptions: [WorkingHoursException.Unavailable(Pro, Monday, Recorded)]);

        Assert.Equal(BookingRefusal.OutsideWorkingHours, AvailabilitySolver.Explain(inputs, At(9)).Refusal);
    }

    [Fact]
    public void A_block_and_an_appointment_are_distinguished_though_they_subtract_identically()
    {
        var blocked = Inputs(busy: [Busy(10, 11, BusyCause.InternalBlock)]);
        var taken = Inputs(busy: [Busy(10, 11, BusyCause.Appointment)]);

        // The distinction change 4's F5 anticipated: one list, one subtraction, and the write
        // path is the one caller that has to name the cause.
        Assert.Equal(BookingRefusal.SlotBlocked, AvailabilitySolver.Explain(blocked, At(10)).Refusal);
        Assert.Equal(BookingRefusal.SlotTaken, AvailabilitySolver.Explain(taken, At(10)).Refusal);

        // And the subtraction itself does not care which it was — same slots removed, both times.
        Assert.Equal(
            AvailabilitySolver.Solve(blocked).Select(slot => slot.Start),
            AvailabilitySolver.Solve(taken).Select(slot => slot.Start));
    }

    [Fact]
    public void An_external_block_is_reported_as_blocked_until_change_7_gives_it_its_own_answer()
    {
        var inputs = Inputs(busy: [Busy(10, 11, BusyCause.ExternalBlock)]);

        // Correct for now: from a patient's side an externally-synced block means the same thing
        // as an internal one — the professional is unavailable, and nobody was faster.
        Assert.Equal(BookingRefusal.SlotBlocked, AvailabilitySolver.Explain(inputs, At(10)).Refusal);
    }

    [Fact]
    public void A_slot_with_every_room_occupied_is_refused_as_resource_unavailable()
    {
        var inputs = Inputs(resources: [Room(RoomA, busy: [Busy(9, 12, BusyCause.Appointment)])]);

        Assert.Equal(BookingRefusal.ResourceUnavailable, AvailabilitySolver.Explain(inputs, At(9)).Refusal);
    }

    [Fact]
    public void A_clinic_with_no_rooms_of_the_type_refuses_as_resource_unavailable()
    {
        // Rather than as outside working hours, which the professional's schedule would otherwise
        // suggest: the hours are fine, there is simply nowhere for the visit to happen.
        Assert.Equal(
            BookingRefusal.ResourceUnavailable,
            AvailabilitySolver.Explain(Inputs(resources: []), At(9)).Refusal);
    }

    [Fact]
    public void The_write_assigns_the_room_rather_than_leaving_the_caller_to_choose()
    {
        var inputs = Inputs(resources: [Room(RoomA, busy: [Busy(9, 12, BusyCause.Appointment)]), Room(RoomB)]);

        var verdict = AvailabilitySolver.Explain(inputs, At(9));

        // The fall-through, decided inside the same walk that judged the slot offerable. This is
        // domain-model F2 having exactly one implementation.
        Assert.Equal(RoomB, verdict.ResourceId);
    }

    // --- 5.6 The half-open boundary, on both paths -----------------------------------

    [Theory]
    [InlineData(BusyCause.InternalBlock)]
    [InlineData(BusyCause.Appointment)]
    public void A_slot_beginning_when_a_busy_period_ends_is_offered_and_admitted(BusyCause cause)
    {
        var inputs = Inputs(busy: [Busy(9, 10, cause)]);

        Assert.Contains(At(10), AvailabilitySolver.Solve(inputs).Select(slot => slot.Start));
        Assert.True(AvailabilitySolver.Explain(inputs, At(10)).IsOfferable);
    }

    [Theory]
    [InlineData(BusyCause.InternalBlock)]
    [InlineData(BusyCause.Appointment)]
    public void A_slot_ending_when_a_busy_period_begins_is_offered_and_admitted(BusyCause cause)
    {
        var inputs = Inputs(busy: [Busy(10, 11, cause)]);

        Assert.Contains(At(9), AvailabilitySolver.Solve(inputs).Select(slot => slot.Start));
        Assert.True(AvailabilitySolver.Explain(inputs, At(9)).IsOfferable);
    }

    // --- 5.7 The seam fill, at the domain level --------------------------------------

    [Fact]
    public void An_appointment_in_the_professionals_busy_list_removes_its_slots()
    {
        var inputs = Inputs(busy: [Busy(10, 11, BusyCause.Appointment)]);

        var starts = AvailabilitySolver.Solve(inputs).Select(slot => slot.Start).ToArray();

        Assert.DoesNotContain(At(10), starts);
        Assert.Contains(At(9), starts);
        Assert.Contains(At(11), starts);
    }

    [Fact]
    public void An_appointment_occupying_a_room_only_withholds_a_slot_when_no_other_room_is_free()
    {
        var occupied = Busy(10, 11, BusyCause.Appointment);

        // One room, taken: the slot goes. Two rooms, one taken: the slot survives on the other.
        // This is the asymmetry design B11 records — an appointment feeds TWO lists, a block one.
        var oneRoom = Inputs(resources: [Room(RoomA, busy: [occupied])]);
        var twoRooms = Inputs(resources: [Room(RoomA, busy: [occupied]), Room(RoomB)]);

        Assert.DoesNotContain(At(10), AvailabilitySolver.Solve(oneRoom).Select(slot => slot.Start));

        var fellThrough = AvailabilitySolver.Solve(twoRooms).Single(slot => slot.Start == At(10));

        Assert.Equal(RoomB, fellThrough.ResourceId);
    }

    [Fact]
    public void The_rooms_turnaround_applies_and_the_professionals_does_not()
    {
        var appointment = Busy(9, 10, BusyCause.Appointment);

        // The room is not free at 10:00 — it is being cleaned. Same interval, applied to the
        // professional, leaves 10:00 offerable: turnaround belongs to the room, not to the person
        // walking out of it. A plausible "fix" would break exactly one of these two.
        var roomBusy = Inputs(
            stepMinutes: 30,
            durationMinutes: 30,
            resources: [Room(RoomA, bufferMinutes: 15, busy: [appointment])]);

        var professionalBusy = Inputs(stepMinutes: 30, durationMinutes: 30, busy: [appointment]);

        Assert.False(AvailabilitySolver.Explain(roomBusy, At(10)).IsOfferable);
        Assert.True(AvailabilitySolver.Explain(roomBusy, At(10, 30)).IsOfferable);
        Assert.True(AvailabilitySolver.Explain(professionalBusy, At(10)).IsOfferable);
    }
}
