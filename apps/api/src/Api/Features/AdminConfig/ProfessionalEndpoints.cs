using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Time;
using Clinic.Domain;
using Clinic.Domain.Configuration;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.AdminConfig;

/// <summary>
/// S7 — a professional's clinical configuration (spec: clinic-configuration, professional half).
/// </summary>
/// <remarks>
/// <para>
/// Every route is keyed by <c>userId</c> rather than by a professional id, because the
/// configuration record may not exist yet (design E1). <see cref="ResolveAsync"/> is where that
/// asymmetry lives: a read tolerates its absence, a write creates it.
/// </para>
/// <para>
/// The pattern each write follows is the one 3a established — the slice gathers the facts, the
/// domain decides what they mean. Here the facts are "does this professional hold the specialty"
/// and "what segments already exist", and getting either query's active-predicate wrong is how
/// this slice would break while every unit test still passed.
/// </para>
/// </remarks>
internal static class ProfessionalEndpoints
{
    internal static IEndpointRouteBuilder MapProfessionalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/config/professionals")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/", ListAsync).WithName("ListProfessionals");
        group.MapGet("/{userId:guid}", DetailAsync).WithName("GetProfessional");

        group.MapPost("/{userId:guid}/specialties", GrantSpecialtyAsync).WithName("GrantSpecialty");
        group.MapPost("/{userId:guid}/specialties/{specialtyId:guid}/revoke", RevokeSpecialtyAsync)
            .WithName("RevokeSpecialty");

        group.MapPut("/{userId:guid}/durations", SetDurationAsync).WithName("SetDuration");
        group.MapPost("/{userId:guid}/durations/{appointmentTypeId:guid}/clear", ClearDurationAsync)
            .WithName("ClearDuration");

        group.MapPost("/{userId:guid}/working-hours", DefineWorkingHoursAsync).WithName("DefineWorkingHours");
        group.MapPost("/{userId:guid}/working-hours/{segmentId:guid}/retire", RetireWorkingHoursAsync)
            .WithName("RetireWorkingHours");

        group.MapPost("/{userId:guid}/exceptions", DefineExceptionAsync).WithName("DefineException");
        group.MapPost("/{userId:guid}/exceptions/{exceptionId:guid}/retire", RetireExceptionAsync)
            .WithName("RetireException");

        return endpoints;
    }

    /// <summary>
    /// Every invited professional, configured or not (design E1).
    /// </summary>
    /// <remarks>
    /// Driven from <c>Users</c> rather than from <c>Professionals</c>, which is the whole point:
    /// listing configuration records would hide exactly the people an administrator opened this
    /// screen to configure.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var professionals = await database.Users
            .AsNoTracking()
            .Where(user => user.Role == Role.Professional && user.DeletedAtUtc == null)
            .OrderBy(user => user.Email)
            .Select(user => new
            {
                user.Id,
                user.Email,
                user.ExternalSubjectId,
                Record = database.Professionals
                    .Where(record => record.UserId == user.Id && record.DeactivatedAtUtc == null)
                    .Select(record => (Guid?)record.Id)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var ids = professionals.Where(p => p.Record != null).Select(p => p.Record!.Value).ToList();

        // Counted in one round trip each rather than per row, so the list does not fan out.
        var specialtyCounts = await CountByProfessionalAsync(
            database.ProfessionalSpecialties.Where(x => x.DeactivatedAtUtc == null && ids.Contains(x.ProfessionalId))
                .Select(x => x.ProfessionalId),
            cancellationToken);

        var durationCounts = await CountByProfessionalAsync(
            database.ProfessionalAppointmentTypes.Where(x => x.DeactivatedAtUtc == null && ids.Contains(x.ProfessionalId))
                .Select(x => x.ProfessionalId),
            cancellationToken);

        var hoursCounts = await CountByProfessionalAsync(
            database.WorkingHoursTemplates.Where(x => x.DeactivatedAtUtc == null && ids.Contains(x.ProfessionalId))
                .Select(x => x.ProfessionalId),
            cancellationToken);

        var entries = professionals.Select(p => new ProfessionalListEntry(
            p.Id,
            p.Email,
            IsConfigured: p.Record != null,
            AwaitsClaim: p.ExternalSubjectId == null,
            IsActive: true,
            SpecialtyCount: Count(specialtyCounts, p.Record),
            DurationCount: Count(durationCounts, p.Record),
            WorkingHoursCount: Count(hoursCounts, p.Record))).ToList();

        return Results.Ok(entries);
    }

    private static async Task<IResult> DetailAsync(
        Guid userId,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var user = await FindProfessionalUserAsync(database, userId, cancellationToken);

        if (user is null)
        {
            return CatalogRefusals.NotFound();
        }

        var record = await database.Professionals
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.DeactivatedAtUtc == null, cancellationToken);

        if (record is null)
        {
            // Not an error: an invited professional nobody has configured yet is an ordinary
            // state, and the screen needs to render it rather than a 404.
            return Results.Ok(new ProfessionalDetail(
                userId, user.Email, IsConfigured: false, AwaitsClaim: user.ExternalSubjectId is null,
                Specialties: [], Durations: [], WorkingHours: [], Exceptions: []));
        }

        var specialties = await database.ProfessionalSpecialties
            .AsNoTracking()
            .Where(x => x.ProfessionalId == record.Id && x.DeactivatedAtUtc == null)
            .Join(database.Specialties, x => x.SpecialtyId, s => s.Id, (x, s) => new { s.Id, s.Name })
            .OrderBy(x => x.Name)
            .Select(x => new HeldSpecialty(x.Id, x.Name))
            .ToListAsync(cancellationToken);

        var durations = await database.ProfessionalAppointmentTypes
            .AsNoTracking()
            .Where(x => x.ProfessionalId == record.Id && x.DeactivatedAtUtc == null)
            .Join(database.AppointmentTypes, x => x.AppointmentTypeId, t => t.Id, (x, t) => new { x.DurationMinutes, t })
            .Join(database.Specialties, x => x.t.SpecialtyId, s => s.Id, (x, s) => new { x.DurationMinutes, x.t, s })
            .OrderBy(x => x.s.Name).ThenBy(x => x.t.Name)
            .Select(x => new ConfiguredDuration(x.t.Id, x.t.Name, x.s.Id, x.s.Name, x.DurationMinutes))
            .ToListAsync(cancellationToken);

        var segments = await database.WorkingHoursTemplates
            .AsNoTracking()
            .Where(x => x.ProfessionalId == record.Id && x.DeactivatedAtUtc == null)
            .OrderBy(x => x.DayOfWeek).ThenBy(x => x.StartTime)
            .ToListAsync(cancellationToken);

        var exceptions = await database.WorkingHoursExceptions
            .AsNoTracking()
            .Where(x => x.ProfessionalId == record.Id && x.DeactivatedAtUtc == null)
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);

        return Results.Ok(new ProfessionalDetail(
            userId,
            user.Email,
            IsConfigured: true,
            AwaitsClaim: user.ExternalSubjectId is null,
            specialties,
            durations,
            segments.Select(x => new WorkingHoursSegment(
                x.Id,
                x.DayOfWeek.ToString(),
                WallClockText.Format(x.StartTime),
                WallClockText.Format(x.EndTime),
                WallClockText.Format(x.EffectiveFrom),
                x.EffectiveTo is { } to ? WallClockText.Format(to) : null)).ToList(),
            exceptions.Select(x => new WorkingHoursOverride(
                x.Id,
                WallClockText.Format(x.Date),
                x.StartTime is { } start ? WallClockText.Format(start) : null,
                x.EndTime is { } end ? WallClockText.Format(end) : null)).ToList()));
    }

    // --- Qualifications --------------------------------------------------------------

    private static async Task<IResult> GrantSpecialtyAsync(
        Guid userId,
        GrantSpecialtyRequest request,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(database, userId, clock, cancellationToken);

        if (resolved is null)
        {
            return CatalogRefusals.NotFound();
        }

        var specialtyIsActive = await database.Specialties.AnyAsync(
            s => s.Id == request.SpecialtyId && s.DeactivatedAtUtc == null, cancellationToken);

        if (!specialtyIsActive)
        {
            return CatalogRefusals.NotFound();
        }

        var existing = await database.ProfessionalSpecialties.FirstOrDefaultAsync(
            x => x.ProfessionalId == resolved.Id && x.SpecialtyId == request.SpecialtyId,
            cancellationToken);

        if (existing is null)
        {
            database.ProfessionalSpecialties.Add(
                ProfessionalSpecialty.Grant(resolved.Id, request.SpecialtyId, clock.GetUtcNow()));
        }
        else
        {
            // Re-granting a revoked qualification restores the original row rather than adding a
            // second one, which is what the partial unique index would refuse anyway.
            existing.Restore();
        }

        await database.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> RevokeSpecialtyAsync(
        Guid userId,
        Guid specialtyId,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var record = await ExistingRecordAsync(database, userId, cancellationToken);

        if (record is null)
        {
            return CatalogRefusals.NotFound();
        }

        var qualification = await database.ProfessionalSpecialties.FirstOrDefaultAsync(
            x => x.ProfessionalId == record.Id && x.SpecialtyId == specialtyId && x.DeactivatedAtUtc == null,
            cancellationToken);

        if (qualification is null)
        {
            return CatalogRefusals.NotFound();
        }

        // The predicate that matters: durations that are active, for appointment types belonging
        // to THIS specialty. Counting all of the professional's durations would refuse a
        // revocation that should succeed.
        var dependents = await database.ProfessionalAppointmentTypes
            .Where(duration => duration.ProfessionalId == record.Id && duration.DeactivatedAtUtc == null)
            .Join(database.AppointmentTypes, duration => duration.AppointmentTypeId, t => t.Id, (duration, t) => t)
            .CountAsync(t => t.SpecialtyId == specialtyId, cancellationToken);

        try
        {
            qualification.Revoke(dependents, clock.GetUtcNow());
            await database.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
    }

    // --- Durations -------------------------------------------------------------------

    private static async Task<IResult> SetDurationAsync(
        Guid userId,
        SetDurationRequest request,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(database, userId, clock, cancellationToken);

        if (resolved is null)
        {
            return CatalogRefusals.NotFound();
        }

        var appointmentType = await database.AppointmentTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Id == request.AppointmentTypeId && t.DeactivatedAtUtc == null,
                cancellationToken);

        if (appointmentType is null)
        {
            return CatalogRefusals.NotFound();
        }

        // The gate's fact (design E2): an ACTIVE qualification for the specialty this
        // appointment type belongs to.
        var holdsSpecialty = await database.ProfessionalSpecialties.AnyAsync(
            x => x.ProfessionalId == resolved.Id
                && x.SpecialtyId == appointmentType.SpecialtyId
                && x.DeactivatedAtUtc == null,
            cancellationToken);

        var existing = await database.ProfessionalAppointmentTypes.FirstOrDefaultAsync(
            x => x.ProfessionalId == resolved.Id && x.AppointmentTypeId == request.AppointmentTypeId,
            cancellationToken);

        try
        {
            if (existing is null)
            {
                database.ProfessionalAppointmentTypes.Add(ProfessionalAppointmentType.Set(
                    resolved.Id,
                    request.AppointmentTypeId,
                    request.DurationMinutes,
                    holdsSpecialty,
                    clock.GetUtcNow()));
            }
            else
            {
                // Editing an existing duration re-checks the gate too: the qualification it
                // relied on may have been revoked since.
                if (!existing.IsActive)
                {
                    existing.Restore(holdsSpecialty);
                }
                else if (!holdsSpecialty)
                {
                    throw new CatalogRuleViolationException(
                        CatalogRefusal.SpecialtyNotHeld,
                        "This professional no longer holds the specialty this appointment type belongs to.");
                }

                existing.ChangeDuration(request.DurationMinutes);
            }

            await database.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
        catch (DomainRuleViolationException)
        {
            return CatalogRefusals.Invalid("durationMinutes");
        }
    }

    private static async Task<IResult> ClearDurationAsync(
        Guid userId,
        Guid appointmentTypeId,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var record = await ExistingRecordAsync(database, userId, cancellationToken);

        if (record is null)
        {
            return CatalogRefusals.NotFound();
        }

        var duration = await database.ProfessionalAppointmentTypes.FirstOrDefaultAsync(
            x => x.ProfessionalId == record.Id
                && x.AppointmentTypeId == appointmentTypeId
                && x.DeactivatedAtUtc == null,
            cancellationToken);

        if (duration is null)
        {
            return CatalogRefusals.NotFound();
        }

        duration.Clear(clock.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    // --- Working hours ---------------------------------------------------------------

    private static async Task<IResult> DefineWorkingHoursAsync(
        Guid userId,
        DefineWorkingHoursRequest request,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(database, userId, clock, cancellationToken);

        if (resolved is null)
        {
            return CatalogRefusals.NotFound();
        }

        var day = WallClockText.ParseDayOfWeek(request.DayOfWeek);
        var start = WallClockText.ParseTime(request.StartTime);
        var end = WallClockText.ParseTime(request.EndTime);
        var from = WallClockText.ParseDate(request.EffectiveFrom);

        if (day is null)
        {
            return CatalogRefusals.Invalid("dayOfWeek");
        }

        if (start is null)
        {
            return CatalogRefusals.Invalid("startTime");
        }

        if (end is null)
        {
            return CatalogRefusals.Invalid("endTime");
        }

        if (from is null)
        {
            return CatalogRefusals.Invalid("effectiveFrom");
        }

        // A supplied-but-unparseable end date is an error; an absent one is an open-ended
        // pattern. Those must not be conflated.
        var to = WallClockText.ParseDate(request.EffectiveTo);

        if (to is null && !string.IsNullOrWhiteSpace(request.EffectiveTo))
        {
            return CatalogRefusals.Invalid("effectiveTo");
        }

        // Active segments only — a retired schedule must not block a new one.
        var existing = await database.WorkingHoursTemplates
            .Where(x => x.ProfessionalId == resolved.Id && x.DeactivatedAtUtc == null)
            .ToListAsync(cancellationToken);

        try
        {
            var segment = WorkingHoursTemplate.Define(
                resolved.Id, day.Value, start.Value, end.Value, from.Value, to, existing, clock.GetUtcNow());

            database.WorkingHoursTemplates.Add(segment);
            await database.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
        catch (DomainRuleViolationException)
        {
            return CatalogRefusals.Invalid("dayOfWeek");
        }
    }

    private static async Task<IResult> RetireWorkingHoursAsync(
        Guid userId,
        Guid segmentId,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var record = await ExistingRecordAsync(database, userId, cancellationToken);

        if (record is null)
        {
            return CatalogRefusals.NotFound();
        }

        var segment = await database.WorkingHoursTemplates.FirstOrDefaultAsync(
            x => x.Id == segmentId && x.ProfessionalId == record.Id, cancellationToken);

        if (segment is null)
        {
            return CatalogRefusals.NotFound();
        }

        segment.Retire(clock.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    // --- Exceptions ------------------------------------------------------------------

    private static async Task<IResult> DefineExceptionAsync(
        Guid userId,
        DefineExceptionRequest request,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(database, userId, clock, cancellationToken);

        if (resolved is null)
        {
            return CatalogRefusals.NotFound();
        }

        var date = WallClockText.ParseDate(request.Date);

        if (date is null)
        {
            return CatalogRefusals.Invalid("date");
        }

        var start = WallClockText.ParseTime(request.StartTime);
        var end = WallClockText.ParseTime(request.EndTime);

        // Both or neither: one time alone is an incomplete span rather than an all-day absence.
        if (start is null != end is null)
        {
            return CatalogRefusals.Invalid(start is null ? "startTime" : "endTime");
        }

        var alreadyCovered = await database.WorkingHoursExceptions.AnyAsync(
            x => x.ProfessionalId == resolved.Id && x.Date == date.Value && x.DeactivatedAtUtc == null,
            cancellationToken);

        try
        {
            WorkingHoursException.EnsureNoneFor(resolved.Id, date.Value, alreadyCovered);

            var exception = start is { } startTime && end is { } endTime
                ? WorkingHoursException.DifferentHours(
                    resolved.Id, date.Value, startTime, endTime, clock.GetUtcNow())
                : WorkingHoursException.Unavailable(resolved.Id, date.Value, clock.GetUtcNow());

            database.WorkingHoursExceptions.Add(exception);
            await database.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
    }

    private static async Task<IResult> RetireExceptionAsync(
        Guid userId,
        Guid exceptionId,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var record = await ExistingRecordAsync(database, userId, cancellationToken);

        if (record is null)
        {
            return CatalogRefusals.NotFound();
        }

        var exception = await database.WorkingHoursExceptions.FirstOrDefaultAsync(
            x => x.Id == exceptionId && x.ProfessionalId == record.Id, cancellationToken);

        if (exception is null)
        {
            return CatalogRefusals.NotFound();
        }

        exception.Retire(clock.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    // --- The E1 seam -----------------------------------------------------------------

    /// <summary>
    /// Resolves the configuration record for a user, creating it on first use.
    /// </summary>
    /// <remarks>
    /// This is design E1 in one method: writes go through here, so "created on first save" is a
    /// property of the slice rather than something each handler remembers. Returns null when the
    /// user does not exist or does not hold the professional role, which the caller answers with
    /// <c>config.not_found</c>.
    ///
    /// It does not call <c>SaveChanges</c> — the caller's save commits the new record together
    /// with whatever prompted it, so a refused write leaves no orphan record behind.
    /// </remarks>
    private static async Task<Professional?> ResolveAsync(
        ClinicDbContext database,
        Guid userId,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await FindProfessionalUserAsync(database, userId, cancellationToken) is null)
        {
            return null;
        }

        var existing = await database.Professionals.FirstOrDefaultAsync(
            p => p.UserId == userId && p.DeactivatedAtUtc == null, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var created = Professional.ForUser(userId, clock.GetUtcNow());
        database.Professionals.Add(created);

        return created;
    }

    /// <summary>The record, without creating one — for reads and for retiring what exists.</summary>
    private static Task<Professional?> ExistingRecordAsync(
        ClinicDbContext database,
        Guid userId,
        CancellationToken cancellationToken) =>
        database.Professionals.FirstOrDefaultAsync(
            p => p.UserId == userId && p.DeactivatedAtUtc == null, cancellationToken);

    /// <summary>
    /// The user, only if they are a professional.
    /// </summary>
    /// <remarks>
    /// Checking the role here is what stops S7 from being a back door into configuring a patient
    /// or an administrator as though they saw patients.
    /// </remarks>
    private static Task<User?> FindProfessionalUserAsync(
        ClinicDbContext database,
        Guid userId,
        CancellationToken cancellationToken) =>
        database.Users.AsNoTracking().FirstOrDefaultAsync(
            user => user.Id == userId && user.Role == Role.Professional && user.DeletedAtUtc == null,
            cancellationToken);

    private static async Task<Dictionary<Guid, int>> CountByProfessionalAsync(
        IQueryable<Guid> professionalIds,
        CancellationToken cancellationToken)
    {
        var grouped = await professionalIds
            .GroupBy(id => id)
            .Select(group => new { ProfessionalId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return grouped.ToDictionary(x => x.ProfessionalId, x => x.Count);
    }

    private static int Count(Dictionary<Guid, int> counts, Guid? professionalId) =>
        professionalId is { } id && counts.TryGetValue(id, out var count) ? count : 0;
}
