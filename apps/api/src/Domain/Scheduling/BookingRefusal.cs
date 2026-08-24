namespace Clinic.Domain.Scheduling;

/// <summary>
/// Why a booking cannot happen. Each value maps to exactly one code from
/// <c>docs/07-error-codes.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One enum for two producers, deliberately.</b> Some of these are decided by the
/// <see cref="Appointment"/> aggregate as it refuses to be constructed, and some by
/// <see cref="AvailabilitySolver.Explain"/> as it walks a candidate slot. Splitting them would
/// mean two mappings in the slice and two chances for the same failure to arrive as two
/// different codes — and the caller cannot tell which layer said no, nor should they have to.
/// </para>
/// <para>
/// What is deliberately <em>not</em> here: the structurally impossible. A range that does not
/// move forward, a duration that does not match the range it produced, a resource of the wrong
/// type — those are <see cref="DomainRuleViolationException"/>, because no request can express
/// them and there is no message a patient could act on. The line is whether a well-formed
/// request from an honest caller could produce it.
/// </para>
/// </remarks>
public enum BookingRefusal
{
    /// <summary>
    /// The professional has no candidate hours at that time — <c>booking.outside_working_hours</c>,
    /// 422.
    /// </summary>
    OutsideWorkingHours = 1,

    /// <summary>
    /// The start is sooner from now than the configured minimum lead time —
    /// <c>booking.lead_time_violation</c>, 422 (invariant I8).
    /// </summary>
    LeadTimeViolation = 2,

    /// <summary>
    /// The start is further ahead than the configured scheduling horizon —
    /// <c>booking.horizon_exceeded</c>, 422 (invariant I8).
    /// </summary>
    HorizonExceeded = 3,

    /// <summary>
    /// The professional holds an internal block over that time — <c>booking.slot_blocked</c>,
    /// 409 (the booking direction of invariant I7).
    /// </summary>
    /// <remarks>
    /// Distinguished from <see cref="SlotTaken"/> because the remedy differs: nobody was faster,
    /// the professional declared themselves unavailable. The solver has to keep the two apart
    /// while subtracting them identically, which is the one place the busy set's deliberate
    /// silence about cause has to be broken.
    /// </remarks>
    SlotBlocked = 4,

    /// <summary>
    /// The professional already holds an appointment over that time —
    /// <c>booking.slot_taken</c>, 409 (invariant I4).
    /// </summary>
    SlotTaken = 5,

    /// <summary>
    /// The patient already holds an appointment over that time — <c>booking.patient_busy</c>,
    /// 409 (invariant I6).
    /// </summary>
    /// <remarks>
    /// Decided by the slice rather than the solver: the solver is given one professional's
    /// schedule and knows nothing about a patient's other appointments, and widening its input to
    /// carry them would make an availability read depend on who is asking.
    /// </remarks>
    PatientBusy = 6,

    /// <summary>
    /// Every active resource of the required type is occupied —
    /// <c>booking.resource_unavailable</c>, 409 (invariant I5).
    /// </summary>
    ResourceUnavailable = 7,

    /// <summary>
    /// The professional holds no active duration for that appointment type —
    /// <c>booking.specialty_mismatch</c>, 422 (invariant I2).
    /// </summary>
    /// <remarks>
    /// The duration is the qualification gate <c>professional-configuration</c> built: it may
    /// only exist for a type whose specialty the professional holds, so its absence <em>is</em>
    /// "not qualified for this kind of visit" and the specialty check comes along for free.
    /// </remarks>
    SpecialtyMismatch = 8,
}

/// <summary>
/// A booking rule said no, and named which one.
/// </summary>
/// <remarks>
/// The same shape as <c>CatalogRuleViolationException</c> and for the same reason: one endpoint
/// can refuse for several distinct reasons carrying different codes and different statuses, so
/// the reason travels with the refusal rather than being re-derived by the slice that just asked.
///
/// The message is for logs and developers only, never returned to a caller (Decision I).
/// </remarks>
public sealed class BookingRuleViolationException(BookingRefusal reason, string message)
    : Exception(message)
{
    public BookingRefusal Reason { get; } = reason;
}
