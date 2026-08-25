using System.Security.Claims;
using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Scheduling;
using Clinic.Api.Infrastructure.Time;
using Clinic.Domain;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Clinic.Api.Features.Booking;

/// <summary>
/// P5 and P6 — a patient changes an appointment they already have (spec: booking; design C1–C11).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is new here is not machinery but ordering and authority.</b> No new dependency, no new
/// lock, no new constraint. Two things in this file are correctness properties rather than style
/// choices, and both are invisible when wrong:
/// </para>
/// <para>
/// 1. <b>The reschedule's statement order</b> (design C2, <c>02-domain-model.md</c> §5). The old
/// row must leave the partial exclusion indexes <em>before</em> the new one joins. See
/// <see cref="RescheduleAsync"/>.
/// </para>
/// <para>
/// 2. <b>The row lock on both transitions</b> (design C8). The exclusion constraints police
/// overlap <em>between</em> rows; nothing in the schema polices the lifecycle of <em>one</em>. A
/// cancel and a reschedule racing on the same appointment would otherwise both pass the
/// aggregate's guard against the same snapshot, and the patient would end up cancelled and booked.
/// </para>
/// <para>
/// <b>What a patient is told about an appointment that is not theirs</b> (design C6): the same
/// thing they are told about one that never existed — <c>auth.ownership_denied</c>. The catalogue
/// settled that shape for patient records and the reasoning is unchanged: a 404 here would be an
/// oracle for which appointment ids are real.
/// </para>
/// <para>
/// <b><c>booking-desk</c> widened the two write paths to admit reception, and changed nothing
/// else</b> (design N2). Staff share these handlers rather than getting mirrors of them, because
/// the statement ordering below is a correctness property with a silent failure mode and a second
/// implementation is a second place to get it wrong. Everything that differs between the two
/// callers — whose appointment, whether the cutoff binds, and whether an unknown id is a 404 or a
/// 403 — comes from <see cref="BookingActor"/>, one branch shared with the booking path.
/// </para>
/// <para>
/// So <c>booking.appointment_not_found</c> is now reachable, and only from the staff branch. A
/// patient still cannot tell absence from denial.
/// </para>
/// </remarks>
internal static class AppointmentLifecycleEndpoints
{
    internal static IEndpointRouteBuilder MapAppointmentLifecycleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // P5's list stays the patient's own: "my appointments" has no staff reading, and the
        // day view (S1/S4) is a different question with a different shape and its own access log.
        endpoints.MapGet("/api/appointments", ListAsync)
            .RequireAuthorization(AuthorizationPolicies.Patient)
            .WithName("ListMyAppointments");

        // The two writes admit reception as well (design N2). A professional is refused: changing
        // an appointment is reception's work, and a clinician who could would be a second route to
        // the same transition with nothing here expecting them.
        endpoints.MapPost("/api/appointments/{id:guid}/cancel", CancelAsync)
            .RequireAuthorization(AuthorizationPolicies.PatientOrClinicStaff)
            .WithName("CancelAppointment");

        endpoints.MapPost("/api/appointments/{id:guid}/reschedule", RescheduleAsync)
            .RequireAuthorization(AuthorizationPolicies.PatientOrClinicStaff)
            .WithName("RescheduleAppointment");

