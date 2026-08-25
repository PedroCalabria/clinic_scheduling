using System.Security.Claims;
using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.Booking;

/// <summary>
/// Who is acting on an appointment, and on whose behalf (design N2, N3).
/// </summary>
/// <remarks>
/// <para>
/// <b>One helper for three endpoints, and that is the whole reason staff share the patient's
/// routes rather than getting mirrors of them.</b> Booking, cancelling and rescheduling each need
/// the same four answers — which patient, whether the cutoff applies, whether an unknown id is a
/// 404 or a 403, and what source to record — and all four follow from the role alone. Three
/// copies of that branch is three places for it to drift, on paths where drifting means an
/// authorization hole rather than a wrong answer.
/// </para>
/// <para>
/// The reschedule path is the sharper argument. Its statement ordering — UPDATE the old row out of
/// the partial exclusion indexes <em>before</em> inserting its replacement — is a correctness
/// property whose failure mode is "a near reschedule always fails, a distant one never does". A
/// second implementation of that path for staff would be a second place to get it subtly wrong.
/// </para>
/// </remarks>
internal readonly record struct BookingActor
{
    private BookingActor(Role role, Guid patientId)
    {
        Role = role;
        PatientId = patientId;
    }

    /// <summary>The acting user's role, from their session.</summary>
    public Role Role { get; }

    /// <summary>
    /// The patient this caller acts as — resolved, never echoed from the request.
    /// </summary>
    /// <remarks>
    /// <c>Guid.Empty</c> for a staff caller on the lifecycle paths, where there is no such patient:
    /// reception acts on <em>an appointment</em>, and the appointment names its own patient. Read
    /// it only where <see cref="IsClinic"/> is false, or through <see cref="Reaches"/>, which is
    /// what the write paths actually use.
    /// </remarks>
    public Guid PatientId { get; }

    /// <summary>True when the clinic is acting rather than the patient themselves.</summary>
    public bool IsClinic => Role is Role.FrontDesk or Role.Administrator;

    /// <summary>
    /// Whether the cancellation cutoff binds this caller (<c>02-domain-model.md</c> §5).
    /// </summary>
    /// <remarks>
    /// The second caller of the authority parameter <c>booking-lifecycle</c> built, and the first
    /// to ever pass <c>false</c>. The domain still knows nothing about roles: it is handed a fact,
    /// and this is where the fact is established. <c>AppointmentLifecycleTests.cs:258</c> specified
    /// what happens on the <c>false</c> side before any caller existed.
    /// </remarks>
    public bool CutoffApplies => !IsClinic;

    /// <summary>How an appointment created by this caller is recorded.</summary>
    public AppointmentSource Source => IsClinic ? AppointmentSource.FrontDesk : AppointmentSource.SelfService;

    /// <summary>
    /// The answer for an appointment this caller cannot reach — whether that is because it belongs
    /// to somebody else or because it does not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A patient gets <c>auth.ownership_denied</c> for both, so the response cannot be used to
    /// enumerate appointment ids. Staff get <c>booking.appointment_not_found</c>, which the error
    /// catalogue reserved for exactly this branch — "a path whose caller is entitled to
    /// distinguish absence from denial" — because a receptionist who mistypes an id needs to know
    /// they mistyped it, and there is no record they are not entitled to reach.
    /// </para>
    /// </remarks>
    public IResult CannotReach() =>
        IsClinic
            ? ApiError.Result(ErrorCodes.BookingAppointmentNotFound, StatusCodes.Status404NotFound)
            : ApiError.Result(ErrorCodes.OwnershipDenied, StatusCodes.Status403Forbidden);

    /// <summary>Either an actor, or the refusal to return instead.</summary>
    internal readonly record struct Result(BookingActor? Actor, IResult? Refusal)
    {
        internal bool Resolved => Actor is not null;
    }

    /// <summary>
    /// Resolves who is acting on the two lifecycle paths, where the appointment names the patient.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No patient is named on these paths and none is accepted</b> — not even from staff. A
    /// cancel or a reschedule identifies its appointment, and the appointment identifies its
    /// patient, so a patient id in the request could only ever disagree with the row. The booking
    /// path is the opposite case and has its own resolution below: there is no appointment yet, so
    /// somebody has to say who it is for.
    /// </para>
    /// <para>
    /// A patient still needs their own record resolved, because it is the filter that makes "their
    /// own appointment" a predicate rather than a check somebody remembers to write. Staff need
    /// nothing resolved at all: there is no appointment reception may not act on.
    /// </para>
    /// </remarks>
    public static async Task<Result> ForLifecycleAsync(
        ClaimsPrincipal actor,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var role = actor.Role();

        if (role is Role.FrontDesk or Role.Administrator)
        {
            return new Result(new BookingActor(role, Guid.Empty), null);
        }

        return await OwnPatientAsync(actor, role, database, cancellationToken);
    }

    /// <summary>
    /// Resolves who is acting and for whom, from the session and — only for staff — the request.
    /// </summary>
    /// <param name="requestedPatientId">
    /// The patient named in the request body, if any. <b>Honoured only for a staff caller.</b>
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>A patient supplying the field is refused, not ignored</b> (design N3). The identity rule
    /// says a client-supplied identifier "never widens access", which admits both readings — but
    /// this operation <em>writes</em>. Silently substituting the session's patient would create a
    /// real appointment for the wrong person and report success, and nobody would find out until
    /// somebody arrived at the clinic. The refusal covers their own id too: the field is refused
    /// by role rather than validated by value, so there is no path on which a patient's request
    /// body influences whose appointment this is.
    /// </para>
    /// <para>
    /// A staff caller must name a patient, having no patient record of their own to fall back to.
    /// That is <c>validation.required</c> rather than a 403 — the caller is entitled to be here
    /// and left something out.
    /// </para>
    /// </remarks>
    public static async Task<Result> ResolveAsync(
        ClaimsPrincipal actor,
        Guid? requestedPatientId,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var role = actor.Role();

        if (role is Role.FrontDesk or Role.Administrator)
        {
            if (requestedPatientId is not { } patientId || patientId == Guid.Empty)
            {
                return new Result(null, CatalogRefusals.Required("patientId"));
            }

            var exists = await database.Patients.AnyAsync(
                candidate => candidate.Id == patientId && candidate.DeletedAtUtc == null,
                cancellationToken);

            return exists
                ? new Result(new BookingActor(role, patientId), null)

                // Staff are entitled to distinguish absence from denial, so this is the plain
                // 404 PatientLookup already settled on for them.
                : new Result(null, ApiError.Result(ErrorCodes.PatientNotFound, StatusCodes.Status404NotFound));
        }

        if (requestedPatientId is not null)
        {
            return new Result(null, ApiError.Result(ErrorCodes.Forbidden, StatusCodes.Status403Forbidden));
        }

        return await OwnPatientAsync(actor, role, database, cancellationToken);
    }

    /// <summary>The caller acting as their own patient record.</summary>
    private static async Task<Result> OwnPatientAsync(
        ClaimsPrincipal actor,
        Role role,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var own = await database.Patients
            .Where(candidate => candidate.UserId == actor.UserId() && candidate.DeletedAtUtc == null)
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (own is not { } ownId)
        {
            // A patient session with no patient record. Not reachable through provisioning, which
            // creates both together — a corrupt-state guard rather than a rule.
            return new Result(null, ApiError.Result(ErrorCodes.PatientNotFound, StatusCodes.Status404NotFound));
        }

        return new Result(new BookingActor(role, ownId), null);
    }
}
