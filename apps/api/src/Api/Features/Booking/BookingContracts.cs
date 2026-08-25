namespace Clinic.Api.Features.Booking;

/// <summary>
/// A patient booking one slot (design B9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three fields, and the two that are absent are the interesting ones.</b>
/// </para>
/// <para>
/// There is no <c>resourceId</c>. Not ignored if present — <em>absent from the contract</em>, so
/// "the server assigns the room" (domain-model F2) is structural rather than a rule somebody has
/// to remember to apply. Change 4 shipped a slot naming its room and recorded the hazard that a
/// client might echo it back as authority; the mitigation it named was a constraint on this
/// change, and this is where that constraint is kept. The same shape as an internal block
/// carrying no professional.
/// </para>
/// <para>
/// There is no <c>patientId</c>. The appointment belongs to the caller's own patient record, read
/// from the session. <c>booking-lifecycle</c> widens this path for the front desk booking on
/// somebody's behalf, and it will do so by adding an explicit, role-gated field — never by
/// starting to trust a body value this change ignores.
/// </para>
/// <para>
/// <see cref="StartsAt"/> is a <b>UTC instant</b>, ISO-8601, never a wall-clock label (Q4). A date
/// on which the clinic timezone turns its clock back legitimately produces two slots reading the
/// same local time an hour apart in real time; an instant distinguishes them and a label cannot.
/// The end is not sent either — it is derived from the professional's duration for the type, which
/// is what "the duration is baked in at booking" (I1) means on the wire.
/// </para>
/// </remarks>
internal sealed record BookAppointmentRequest(
    Guid? AppointmentTypeId,
    Guid? ProfessionalId,
    string? StartsAt);

/// <summary>
/// The appointment that now exists.
/// </summary>
/// <remarks>
/// <para>
/// Instants on the wire, like an availability slot and unlike a time block: the consumer is a
/// booking flow that has to reason about real time, and it renders clinic wall clock from these
/// using the timezone below rather than the browser's own zone.
/// </para>
/// <para>
/// <b>No room.</b> The server assigned one and the appointment holds it, but a patient does not
/// need to know which — and putting it here would invite a client to send it back on some future
/// call. What a patient needs is when, with whom, and for what.
/// </para>
/// </remarks>
internal sealed record AppointmentResponse(
    Guid Id,
    Guid ProfessionalId,
    Guid AppointmentTypeId,
    string StartsAt,
    string EndsAt,
    string Status,
    string Timezone);

/// <summary>
/// A patient moving an appointment to a new time (design C3).
/// </summary>
/// <remarks>
/// <para>
/// <b>One field, and the three that are absent are the point.</b> No professional, no appointment
/// type, no room. A reschedule keeps the first two by definition and the server assigns the third,
/// so "a reschedule cannot change the professional" is structural rather than a rule somebody
/// validates — the same shape that made server-side room assignment structural in
/// <see cref="BookAppointmentRequest"/>.
/// </para>
/// <para>
/// Moving to a different professional is a cancellation followed by a new booking, through the two
/// paths that already exist. That is what it means, and it also keeps the professional-scoped lock
/// single-keyed, so the deadlock a two-professional reschedule would introduce does not exist to
/// be solved.
/// </para>
/// <para>
/// A <b>UTC instant</b> like every other slot reference on the wire (Q4), never a wall-clock label.
/// </para>
/// </remarks>
internal sealed record RescheduleAppointmentRequest(string? StartsAt);

/// <summary>
/// One appointment as P5 lists it.
/// </summary>
/// <param name="CanChange">
/// Whether the caller may still reschedule or cancel it — <b>decided by the server</b> (design
/// C10).
/// </param>
/// <param name="IsUpcoming">Whether it has yet to finish, which is how the two lists are split.</param>
/// <remarks>
/// <para>
/// <see cref="CanChange"/> is a decision rather than the inputs to one, and the cutoff duration is
/// deliberately not sent. A browser's clock is not the clinic's and is user-settable; a screen
/// computing the rule locally could show an enabled action the server will refuse, and the entire
/// point of P5 showing the rule is that the rule shown is the rule enforced.
/// </para>
/// <para>
/// It folds two causes together — terminal, and inside the cutoff — because a screen needs to know
/// only that the action is unavailable. The reason it *shows* comes from
/// <see cref="Status"/> plus <see cref="StartsAt"/>, which it already has.
/// </para>
/// </remarks>
internal sealed record MyAppointment(
    Guid Id,
    Guid ProfessionalId,
    Guid AppointmentTypeId,
    string StartsAt,
    string EndsAt,
    string Status,
    bool CanChange,
    bool IsUpcoming)
{
    /// <summary>
    /// The same shape a booking returns, so P6's success and P3's success are one type on the
    /// client.
    /// </summary>
    internal AppointmentResponse ToAppointmentResponse(Clinic.Api.Infrastructure.Time.ClinicTimezone timezone) =>
        new(Id, ProfessionalId, AppointmentTypeId, StartsAt, EndsAt, Status, timezone.Id);
}

/// <summary>
/// P5's payload — the caller's own appointments, split by time.
/// </summary>
/// <remarks>
/// <para>
/// Split by <b>time</b> rather than by status, with terminal appointments annotated where they
/// fall: "what happened to my 3pm?" is the question a patient asks, and a cancelled appointment
/// belongs where they would look for it rather than in a third section. Recorded as design Open
/// Question 1 — the validation guide collects a human opinion before this hardens.
/// </para>
/// <para>
/// <see cref="Timezone"/> travels with the payload for the same reason the availability response
/// carries it: every time above is an instant, and clinic wall clock is the only correct way to
/// render it.
/// </para>
/// </remarks>
internal sealed record MyAppointmentsResponse(
    IReadOnlyList<MyAppointment> Upcoming,
    IReadOnlyList<MyAppointment> Past,
    string Timezone);
