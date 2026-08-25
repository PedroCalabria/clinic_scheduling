namespace Clinic.Api.Features.Schedule;

/// <summary>
/// One appointment as S1 and S4 read it.
/// </summary>
/// <param name="PatientName">
/// <b>The reason this read writes an <c>AccessLog</c> row.</b> Every other field here is clinic
/// configuration; this one is a person's personal data being shown to somebody who is not them,
/// which is precisely the access <c>02-domain-model.md</c> §8 promises to record. S1 and S4 are the
/// first screens in the product where that is true — the block path documented writing no row
/// because a block names nobody.
/// </param>
/// <param name="ResourceName">
/// The room, <b>shown on staff surfaces</b>. D7 keeps a patient from being told which room; a
/// receptionist has to tell them where to go, and a professional needs to know where they are
/// sitting (design N5).
/// </param>
/// <param name="PatientCanChange">
/// Whether <b>the patient</b> may still cancel or move this — not whether the reader may.
/// <para>
/// Decided by the server for the reason 5b's C10 gives: a browser's clock is not the clinic's and
/// is user-settable. Named for the patient rather than for the caller because S4's whole
/// demonstration is the sentence "the patient can no longer change this, and you can". A flag
/// meaning "the reader may act" would be constantly true for reception and would say nothing.
/// </para>
/// </param>
internal sealed record ScheduledAppointment(
    Guid Id,
    Guid ProfessionalId,
    string ProfessionalName,
    Guid PatientId,
    string PatientName,
    Guid AppointmentTypeId,
    string AppointmentTypeName,
    Guid ResourceId,
    string ResourceName,
    string StartsAt,
    string EndsAt,
    string Status,
    string Source,
    bool PatientCanChange);

/// <summary>
/// One period a professional has declared themselves unavailable for.
/// </summary>
/// <remarks>
/// Carried on the same payload as the appointments rather than fetched separately, because a day
/// with a gap in it and a day with a declared block in it look identical otherwise — and a
/// receptionist deciding whether to offer 14:00 needs to know which one they are looking at. It
/// names no patient, so it contributes nothing to the access record.
/// </remarks>
internal sealed record ScheduledBlock(
    Guid Id,
    Guid ProfessionalId,
    string ProfessionalName,
    string StartsAt,
    string EndsAt);

/// <summary>
/// A clinic day, as a professional sees their own (S1) or as reception sees all of them (S4).
/// </summary>
/// <param name="Date">The clinic date requested, echoed as a wall-clock date.</param>
/// <param name="Timezone">
/// The zone the instants above are rendered in. Carried for the same reason every other scheduling
/// payload carries it: the times are instants, and clinic wall clock is the only correct way to
/// show them.
/// </param>
/// <remarks>
/// <b>One shape for both screens</b> (design N9). They ask the same question with a different
/// scope, and the scope is settled by the caller's role rather than by the payload — so two shapes
/// would be two of everything, including two places to write the access record, which is the
/// duplication in this change most costly to get wrong.
/// </remarks>
internal sealed record ScheduleDayResponse(
    string Date,
    string Timezone,
    IReadOnlyList<ScheduledAppointment> Appointments,
    IReadOnlyList<ScheduledBlock> Blocks);

/// <summary>
/// A patient reception has resolved for a booking (design N8).
/// </summary>
/// <param name="HasDataProcessingConsent">
/// Whether this patient currently holds an active data-processing consent at the configured
/// version — the same query the booking gate runs.
/// <para>
/// Returned so that a receptionist learns it <b>before</b> taking a walk-in's time rather than as
/// an <c>auth.consent_required</c> refusal after choosing a slot. The gate itself is not relaxed
/// for staff, and must not be: exempting reception would let the clinic route around a patient's
/// own withdrawal by telephoning the desk.
/// </para>
/// </param>
internal sealed record ResolvedPatient(
    Guid PatientId,
    string FullName,
    string ContactEmail,
    bool HasDataProcessingConsent);
