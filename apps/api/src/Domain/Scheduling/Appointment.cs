using NodaTime;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// Where an appointment came from (02-domain-model.md §2).
/// </summary>
/// <remarks>
/// Two values, one of them used. <c>booking-core</c> is the patient's own booking;
/// <c>booking-lifecycle</c> adds the front desk acting on a patient's behalf (S5). The same
/// argument as <see cref="TimeBlockSource"/>: the second value is designed rather than
/// speculated, and the alternative is renaming a column in 5b.
/// </remarks>
public enum AppointmentSource
{
    /// <summary>The patient booked it themselves, through P2/P3.</summary>
    SelfService = 1,

    /// <summary>Reception booked it for them, by phone or at the desk (5b).</summary>
    FrontDesk = 2,
}

/// <summary>
/// The appointment state machine (02-domain-model.md §3, invariant I9).
/// </summary>
/// <remarks>
/// <para>
/// <b>All five values, though <c>booking-core</c> can only reach the first.</b> I9 is a
/// statement about a shape — "transitions only per the state machine" — and an enum holding two
/// values would misdescribe the shape while making <c>booking-lifecycle</c> a column rewrite
/// instead of new code (design B10).
/// </para>
/// <para>
/// The load-bearing consequence of declaring them now is that the database's exclusion
/// constraints are already written as <c>WHERE status = 'Scheduled'</c>. So the day 5b writes
/// <c>Cancelled</c>, the slot frees itself with no migration and no constraint change — and that
/// behaviour is provable today by writing a terminal row directly, which is exactly what one of
/// this change's integration tests does.
/// </para>
/// </remarks>
public enum AppointmentStatus
{
    /// <summary>The only live state. Occupies its professional, its room, and its patient.</summary>
    Scheduled = 1,

    /// <summary>Attended. Set by the front desk in 5b.</summary>
    Completed = 2,

    /// <summary>The patient did not attend. Feeds SC-4's metric; set in 5b.</summary>
    NoShow = 3,

    /// <summary>Called off by the patient or the desk (5b). Frees the time it held.</summary>
    Cancelled = 4,

    /// <summary>
    /// Replaced by a new linked appointment (5b). Terminal, and frees the time it held.
    /// </summary>
    Rescheduled = 5,
}

/// <summary>
/// Everything the aggregate needs to be told in order to refuse being built wrongly.
/// </summary>
/// <remarks>
/// <para>
/// A record rather than a dozen parameters, and every field is a <em>fact the caller has
/// already established</em> rather than something the aggregate could look up. That is the same
/// bargain <see cref="AvailabilityInputs"/> struck (design F1): the core stays pure and
/// unit-testable, and the price is that the loading step has to be right.
/// </para>
/// <para>
/// The two pairs that look redundant are the interesting part.
/// <see cref="ProfessionalHoldsDurationForType"/> is I2's gate as a boolean because the gate is
/// the *existence* of a duration, and passing the duration alone would leave the aggregate
/// unable to distinguish "40 minutes" from "not qualified". <see cref="ResourceTypeId"/> and
/// <see cref="RequiredResourceTypeId"/> are both present because I3 is a comparison, and an
/// aggregate handed only the conclusion could not enforce it.
/// </para>
/// </remarks>
/// <param name="DurationMinutes">
/// What this appointment type takes <em>this</em> professional, right now. Baked into the range
/// below and never consulted again — invariant I1.
/// </param>
/// <param name="ProfessionalHoldsDurationForType">
/// Whether an active <c>ProfessionalAppointmentType</c> exists. False refuses with
/// <see cref="BookingRefusal.SpecialtyMismatch"/>.
/// </param>
public sealed record AppointmentBooking(
    Guid PatientId,
    Guid ProfessionalId,
    Guid ResourceId,
    Guid AppointmentTypeId,
    Instant StartsAt,
    int DurationMinutes,
    bool ProfessionalHoldsDurationForType,
    Guid ResourceTypeId,
    Guid RequiredResourceTypeId,
    AppointmentSource Source);

