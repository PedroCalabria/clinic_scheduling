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