        return endpoints;
    }

    /// <summary>
    /// P5's data — the caller's own appointments, and whether each may still be changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>CanChange</c> is computed here and not in the browser</b> (design C10). The browser's
    /// clock is not the clinic's, is user-settable, and is exactly the class of bug that passes
    /// every test in this repository — the whole suite runs in one process with one notion of
    /// local time. A screen that computed the cutoff locally could show an enabled button the
    /// server will refuse, and the point of P5 showing the rule is that the shown rule is the
    /// enforced one.
    /// </para>
    /// <para>
    /// Split by <em>time</em> rather than by status, with terminal appointments annotated where
    /// they fall. "What happened to my 3pm?" is the question a patient actually asks, and an
    /// appointment that was cancelled last week belongs where they would look for it. Recorded as
    /// design Open Question 1 — the validation guide collects a human opinion on it.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal actor,
        ClinicDbContext database,
        ClinicTimezone timezone,
        ClinicScheduling scheduling,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var patient = await CallerAsync(actor, database, cancellationToken);

        if (patient is null)
        {
            return ApiError.Result(ErrorCodes.PatientNotFound, StatusCodes.Status404NotFound);
        }

        var now = Instant.FromDateTimeOffset(clock.GetUtcNow());

        var appointments = await database.Appointments
            .AsNoTracking()
            .Where(appointment => appointment.PatientId == patient.Id)
            .ToListAsync(cancellationToken);

        var described = appointments
            .Select(appointment => Describe(appointment, scheduling.CancellationCutoff, now))
            .ToList();

        return Results.Ok(new MyAppointmentsResponse(
            described.Where(entry => entry.IsUpcoming).OrderBy(entry => entry.StartsAt).ToList(),
            described.Where(entry => !entry.IsUpcoming).OrderByDescending(entry => entry.StartsAt).ToList(),
            timezone.Id));
    }

    /// <summary>
    /// Calls an appointment off, freeing the time it held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No advisory lock, deliberately</b> (design C8). The domain-model G1 lock serializes a
    /// read-then-write across two tables — booking reads blocks, block creation reads
    /// appointments. This path reads nothing and can create no overlap: it only <em>removes</em>
    /// a row from three partial indexes. A lock here would be cargo-cult serialization on a path
    /// that cannot race what the lock protects.
    /// </para>
    /// <para>
    /// <b>No consent gate, also deliberately</b> (design C11). Booking has one, and applying it
    /// here by reflex would trap a patient in an appointment as a consequence of exercising a
    /// right over their own data — refusing to let somebody <em>leave</em> because they withdrew
    /// consent to processing is the wrong way round, and a cancel reduces what the clinic holds.
    /// </para>
    /// </remarks>
    private static async Task<IResult> CancelAsync(
        Guid id,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        ClinicScheduling scheduling,
        ClinicTimezone timezone,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var resolved = await BookingActor.ForLifecycleAsync(actor, database, cancellationToken);

        if (resolved.Actor is not { } bookingActor)
        {
            return resolved.Refusal!;
        }

        database.ChangeTracker.Clear();

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var appointment = await LockAsync(database, id, bookingActor, cancellationToken);

        if (appointment is null)
        {
            return bookingActor.CannotReach();
        }

        try
        {
            appointment.Cancel(
                scheduling.CancellationCutoff,
                Instant.FromDateTimeOffset(clock.GetUtcNow()),

                // THE FRONT-DESK OVERRIDE, and it is this one argument (design N1, N4.2). True for
                // a patient, false for reception — the second caller of the authority parameter 5b
                // built and the first ever to pass false. AppointmentLifecycleTests.cs:258 wrote
                // down what happens on that side before any caller existed; nothing in the domain
                // or in that test changed to make this work.
                bookingActor.CutoffApplies);
        }
        catch (BookingRuleViolationException refusal)
        {
            return refusal.Reason.ToResult();
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.Ok(Describe(appointment, scheduling.CancellationCutoff, Instant.FromDateTimeOffset(clock.GetUtcNow()))
            .ToAppointmentResponse(timezone));
    }

    /// <summary>
    /// Moves an appointment to a new time with the same professional.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The request carries an instant and nothing else</b> (design C3). No professional, no
    /// appointment type, no room — so "a reschedule keeps the same professional" is structural
    /// rather than validated, the same shape that made server-side room assignment structural in
    /// 5a. Moving to a different professional is a cancellation followed by a new booking, through
    /// the two paths that already exist.
    /// </para>
    /// <para>
    /// The useful consequence is that only <em>one</em> professional's advisory lock is ever
    /// needed. A cross-professional reschedule would need two, and two transactions taking
    /// <c>{A,B}</c> and <c>{B,A}</c> deadlock — a problem this path does not have rather than one
    /// it solves.
    /// </para>
    /// </remarks>
    private static async Task<IResult> RescheduleAsync(
        Guid id,
        RescheduleAppointmentRequest request,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        ScheduleReader reader,
        ClinicTimezone timezone,
        ClinicScheduling scheduling,
        TimeProvider clock,
        IOptions<AuthOptions> auth,
        CancellationToken cancellationToken)
    {
        if (ParseInstant(request.StartsAt) is not { } startsAt)
        {
            return CatalogRefusals.Invalid(nameof(request.StartsAt));
        }

        var resolved = await BookingActor.ForLifecycleAsync(actor, database, cancellationToken);

        if (resolved.Actor is not { } bookingActor)
        {
            return resolved.Refusal!;
        }

        // Whose appointment this is decides whose consent is read — for a staff caller that is the
        // patient's, resolved from the appointment below rather than from the session. Loaded here
        // for the patient path, where the two are the same.
        var patientUserId = await database.Appointments
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Join(database.Patients, candidate => candidate.PatientId, p => p.Id, (_, p) => (Guid?)p.UserId)
            .FirstOrDefaultAsync(cancellationToken) ?? actor.UserId();

        // The LGPD gate, and the one asymmetry with cancel (design C11). A reschedule CREATES an
        // appointment, so it passes through the same gate a booking does; a cancel does not,
        // because withdrawing from a service must not be blocked by having withdrawn consent.
        //
        // It reads the PATIENT'S consent on both paths. Exempting reception would let the clinic
        // move a patient who has withdrawn consent to processing, which is the wrong way round.
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

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CommitRescheduleAsync(
                    database, reader, timezone, scheduling, clock, bookingActor, id, startsAt, cancellationToken);
            }
            catch (BookingRuleViolationException refusal)
            {
                return refusal.Reason.ToResult();
            }
            catch (Exception failure) when (failure.RacedOn() is { } raced)
            {
                // The genuine race: the checks passed and another transaction committed in
                // between. The constraint doing the job the application check cannot.
                return raced.ToResult();
            }
            catch (Exception failure) when (attempt < ConcurrencyAttempts && failure.IsConcurrencyRollback())
            {
                // A deadlock, not a business outcome — the transaction was rolled back entirely,
                // so a retry re-reads committed state and produces the correct specific answer.
            }
        }
    }

    /// <summary>Matches the booking path's allowance, and for the same reason.</summary>
    private const int ConcurrencyAttempts = 3;

    /// <summary>
    /// One transactional attempt: lock, load, ask the solver, transition, insert, commit.
    /// </summary>
    private static async Task<IResult> CommitRescheduleAsync(
        ClinicDbContext database,
        ScheduleReader reader,
        ClinicTimezone timezone,
        ClinicScheduling scheduling,
        TimeProvider clock,
        BookingActor bookingActor,
        Guid appointmentId,
        Instant startsAt,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        // Reachability before anything expensive, and without the lock — a caller who cannot reach
        // this appointment learns nothing about it, including how long it took to say no. For a
        // patient that means ownership; for reception it means existence, which is the whole of
        // the difference between the two roles on this path.
        var owned = await Reachable(database.Appointments.AsNoTracking(), appointmentId, bookingActor)
            .Select(candidate => new { candidate.ProfessionalId, candidate.PatientId })
            .FirstOrDefaultAsync(cancellationToken);

        if (owned is null)
        {
            return bookingActor.CannotReach();
        }

        // The appointment's own patient, not the actor's. On the patient path these are the same
        // value; on the staff path the actor has no patient record at all.
        var patientId = owned.PatientId;

        // FIRST, before the read it protects. This path INSERTS, so it races the block-creation
        // path exactly as a booking does, and a lock taken after the load serializes nothing
        // (domain-model G1, 5a's design B7).
        await ScheduleMutation.TakeProfessionalLockAsync(database, owned.ProfessionalId, cancellationToken);

        // Now the row itself, FOR UPDATE (design C8). This is the lock that stops a concurrent
        // cancel and reschedule from both passing the aggregate's guard against the same
        // snapshot — the race no exclusion constraint can see, because it is about one row's
        // lifecycle rather than about overlap between rows.
        var appointment = await LockAsync(database, appointmentId, bookingActor, cancellationToken);

        if (appointment is null)
        {
            return bookingActor.CannotReach();
        }

        var appointmentType = await database.AppointmentTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                type => type.Id == appointment.AppointmentTypeId && type.DeactivatedAtUtc == null,
                cancellationToken);

        if (appointmentType is null)
        {
            // The type was retired since the booking. Not a reschedule anyone can complete, and
            // the same answer a booking naming it would get.
            return CatalogRefusals.NotFound();
        }

        var date = startsAt.InZone(timezone.Zone).Date;

        // The appointment being moved is EXCLUDED from the busy set (design C7). At this instant
        // it is still Scheduled, so without this a near move would tell the patient that their own
        // outgoing appointment blocks their new one — for the professional, for the room, and for
        // themselves.
        var loaded = await reader.ReadAsync(
            appointmentType,
            date,
            date,
            appointment.ProfessionalId,
            cancellationToken,
            excludingAppointmentId: appointment.Id);

        if (!loaded.DurationsByProfessional.TryGetValue(appointment.ProfessionalId, out var durationMinutes))
        {
            // I2, rechecked against the move rather than inherited from the original booking: a
            // qualification cleared in between must stop it.
            return BookingRefusal.SpecialtyMismatch.ToResult();
        }

        // The same walk the availability read uses, from the same loading step. Nothing here
        // re-implements "is this slot offerable" — that is 5a's design B1, and a third caller
        // inherits it for free.
        var verdict = AvailabilitySolver.Explain(loaded.Inputs, startsAt);

        if (verdict.ResourceId is not { } resourceId)
        {
            return verdict.Refusal!.Value.ToResult();
        }

        var range = TimeRange.Between(startsAt, startsAt + Duration.FromMinutes(durationMinutes));

        if (await reader.PatientIsBusyAsync(patientId, range, cancellationToken, excludingAppointmentId: appointment.Id))
        {
            return BookingRefusal.PatientBusy.ToResult();
        }

        if (!loaded.ResourceTypeByResource.TryGetValue(resourceId, out var resourceTypeId))
        {
            throw new InvalidOperationException(
                $"Resource {resourceId} was assigned but is not in the candidate set.");
        }

        var now = Instant.FromDateTimeOffset(clock.GetUtcNow());

        var replacement = appointment.RescheduleTo(
            new AppointmentBooking(
                patientId,
                appointment.ProfessionalId,
                resourceId,
                appointment.AppointmentTypeId,
                startsAt,
                durationMinutes,
                ProfessionalHoldsDurationForType: true,
                resourceTypeId,
                appointmentType.RequiredResourceTypeId,

                // The ORIGINAL appointment's source, carried onto its replacement. A reschedule
                // does not change where an appointment came from: one booked at the desk that
                // reception then moves is still an appointment the clinic made.
                appointment.Source),
            scheduling.Parameters,
            scheduling.CancellationCutoff,
            now,
            clock.GetUtcNow(),

            // The override again, on the other transition it applies to (design N1).
            bookingActor.CutoffApplies);

        // ─────────────────────────────────────────────────────────────────────────────────────
        //  THE STATEMENT ORDER BELOW IS A CORRECTNESS PROPERTY, NOT A STYLE CHOICE (design C2).
        //
        //  The three EXCLUDE constraints are partial (`WHERE status = 'Scheduled'`) and were
        //  created WITHOUT `DEFERRABLE`, so PostgreSQL evaluates them at the end of each
        //  STATEMENT rather than at commit.
        //
        //  So the UPDATE that takes the old row to `Rescheduled` — removing it from all three
        //  partial indexes — must be flushed BEFORE the INSERT of its replacement. Reversed, the
        //  insert is checked against a row that is still live and
        //  `appointments_patient_no_overlap` fires: a same-patient near move ALWAYS fails.
        //
        //  A test that moves an appointment to next week passes either way, because the two
        //  ranges do not overlap. Only a near move catches it. See the integration tier, where
        //  the few-minute delta is the assertion rather than a fixture value.
        //
        //  Two SaveChanges rather than one, inside one transaction: EF orders inserts before
        //  updates within a single call, which is exactly the wrong order here.
        // ─────────────────────────────────────────────────────────────────────────────────────
        await database.SaveChangesAsync(cancellationToken);

        database.Appointments.Add(replacement);
        await database.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return Results.Ok(Describe(replacement, scheduling.CancellationCutoff, now).ToAppointmentResponse(timezone));
    }

    /// <summary>
    /// Loads the caller's own appointment and holds its row for the rest of the transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FOR UPDATE</c>, and it is the whole of design C8. The aggregate's liveness guard is
    /// correct and is evaluated against a snapshot — so two transactions reading the same
    /// <c>Scheduled</c> row both pass it, and a cancel and a reschedule can both commit. The
    /// patient cancelled and ended up with an appointment. Nothing in the schema prevents that:
    /// the exclusion constraints police overlap <em>between</em> rows, not the lifecycle of one.
    /// </para>
    /// <para>
    /// The reachability filter is in the same statement rather than checked afterwards, so a
    /// caller cannot take a lock on a row they may not reach. For a patient that filter is their
    /// own patient id; for reception there is none, because there is no appointment reception may
    /// not act on — which is why the parameter is bound rather than interpolated either way.
    /// </para>
    /// </remarks>
    private static async Task<Appointment?> LockAsync(
        ClinicDbContext database,
        Guid appointmentId,
        BookingActor bookingActor,
        CancellationToken cancellationToken)
    {
        var (connection, dbTransaction) = await ScheduleMutation.EnlistAsync(database, cancellationToken);

        // One statement with a parameterised predicate rather than two SQL strings: @patientId is
        // null for reception, and `(@patientId is null or patient_id = @patientId)` is the same
        // filter the LINQ path above expresses. Two literals would be two places to forget one.
        var patientId = bookingActor.IsClinic ? (Guid?)null : bookingActor.PatientId;

        var locked = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            select id
            from appointments
            where id = @appointmentId
              and (@patientId::uuid is null or patient_id = @patientId)
            for update
            """,
            new { appointmentId, patientId },
            dbTransaction,
            cancellationToken: cancellationToken));

        if (locked is null)
        {
            return null;
        }

        // Tracked, because the transition mutates it and EF issues the UPDATE. The row is already
        // held by the statement above, so this read cannot see a version somebody else is
        // changing.
        return await database.Appointments.FirstAsync(
            appointment => appointment.Id == locked, cancellationToken);
    }

    private static async Task<Patient?> CallerAsync(
        ClaimsPrincipal actor,
        ClinicDbContext database,
        CancellationToken cancellationToken) =>
        await database.Patients.FirstOrDefaultAsync(
            candidate => candidate.UserId == actor.UserId() && candidate.DeletedAtUtc == null,
            cancellationToken);

    /// <summary>
    /// Narrows a query to the appointments this actor may act on.
    /// </summary>
    /// <remarks>
    /// A patient reaches their own and nothing else; reception reaches any, there being no
    /// appointment the desk may not run. Expressed once so that the two write paths cannot come to
    /// disagree about it, and so that "reception is unfiltered" is a visible decision rather than
    /// a missing <c>Where</c> somewhere.
    /// </remarks>
    private static IQueryable<Appointment> Reachable(
        IQueryable<Appointment> appointments,
        Guid appointmentId,
        BookingActor bookingActor) =>
        bookingActor.IsClinic
            ? appointments.Where(candidate => candidate.Id == appointmentId)
            : appointments.Where(candidate =>
                candidate.Id == appointmentId && candidate.PatientId == bookingActor.PatientId);

    private static MyAppointment Describe(
        Appointment appointment,
        CancellationCutoffPolicy cutoff,
        Instant now) =>
        new(
            appointment.Id,
            appointment.ProfessionalId,
            appointment.AppointmentTypeId,
            InstantPattern.ExtendedIso.Format(appointment.StartsAt),
            InstantPattern.ExtendedIso.Format(appointment.EndsAt),
            appointment.Status.ToString(),

            // Both halves, because both make it unchangeable and a screen needs neither to
            // distinguish them: a terminal appointment has nothing left to change, and a live one
            // inside the cutoff has a rule in the way.
            CanChange: appointment.IsLive && cutoff.Permits(appointment.StartsAt, now, cutoffApplies: true),
            IsUpcoming: appointment.EndsAt > now);

    private static Instant? ParseInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parsed = InstantPattern.ExtendedIso.Parse(value);

        return parsed.Success ? parsed.Value : null;
    }
}