/// <summary>
/// The scheduling aggregate root (02-domain-model.md §2, §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this type is for, given that the database also enforces overlap and the slice also
/// explains refusals.</b> The three layers are not redundant (design B2). The exclusion
/// constraints make the guarantee hold under concurrency, whatever the code does. The solver's
/// <see cref="AvailabilitySolver.Explain"/> names a cause so a refusal is a sentence a patient
/// understands. This aggregate makes an invalid appointment <em>impossible to construct</em> —
/// which is what stops <c>booking-lifecycle</c>'s second write path from reintroducing a bug
/// this change fixes, and it is the layer the unit tests can reach without a database.
/// </para>
/// <para>
/// <b>No soft-delete column</b>, deviating from the ERD in 02 §9 and argued in design B3: an
/// appointment's history is reconstructible from its status, and <c>Cancelled</c> versus
/// <c>Rescheduled</c> versus <c>NoShow</c> are richer facts than a deleted flag. A second, weaker
/// way for a row to stop counting would have to be honoured by the exclusion predicate too, and
/// two sources of truth for "is this row live" is how a constraint becomes decorative.
/// </para>
/// <para>
/// <b><c>rescheduledFromId</c> exists as of <c>booking-lifecycle</c>; <c>externalEventId</c> still
/// does not.</b> The rule 5a stated has not changed — no column for a producer that does not
/// exist — it is that the producer now exists. <see cref="RescheduleTo"/> writes the link;
/// nothing writes an external event id. <b>Still true after change 6a</b>, which establishes the
/// calendar CONNECTION and propagates nothing: the column arrives with 6b's outbox, the producer
/// that would fill it.
/// </para>
/// <para>
/// <b>Two of the four terminal transitions are reachable.</b> <see cref="Cancel"/> and
/// <see cref="RescheduleTo"/> are here; there is no <c>Complete()</c> and no <c>MarkNoShow()</c>,
/// so <see cref="AppointmentStatus.Completed"/> and <see cref="AppointmentStatus.NoShow"/> remain
/// unreachable <em>by inspection</em> rather than by reading a transition table. They are
/// front-desk observations about a visit that has already happened.
/// <b>Correction, made in change 6a:</b> this comment used to say <c>booking-desk</c> would
/// record them. It shipped without doing so, which left the two states owned by nobody — they
/// now belong to the <c>visit-outcome</c> change (<c>05-openspec-workflow.md</c> §3), which is
/// where <c>Complete()</c> and <c>MarkNoShow()</c> arrive.
/// </para>
/// </remarks>
public sealed class Appointment
{
    /// <summary>EF materialization only.</summary>
    private Appointment()
    {
    }

    public Guid Id { get; private set; }

    public Guid PatientId { get; private set; }

    public Guid ProfessionalId { get; private set; }

    /// <summary>
    /// The room or machine serving this appointment, chosen by the server (domain-model F2).
    /// </summary>
    /// <remarks>
    /// Never a value a caller supplied. The booking request carries no resource field at all,
    /// which is what makes "the server assigns the room" structural rather than a rule somebody
    /// has to remember to apply — the same shape as an internal block carrying no professional.
    /// </remarks>
    public Guid ResourceId { get; private set; }

    public Guid AppointmentTypeId { get; private set; }

    /// <summary>
    /// When the appointment happens, as one value.
    /// </summary>
    /// <remarks>
    /// One property rather than two because the database stores one <c>tstzrange</c> column, and
    /// that column is what the three exclusion constraints index (design B3). Two scalar columns
    /// would need a third, derived one for the constraint to operate on, and then two places
    /// could disagree about the same appointment's time.
    /// </remarks>
    public TimeRange Range { get; private set; }

    /// <summary>The instant the appointment begins. Reads through to <see cref="Range"/>.</summary>
    public Instant StartsAt => Range.Start;

    /// <summary>The instant it ends, exclusive. Reads through to <see cref="Range"/>.</summary>
    public Instant EndsAt => Range.End;

    public AppointmentStatus Status { get; private set; }

    public AppointmentSource Source { get; private set; }

