using System.Security.Claims;
using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Scheduling;
using Clinic.Api.Infrastructure.Time;
using Clinic.Domain;
using Clinic.Domain.Configuration;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Clinic.Api.Features.Booking;

/// <summary>
/// P3's commit — a patient books a slot (spec: booking; design B1, B2, B8, B9).
/// </summary>
/// <remarks>
/// <para>
/// The change's centre of gravity, and the shape is deliberately flat: resolve, gate, lock, load,
/// ask the domain, insert, map. Nothing here decides whether a slot is bookable — the solver's
/// <see cref="AvailabilitySolver.Explain"/> does that, from the same walk the availability read
/// uses, so the read cannot offer what this refuses. Nothing here decides which room to use
/// either; <c>Explain</c> returns it.
/// </para>
/// <para>
/// <b>The ordering inside the transaction is load-bearing.</b> The professional lock is taken
/// FIRST, before the read it protects, because the race being closed is read-then-write across two
/// tables — this path reads blocks, and the block path reads appointments. A lock acquired after
/// the read serializes nothing, and no functional test would notice (design B7).
/// </para>
/// <para>
/// <b>What the lock does not do:</b> appointment-to-appointment exclusion. The three exclusion
/// constraints enforce that, for the room and the patient as well as the professional, and no
/// professional-scoped lock could cover the other two. So this path can still lose a race after
/// its checks pass, and the catch at the end is where that is answered — which is the design
/// working, not a gap in it.
/// </para>
/// </remarks>
internal static class BookingEndpoints
{
    internal static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/appointments", BookAsync)
            // A patient booking for themselves, or reception booking on somebody's behalf (S5).
            // Widened by booking-desk exactly as 5a promised: an explicit role-gated field, not a
            // relaxed policy that starts trusting a body value. A professional is still refused —
            // booking is reception's work, and a professional who could book on behalf would be a
            // second route to the same write with nothing on this path expecting them.
            .RequireAuthorization(AuthorizationPolicies.PatientOrClinicStaff)
            .WithName("BookAppointment");

