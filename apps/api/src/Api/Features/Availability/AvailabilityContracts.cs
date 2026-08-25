namespace Clinic.Api.Features.Availability;

/// <summary>
/// One time an appointment of the requested type could be placed.
/// </summary>
/// <remarks>
/// <para>
/// <b>UTC instants on the wire</b>, formatted ISO-8601. The consumer of this is a booking flow
/// that has to reason about real time; handing it wall clock would make it re-derive the clinic's
/// zone, and a client that gets that wrong books an appointment an hour out.
/// </para>
/// <para>
/// The professional is named because in any-professional mode that <em>is</em> the answer, and the
/// resource because the pair is what actually satisfies the slot (design F6).
/// </para>
/// <para>
/// <b>The resource is an explanation, not a reservation.</b> By the time a caller confirms, that
/// room may be taken. The booking path assigns the room itself (domain-model F2) and does not
/// accept this value back as authority — a client that echoes it is telling the server something
/// the server already knows better. A staff surface showing the room must therefore say it is the
/// room that WOULD be used; the assigned one comes back on the booking response.
/// </para>
/// <para>
/// <b><see cref="ResourceName"/> was added by <c>booking-desk</c>, and D7 is unchanged</b>
/// (design N5). D7 says no room is named on a PATIENT SURFACE — a statement about what a screen
/// renders, and the patient portal renders none. The wire has carried the room identity since
/// change 4; this adds its label so that S4 and S5, which are required to show a room, need no
/// second request against a catalogue endpoint reception may not reach.
/// </para>
/// </remarks>
internal sealed record AvailabilitySlotResponse(
    Guid ProfessionalId,
    Guid ResourceId,
    string ResourceName,
    string Start,
    string End);

/// <summary>
/// The answer to an availability question.
/// </summary>
/// <param name="Timezone">
/// The clinic's configured zone id. Echoed so a client can render the instants above as clinic
/// wall clock without guessing, and so the answer is self-describing in a log or a test failure.
/// </param>
internal sealed record AvailabilityResponse(
    Guid AppointmentTypeId,
    string From,
    string To,
    string Timezone,
    IReadOnlyList<AvailabilitySlotResponse> Slots);

/// <summary>
/// A professional's own unavailability, as S3 reads it.
/// </summary>
/// <remarks>
/// <b>Clinic wall clock on the wire</b>, unlike <see cref="AvailabilitySlotResponse"/> above, and
/// the asymmetry is deliberate. A block is entered and read by a person standing in the clinic,
/// whose only frame of reference is the clinic's clock; the server owns the conversion so no
/// browser has to do zone arithmetic with a <c>datetime-local</c> control that has no zone to
/// begin with. An availability slot, by contrast, is consumed by a machine. Both carry the
/// timezone id so neither is ambiguous.
/// </remarks>
internal sealed record TimeBlockResponse(Guid Id, string StartsAt, string EndsAt, bool IsActive);

/// <summary>A professional's blocks, and the zone their times are expressed in.</summary>
internal sealed record TimeBlockListResponse(string Timezone, IReadOnlyList<TimeBlockResponse> Blocks);

/// <summary>
/// Creating or moving a block.
/// </summary>
/// <remarks>
/// Carries no professional. A new block always belongs to the caller, which is what makes "a
/// block cannot be aimed at somebody else" structural rather than a check somebody has to
/// remember to write (design F11).
/// </remarks>
internal sealed record SaveTimeBlockRequest(string? StartsAt, string? EndsAt);