    /// <summary>
    /// The appointment this one replaced, or null if it was booked outright (02 §3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written only by <see cref="RescheduleTo"/>, on the <em>new</em> appointment. The original
    /// keeps its own range and becomes <see cref="AppointmentStatus.Rescheduled"/>, so "this
    /// appointment was moved from 09:00 to 14:00" is reconstructible — which is the whole reason
    /// a reschedule creates a row instead of editing one. Audit and LGPD both want the 09:00 row
    /// to still say 09:00.
    /// </para>
    /// <para>
    /// <b>The chain is allowed to grow.</b> A moved twice is A → B → C, each link naming the
    /// appointment it directly replaced, and nothing collapses it. No query in
    /// <c>booking-lifecycle</c> walks the chain; it is written so that it is there when something
    /// needs it, which is the opposite of the bet 5a declined to make when the producer did not
    /// exist yet.
    /// </para>
    /// </remarks>
    public Guid? RescheduledFromId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// Whether this appointment occupies its professional, its room, and its patient.
    /// </summary>
    /// <remarks>
    /// The predicate lives here rather than being spelled out at each call site, for the reason
    /// <see cref="TimeBlock.BusyIntervalsOf"/> records: a rule applied to the wrong subset passes
    /// every unit test and is wrong in production. It also has to agree exactly with the
    /// exclusion constraints' <c>WHERE status = 'Scheduled'</c> predicate — one notion of live, in
    /// two places that cannot drift because both name the same single value.
    /// </remarks>
    public bool IsLive => Status == AppointmentStatus.Scheduled;

    /// <summary>The appointment as the solver consumes it — one busy interval among many.</summary>
    public BusyInterval Interval => BusyInterval.Between(Range.Start, Range.End, BusyCause.Appointment);

    /// <summary>
    /// Books an appointment, refusing every way it could be wrong.
    /// </summary>
    /// <param name="parameters">
    /// The same configured step, lead time and horizon the availability read uses. Passed in
    /// rather than defaulted, so the write cannot enforce a different I8 than the read applied.
    /// </param>
    /// <param name="now">
    /// The instant the booking is being made at. Passed in rather than read from a clock, so a
    /// test can place "now" anywhere — including either side of a daylight-saving transition.
    /// </param>
    /// <exception cref="BookingRuleViolationException">
    /// A rule an honest caller could break: the professional is not qualified (I2), or the start
    /// falls outside the lead time or the horizon (I8).
    /// </exception>
    /// <exception cref="DomainRuleViolationException">
    /// Something no request can express: a missing reference, a non-positive duration, or a
    /// resource of the wrong type (I3).
    /// </exception>
    public static Appointment Book(
        AppointmentBooking booking,
        SchedulingParameters parameters,
        Instant now,
        DateTimeOffset createdAtUtc)
    {
        RequireReference(booking.PatientId, "patient");
        RequireReference(booking.ProfessionalId, "professional");
        RequireReference(booking.ResourceId, "resource");
        RequireReference(booking.AppointmentTypeId, "appointment type");

        // I2. Checked before I8 so a patient who is both unqualified and too early is told the
        // thing that will not change by waiting.
        if (!booking.ProfessionalHoldsDurationForType)
        {
            throw new BookingRuleViolationException(
                BookingRefusal.SpecialtyMismatch,
                $"Professional {booking.ProfessionalId} holds no active duration for appointment "
                + $"type {booking.AppointmentTypeId}, so they are not qualified for it.");
        }

        // I3. Not a BookingRefusal: the server chooses the room itself, so a mismatch here means
        // the server chose wrongly, which is a bug and not an answer a patient could act on.
        if (booking.ResourceTypeId != booking.RequiredResourceTypeId)
        {
            throw new DomainRuleViolationException(
                $"Resource {booking.ResourceId} is of resource type {booking.ResourceTypeId}, but "
                + $"appointment type {booking.AppointmentTypeId} requires "
                + $"{booking.RequiredResourceTypeId}.");
        }

        if (booking.DurationMinutes <= 0)
        {
            // Refused at configuration time, so this guards against a corrupt row rather than
            // stating a rule — the same guard the solver applies before slicing.
            throw new DomainRuleViolationException(
                $"An appointment needs a positive duration; got {booking.DurationMinutes} minutes.");
        }

        // I1 — the duration in force RIGHT NOW is baked into the range, and the appointment
        // consults it never again. A later edit to the professional's duration for this type
        // moves future searches and cannot reach this row.
        var duration = Duration.FromMinutes(booking.DurationMinutes);

        // TimeRange refuses an empty or reversed range, so the remaining half of I1 — that the
        // range moves forward — is enforced by construction rather than by a later assertion.
        var range = TimeRange.Between(booking.StartsAt, booking.StartsAt + duration);

        // I8, in the two directions the catalogue names separately because the remedies differ:
        // "too soon" resolves by waiting, "too far ahead" by coming back later.
        if (booking.StartsAt < now + parameters.MinimumLeadTime)
        {
            throw new BookingRuleViolationException(
                BookingRefusal.LeadTimeViolation,
                $"{booking.StartsAt} is sooner than the minimum lead time of "
                + $"{parameters.MinimumLeadTime} from {now}.");
        }

        if (booking.StartsAt > now + parameters.Horizon)
        {
            throw new BookingRuleViolationException(
                BookingRefusal.HorizonExceeded,
                $"{booking.StartsAt} is beyond the scheduling horizon of {parameters.Horizon} "
                + $"from {now}.");
        }

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            PatientId = booking.PatientId,
            ProfessionalId = booking.ProfessionalId,
            ResourceId = booking.ResourceId,
            AppointmentTypeId = booking.AppointmentTypeId,
            Range = range,

            // The only reachable state in this change. There is no setter and no transition
            // method: 5b adds those with their own guards and their own tests (design B10).
            Status = AppointmentStatus.Scheduled,
            Source = booking.Source,
            CreatedAtUtc = createdAtUtc,
        };