        return endpoints;
    }

    private static async Task<IResult> BookAsync(
        BookAppointmentRequest request,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        ScheduleReader reader,
        ClinicTimezone timezone,
        ClinicScheduling scheduling,
        TimeProvider clock,
        IOptions<AuthOptions> auth,
        CancellationToken cancellationToken)
    {
        if (request.AppointmentTypeId is not { } appointmentTypeId || appointmentTypeId == Guid.Empty)
        {
            return CatalogRefusals.Required(nameof(request.AppointmentTypeId));
        }

        if (request.ProfessionalId is not { } professionalId || professionalId == Guid.Empty)
        {
            return CatalogRefusals.Required(nameof(request.ProfessionalId));
        }

        // A UTC instant, never a wall-clock label (Q4). Refused rather than coerced: a client that
        // sends local time is a client that will book an appointment an hour out on a clock-change
        // date, and silently accepting it would hide that until a patient missed a visit.
        if (ParseInstant(request.StartsAt) is not { } startsAt)
        {
            return CatalogRefusals.Invalid(nameof(request.StartsAt));
        }

        // Who is acting, and for whom (design N2, N3). The one place the role decides anything on
        // this path — the patient, and through the actor the source recorded below.
        var resolved = await BookingActor.ResolveAsync(actor, request.PatientId, database, cancellationToken);

        if (resolved.Actor is not { } bookingActor)
        {
            return resolved.Refusal!;
        }

        var patientUserId = await database.Patients
            .Where(candidate => candidate.Id == bookingActor.PatientId)
            .Select(candidate => candidate.UserId)
            .FirstAsync(cancellationToken);

        // The LGPD gate (design B12). Change 2 grants this consent at just-in-time provisioning and
        // P7 lets a patient revoke it, so until now revocation was possible with nothing checking
        // it. The version comparison is included deliberately: it is the mechanism a versioned
        // consent exists for, and a gate that ignored it would make Consent.Version decoration.
        //
        // booking-desk: it reads the PATIENT'S consent, never the actor's, so it binds a staff
        // booking exactly as it binds a patient's own. Exempting reception would let the clinic
        // route around a patient's withdrawal by telephoning the desk, which is the wrong way
        // round — the gate is about whose data is processed, not about who is typing.
        var consented = await database.Consents.AnyAsync(
            consent => consent.UserId == patientUserId
                && consent.Type == ConsentType.DataProcessing
                && consent.RevokedAtUtc == null
                && consent.Version == auth.Value.ConsentVersion,
            cancellationToken);

        if (!consented)
        {
            return ApiError.Result(
                ErrorCodes.ConsentRequired,
                StatusCodes.Status422UnprocessableEntity,
                new Dictionary<string, object?> { ["type"] = nameof(ConsentType.DataProcessing) });
        }

        var appointmentType = await database.AppointmentTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                type => type.Id == appointmentTypeId && type.DeactivatedAtUtc == null,
                cancellationToken);

        if (appointmentType is null)
        {
            return CatalogRefusals.NotFound();
        }

        var professionalExists = await database.Professionals.AnyAsync(
            professional => professional.Id == professionalId && professional.DeactivatedAtUtc == null,
            cancellationToken);

        if (!professionalExists)
        {
            // Distinct from "named a professional who is not qualified for this type", which is
            // booking.specialty_mismatch. This is a reference that does not resolve at all.
            return CatalogRefusals.NotFound();
        }

        // The date the requested instant falls on, in clinic terms — the window the solver needs.
        // One day, because Explain is asking about one start; using the requested instant's own
        // date rather than a caller-supplied one means a client cannot widen the search.
        var date = startsAt.InZone(timezone.Zone).Date;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CommitAsync(
                    database,
                    reader,
                    timezone,
                    scheduling,
                    clock,
                    bookingActor,
                    professionalId,
                    appointmentType,
                    startsAt,
                    date,
                    cancellationToken);
            }
            catch (BookingRuleViolationException refusal)
            {
                // The aggregate's own rules. Reachable even though Explain already checked the same
                // ground, because the aggregate is the layer that makes an invalid appointment
                // impossible to CONSTRUCT — including from booking-lifecycle's future write path
                // (design B2).
                return refusal.Reason.ToResult();
            }
            catch (Exception failure) when (failure.RacedOn() is { } raced)
            {
                // The genuine race: the pre-commit checks passed and another transaction committed
                // in between. This is the constraint doing the job the application check cannot,
                // and the constraint's name says which invariant it was.
                return raced.ToResult();
            }
            catch (Exception failure) when (attempt < ConcurrencyAttempts && failure.IsConcurrencyRollback())
            {
                // A deadlock, not a business outcome — see BookingRefusals.IsConcurrencyRollback.
                // The transaction was rolled back entirely, so the retry re-reads committed state
                // and produces the correct specific answer instead of a guess.
            }
        }
    }

    /// <summary>
    /// How many times the transactional part may be attempted.
    /// </summary>
    /// <remarks>
    /// Three, which is one more than the two-transaction cycle needs. A deadlock kills one victim
    /// and the winner commits, so a single retry resolves the pair; the third attempt is headroom
    /// for a three-way pile-up on one slot. Persisting past that is not a business outcome and is
    /// reported as an unexpected failure rather than dressed up as one.
    /// </remarks>
    private const int ConcurrencyAttempts = 3;

    /// <summary>
    /// One transactional attempt: lock, load, ask the domain, insert, commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted so the retry above can run it again from a clean transaction. Everything before it
    /// — validating the request, resolving the patient, the consent gate, resolving the references —
    /// is deliberately outside, because none of it can be invalidated by a concurrent booking and
    /// re-running it would turn one refusal into two round trips.
    /// </para>
    /// <para>
    /// <b>The ordering inside is load-bearing.</b> The professional lock comes first, before the read
    /// it protects, because the race being closed is read-then-write across two tables — this path
    /// reads blocks, and the block path reads appointments. A lock acquired after the read serializes
    /// nothing, and no functional test would notice (design B7).
    /// </para>
    /// </remarks>
    private static async Task<IResult> CommitAsync(
        ClinicDbContext database,
        ScheduleReader reader,
        ClinicTimezone timezone,
        ClinicScheduling scheduling,
        TimeProvider clock,
        BookingActor bookingActor,
        Guid professionalId,
        AppointmentType appointmentType,
        Instant startsAt,
        LocalDate date,
        CancellationToken cancellationToken)
    {
        var patientId = bookingActor.PatientId;

        // A fresh context state per attempt: a retry must not re-submit the entity the failed
        // attempt added, which would insert twice on success.
        database.ChangeTracker.Clear();

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        await ScheduleMutation.TakeProfessionalLockAsync(database, professionalId, cancellationToken);

        var loaded = await reader.ReadAsync(appointmentType, date, date, professionalId, cancellationToken);

        if (!loaded.DurationsByProfessional.TryGetValue(professionalId, out var durationMinutes))
        {
            // I2, and reachable in practice: a qualification cleared between the search and the
            // confirmation. The reader's eligibility join IS the gate, so absence is the answer.
            return BookingRefusal.SpecialtyMismatch.ToResult();
        }

        var verdict = AvailabilitySolver.Explain(loaded.Inputs, startsAt);

        if (verdict.ResourceId is not { } resourceId)
        {
            // Every refusal the read's own rules can produce, named by the walk that produced it
            // rather than guessed at here (design B1).
            return verdict.Refusal!.Value.ToResult();
        }

        var range = TimeRange.Between(startsAt, startsAt + Duration.FromMinutes(durationMinutes));

        // I6, checked here rather than in the solver: the solver answers "when is this professional
        // free" and folding a patient's other appointments into it would make an availability read
        // depend on who is asking. The constraint is the floor; this is what turns it into a
        // sentence.
        if (await reader.PatientIsBusyAsync(patientId, range, cancellationToken))
        {
            return BookingRefusal.PatientBusy.ToResult();
        }

        if (!loaded.ResourceTypeByResource.TryGetValue(resourceId, out var resourceTypeId))
        {
            // The solver only ever returns a room from the candidate set the reader built, so this
            // is unreachable. Guarded because the alternative is passing a default Guid into an
            // invariant check and having I3 compare two wrong values.
            throw new InvalidOperationException(
                $"Resource {resourceId} was assigned but is not in the candidate set.");
        }

        var appointment = Appointment.Book(
            new AppointmentBooking(
                patientId,
                professionalId,
                resourceId,
                appointmentType.Id,
                startsAt,
                durationMinutes,

                // True by construction — the duration was found above — but passed as the fact it
                // is, so the aggregate enforces I2 itself rather than trusting this caller.
                ProfessionalHoldsDurationForType: true,
                resourceTypeId,
                appointmentType.RequiredResourceTypeId,

                // FrontDesk for reception, SelfService for the patient — derived from the path
                // rather than declared by the caller, which is why the request carries no such
                // field. The first write of a value 5a shipped and left unused.
                bookingActor.Source),
            scheduling.Parameters,
            Instant.FromDateTimeOffset(clock.GetUtcNow()),
            clock.GetUtcNow());

        database.Appointments.Add(appointment);

        // EF performs the insert; the exclusion constraints protect the row whichever client issues
        // it, and the aggregate is the write model (Decision L, design B5).
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Reception is told the room; a patient is not (design N5, D7). Two shapes rather than one
        // with a conditional field, so the rule lives in the type rather than in a branch.
        return bookingActor.IsClinic
            ? Results.Ok(DescribeForStaff(appointment, loaded.ResourceNames, timezone))
            : Results.Ok(Describe(appointment, timezone));
    }

    /// <summary>
    /// An ISO-8601 UTC instant, or null.
    /// </summary>
    /// <remarks>
    /// <c>InstantPattern.ExtendedIso</c>, which is exactly the format the availability response
    /// emits — so the value a client sends back is the value it was given, round-tripped rather
    /// than reconstructed.
    /// </remarks>
    private static Instant? ParseInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parsed = InstantPattern.ExtendedIso.Parse(value);

        return parsed.Success ? parsed.Value : null;
    }

    private static StaffAppointmentResponse DescribeForStaff(
        Appointment appointment,
        IReadOnlyDictionary<Guid, string> resourceNames,
        ClinicTimezone timezone) =>
        new(
            appointment.Id,
            appointment.PatientId,
            appointment.ProfessionalId,
            appointment.AppointmentTypeId,
            appointment.ResourceId,
            resourceNames.TryGetValue(appointment.ResourceId, out var name) ? name : string.Empty,
            InstantPattern.ExtendedIso.Format(appointment.StartsAt),
            InstantPattern.ExtendedIso.Format(appointment.EndsAt),
            appointment.Status.ToString(),
            timezone.Id);

    private static AppointmentResponse Describe(Appointment appointment, ClinicTimezone timezone) =>
        new(
            appointment.Id,
            appointment.ProfessionalId,
            appointment.AppointmentTypeId,
            InstantPattern.ExtendedIso.Format(appointment.StartsAt),
            InstantPattern.ExtendedIso.Format(appointment.EndsAt),
            appointment.Status.ToString(),
            timezone.Id);
}
