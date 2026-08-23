using Clinic.Domain.Configuration;
using NodaTime;

namespace Clinic.Domain.Scheduling;

/// <summary>
/// One professional's schedule, as the solver needs it for a window.
/// </summary>
/// <param name="ProfessionalId">
/// The <see cref="Professional"/> record id, not the user id. Availability is about clinical
/// configuration, and an unconfigured professional has no record, no hours and no durations —
/// so unlike S7, this surface never has to represent one.
/// </param>
/// <param name="DurationMinutes">
/// What the requested appointment type takes <em>this</em> professional. The whole reason
/// Decision C put duration on a junction.
/// </param>
/// <param name="Segments">Active recurring segments. The caller filters for active.</param>
/// <param name="Exceptions">Active exceptions falling inside the window.</param>
/// <param name="BusyIntervals">
/// Every interval in which this professional is already busy, whatever the cause (design F5).
/// </param>
public sealed record ProfessionalSchedule(
    Guid ProfessionalId,
    int DurationMinutes,
    IReadOnlyList<WorkingHoursTemplate> Segments,
    IReadOnlyList<WorkingHoursException> Exceptions,
    IReadOnlyList<BusyInterval> BusyIntervals);

/// <summary>
/// One concrete room or machine of the required type, and when it is already taken.
/// </summary>
/// <remarks>
/// <para>
/// The resource half of the tri-constraint, as a real candidate set rather than a boolean. It was
/// a boolean until the design's open question 1 was answered: a slot now names the resource that
/// satisfies it (design F6), so the solver has to choose one, which means it has to know which
/// ones are free.
/// </para>
/// <para>
/// <see cref="BusyIntervals"/> is the same shape of seam as a professional's, and today it is
/// always empty for the same reason: nothing occupies a room until change 5 books an appointment
/// into it. The difference from the boolean it replaced is that filling it is now the only thing
/// change 5 has to do — the choosing, the buffer, and the "no free room means no slot" rule are
/// written and tested here.
/// </para>
/// </remarks>
/// <param name="BufferMinutes">
/// Turnaround for this resource's type (02-domain-model.md, decision F1), kept out of the
/// bookable window. Comes from <see cref="Configuration.ResourceType"/>, which already refuses a
/// negative value.
/// </param>
public sealed record ResourceCandidate(
    Guid ResourceId,
    int BufferMinutes,
    IReadOnlyList<BusyInterval> BusyIntervals);

/// <summary>
/// Everything <see cref="AvailabilitySolver"/> needs, and nothing it has to go and fetch
/// (design F1).
/// </summary>
/// <remarks>
/// <para>
/// This is the seam between the slice and the protected core, and it is a plain record rather
/// than an interface on purpose. <c>Domain</c> declares the shape and the function;
/// <c>Api</c> fills one and calls the other. A repository interface here so that <c>Api</c>
/// could implement it would be ports-and-adapters ceremony this project has already declined
/// once (P-3), and it would let a lazy load creep into a computation whose whole value is being
/// pure and unit-testable.
/// </para>
/// <para>
/// The consequence to accept is that the bounded read has to be right: an over-fetch is slow and
/// an under-fetch is <em>wrong</em>, and only an integration test can tell. That is why the
/// loading step lives in exactly one place.
/// </para>
/// </remarks>
/// <param name="AppointmentTypeId">Echoed onto every slot, so a slot is self-describing.</param>
/// <param name="FromDate">First date of the window, in clinic wall-clock terms.</param>
/// <param name="ToDate">Last date of the window, inclusive.</param>
/// <param name="ClinicZone">
/// The single configured clinic timezone (Decision H). Every wall-clock value in
/// <paramref name="Professionals"/> is interpreted against this.
/// </param>
/// <param name="Now">
/// The instant the request is being answered at, for the lead time and the horizon. Passed in
/// rather than read from a clock, so a test can place "now" anywhere — including either side of
/// a daylight-saving transition.
/// </param>
/// <param name="Resources">
/// The active resources of the appointment type's required resource type, <b>in the order they
/// should be preferred</b> — the solver takes the first free one, so the caller's ordering is the
/// assignment policy. An empty list means the clinic owns no room this visit could happen in, and
/// nothing is offerable however free the professionals are.
/// </param>
/// <param name="Parameters">Step, lead time and horizon.</param>
/// <param name="Professionals">
/// Every professional eligible for the requested appointment type. One element for the
/// specific-professional query, several for any-professional — there is no second code path
/// (design F7).
/// </param>
public sealed record AvailabilityInputs(
    Guid AppointmentTypeId,
    LocalDate FromDate,
    LocalDate ToDate,
    DateTimeZone ClinicZone,
    Instant Now,
    IReadOnlyList<ResourceCandidate> Resources,
    SchedulingParameters Parameters,
    IReadOnlyList<ProfessionalSchedule> Professionals);

/// <summary>
/// A time at which an appointment of the requested type could be placed.
/// </summary>
/// <remarks>
/// <para>
/// Instants, because a slot is a real time and the client must not have to re-derive a zone to
/// know when it is.
/// </para>
/// <para>
/// <b>It names the resource that satisfies it</b>, completing the
/// <c>(professional, resource)</c> pair 02-domain-model.md §4 describes (design F6).
/// </para>
/// <para>
/// The danger that comes with that, recorded because it does not go away: this is <b>not</b> a
/// reservation. By the time a patient confirms, that room may be taken. Change 5's booking path
/// must therefore treat a client-supplied <see cref="ResourceId"/> as a hint at most and assign
/// the room itself (domain-model F2) — the id is here to explain the answer, never to be handed
/// back as authority.
/// </para>
/// </remarks>
public sealed record AvailabilitySlot(
    Guid ProfessionalId,
    Guid AppointmentTypeId,
    Guid ResourceId,
    Instant Start,
    Instant End);