        return appointment;
    }

    /// <summary>
    /// Calls the appointment off, freeing the time it held (02 §3).
    /// </summary>
    /// <param name="cutoffApplies">
    /// Whether the cancellation cutoff binds this caller. A fact, not a role — see
    /// <see cref="CancellationCutoffPolicy"/> for why the aggregate is told rather than asked.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Two preconditions, and no third.</b> The appointment must be live, and the cutoff must
    /// permit it. Nothing else: cancelling needs no qualification, no room, no duration and no
    /// scheduling parameters, because it takes nothing and gives everything back. That asymmetry
    /// with <see cref="RescheduleTo"/> is the reason these are two named methods rather than one
    /// generic mover taking the union of both parameter lists and ignoring half of it.
    /// </para>
    /// <para>
    /// The row is <b>not</b> deleted and its range is not touched (I10). The status is the entire
    /// mechanism: <see cref="IsLive"/> goes false, and the same instant the three exclusion
    /// constraints stop seeing this row, because their predicate names the one value this leaves.
    /// One notion of "live", two floors, no migration.
    /// </para>
    /// </remarks>
    /// <exception cref="BookingRuleViolationException">
    /// Already terminal, or inside the cutoff.
    /// </exception>
    public void Cancel(CancellationCutoffPolicy cutoff, Instant now, bool cutoffApplies)
    {
        RequireChangeable(cutoff, now, cutoffApplies);

        Status = AppointmentStatus.Cancelled;
    }

    /// <summary>
    /// Moves the appointment to a new time by replacing it (02 §3).
    /// </summary>
    /// <param name="booking">
    /// The replacement's facts. Its patient, professional and appointment type MUST match this
    /// appointment's — a reschedule keeps all three, and a request cannot express otherwise
    /// because the wire contract carries none of them.
    /// </param>
    /// <returns>The new, live appointment. This one is now terminal.</returns>
    /// <remarks>
    /// <para>
    /// <b>The replacement is built before this appointment is touched</b>, and the ordering is
    /// deliberate: <see cref="Book"/> can refuse, and a refusal must leave the original exactly as
    /// it was. Building second would mean every rule violation had to be undone rather than simply
    /// not applied.
    /// </para>
    /// <para>
    /// <b>Every rule is enforced against the NEW time by the code that already enforces it.</b>
    /// The replacement goes through <see cref="Book"/>, so I1, I2, I3 and I8 hold for it exactly
    /// as they would for an outright booking — including the one that distinguishes the two
    /// readings of I1: the duration baked in is the duration in force *now*, so a professional who
    /// changed their duration for this type between the booking and the reschedule moves the
    /// replacement and can never reach the original.
    /// </para>
    /// <para>
    /// <b>Same professional and same appointment type, refused structurally.</b> Moving to a
    /// different professional is a cancellation followed by a new booking, not a reschedule — an
    /// appointment is a commitment with a particular person. The useful side effect is that the
    /// caller needs only one professional's lock, so the deadlock a two-professional reschedule
    /// would introduce does not exist to be solved.
    /// </para>
    /// </remarks>
    /// <exception cref="BookingRuleViolationException">
    /// Already terminal, inside the cutoff, or a rule <see cref="Book"/> refuses.
    /// </exception>
    /// <exception cref="DomainRuleViolationException">
    /// The replacement names a different patient, professional or appointment type — which no
    /// request can express, so it is a bug and not an answer.
    /// </exception>
    public Appointment RescheduleTo(
        AppointmentBooking booking,
        SchedulingParameters parameters,
        CancellationCutoffPolicy cutoff,
        Instant now,
        DateTimeOffset createdAtUtc,
        bool cutoffApplies)
    {
        RequireChangeable(cutoff, now, cutoffApplies);

        RequireSame(booking.PatientId, PatientId, "patient");
        RequireSame(booking.ProfessionalId, ProfessionalId, "professional");
        RequireSame(booking.AppointmentTypeId, AppointmentTypeId, "appointment type");

        var replacement = Book(booking, parameters, now, createdAtUtc);

        // Written on the new row, naming the appointment it DIRECTLY replaced. A appointment moved
        // twice is a chain of two links rather than two rows both pointing at the original, so the
        // history reads in the order it happened.
        replacement.RescheduledFromId = Id;

        // Last, and only once nothing else can throw. Note that this is the statement whose
        // ORDER relative to the replacement's INSERT is load-bearing at the persistence layer —
        // the exclusion indexes are partial and non-deferrable, so the row must leave them before
        // the replacement joins. That ordering is the caller's to get right; see the reschedule
        // handler, which says so at the two statements.
        Status = AppointmentStatus.Rescheduled;

        return replacement;
    }

    /// <summary>
    /// The two preconditions both transitions share.
    /// </summary>
    /// <remarks>
    /// Terminal is checked before the cutoff so that a patient cancelling an already-cancelled
    /// appointment is told what is actually true, rather than being told about a deadline for a
    /// change that has already happened.
    /// </remarks>
    private void RequireChangeable(CancellationCutoffPolicy cutoff, Instant now, bool cutoffApplies)
    {
        if (!IsLive)
        {
            throw new BookingRuleViolationException(
                BookingRefusal.AppointmentNotChangeable,
                $"Appointment {Id} is {Status} and only a scheduled appointment can be changed.");
        }

        if (!cutoff.Permits(StartsAt, now, cutoffApplies))
        {
            throw new BookingRuleViolationException(
                BookingRefusal.CutoffPassed,
                $"Appointment {Id} starts at {StartsAt}, sooner than the {cutoff.Notice} notice "
                + $"required from {now}.");
        }
    }

    private static void RequireSame(Guid replacement, Guid original, string name)
    {
        if (replacement != original)
        {
            // Not a BookingRefusal: the reschedule contract carries only an instant, so a caller
            // cannot ask for this and there is no message a patient could act on. A guard against
            // a future caller assembling the replacement wrongly.
            throw new DomainRuleViolationException(
                $"A reschedule keeps the same {name}; got {replacement} for an appointment whose "
                + $"{name} is {original}. Moving to a different {name} is a cancellation followed "
                + "by a new booking.");
        }
    }

    /// <summary>
    /// The busy intervals a set of appointments contributes to availability — live ones only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="TimeBlock.BusyIntervalsOf"/>, and the pairing is the point: change
    /// 4 built one busy list that says nothing about why somebody is busy, betting that
    /// appointments would join it without the subtraction changing. They do.
    /// </para>
    /// <para>
    /// The one asymmetry worth naming, because it is what the bet nearly missed: a block
    /// contributes to <b>one</b> list — its professional's — while an appointment contributes to
    /// <b>two</b>, its professional's and its room's. The second list existed only because change
    /// 4 reversed its own first cut and made the resource half a candidate set instead of a
    /// boolean, so filling it here is a list to populate rather than a rule to implement
    /// (design B11).
    /// </para>
    /// </remarks>
    public static IReadOnlyList<BusyInterval> BusyIntervalsOf(IEnumerable<Appointment> appointments) =>
        appointments.Where(appointment => appointment.IsLive)
            .Select(appointment => appointment.Interval)
            .ToList();

    private static void RequireReference(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            // Not reachable from a request: the patient comes from the session, the resource from
            // the solver, and the other two are resolved before this is called. A guard against a
            // future caller, in the same spirit as TimeBlock's.
            throw new DomainRuleViolationException($"An appointment requires a {name}.");
        }
    }
}
