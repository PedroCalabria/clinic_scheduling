using System.Security.Claims;
using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Time;
using Clinic.Domain;
using Clinic.Domain.Configuration;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.TimeZones;

namespace Clinic.Api.Features.Availability;

/// <summary>
/// S3 — a professional's own blocked time (spec: availability; design F11, F12).
/// </summary>
/// <remarks>
/// <para>
/// The producer that makes this change's subtraction real. Without it the solver would remove
/// intervals from a set that nothing can put anything into until change 5, and "availability
/// reflects what the professional is busy with" would be a claim only a test fixture had ever
/// exercised.
/// </para>
/// <para>
/// <b>Ownership, on a record that is not patient data.</b> The collection is caller-scoped, so a
/// new block cannot be aimed at anybody else — there is no professional in the request to aim it
/// with. Item operations are id-addressed and therefore <em>can</em> be aimed elsewhere, and are
/// refused with <c>auth.ownership_denied</c>. An administrator is refused outright: qualification
/// is an administrative decision and 3b refuses to let a professional make it, and personal time
/// is the mirror image.
/// </para>
/// <para>
/// The ownership check deliberately does <b>not</b> go through
/// <see cref="PatientDataGuard"/> and writes no <c>AccessLog</c> row. That trail exists so a
/// patient can be told who read their data, and widening it to cover a doctor reading their own
/// diary would dilute an audit whose value is its narrowness.
/// </para>
/// <para>
/// Times cross the wire as clinic wall clock and are converted here, using the same zone and the
/// same lenient resolver the solver uses — so a block and a working hour can never disagree about
/// what a wall-clock time meant.
/// </para>
/// </remarks>
internal static class TimeBlockEndpoints
{
    internal static IEndpointRouteBuilder MapTimeBlockEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/blocks")
            .RequireAuthorization(AuthorizationPolicies.Professional);

        group.MapGet("/", ListAsync).WithName("ListMyBlocks");
        group.MapPost("/", CreateAsync).WithName("CreateMyBlock");
        group.MapPut("/{blockId:guid}", UpdateAsync).WithName("UpdateMyBlock");
        group.MapPost("/{blockId:guid}/retire", RetireAsync).WithName("RetireMyBlock");
        group.MapPost("/{blockId:guid}/restore", RestoreAsync).WithName("RestoreMyBlock");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal actor,
        ClinicDbContext database,
        ClinicTimezone timezone,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(database, actor, cancellationToken);

        if (caller is null)
        {
            return NotConfigured();
        }

        var blocks = await database.TimeBlocks
            .AsNoTracking()
            .Where(block => block.ProfessionalId == caller.Id)
            .OrderBy(block => block.StartsAt)
            .ToListAsync(cancellationToken);

        // Retired blocks are listed too, distinguishable by their flag. Retirement is reversible
        // everywhere else in this system (design D1), and a list that hid them would offer no way
        // back.
        return Results.Ok(new TimeBlockListResponse(
            timezone.Id,
            blocks.Select(block => Describe(block, timezone)).ToList()));
    }

    private static async Task<IResult> CreateAsync(
        SaveTimeBlockRequest request,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        ClinicTimezone timezone,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(database, actor, cancellationToken);

        if (caller is null)
        {
            return NotConfigured();
        }

        if (Parse(request, timezone) is not { } times)
        {
            return InvalidRange();
        }

        try
        {
            var block = TimeBlock.ForProfessional(caller.Id, times.StartsAt, times.EndsAt, clock.GetUtcNow());

            database.TimeBlocks.Add(block);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(Describe(block, timezone));
        }
        catch (DomainRuleViolationException)
        {
            // The only reachable refusal on this path: the professional comes from the session,
            // never from the body, so a missing one is impossible rather than merely unlikely.
            return InvalidRange();
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid blockId,
        SaveTimeBlockRequest request,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        ClinicTimezone timezone,
        CancellationToken cancellationToken)
    {
        var found = await ResolveOwnedAsync(blockId, actor, database, cancellationToken);

        if (found.Refusal is not null)
        {
            return found.Refusal;
        }

        var block = found.Block!;

        if (Parse(request, timezone) is not { } times)
        {
            return InvalidRange();
        }

        try
        {
            block.Reschedule(times.StartsAt, times.EndsAt);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(Describe(block, timezone));
        }
        catch (DomainRuleViolationException)
        {
            // The domain refused before assigning anything, so the stored range is untouched —
            // which is what the screen relies on to keep showing the truth after a refusal.
            return InvalidRange();
        }
    }

    private static Task<IResult> RetireAsync(
        Guid blockId,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        MutateAsync(blockId, actor, database, cancellationToken, block => block.Retire(clock.GetUtcNow()));

    private static Task<IResult> RestoreAsync(
        Guid blockId,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        CancellationToken cancellationToken) =>
        MutateAsync(blockId, actor, database, cancellationToken, block => block.Restore());

    private static async Task<IResult> MutateAsync(
        Guid blockId,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        CancellationToken cancellationToken,
        Action<TimeBlock> change)
    {
        var found = await ResolveOwnedAsync(blockId, actor, database, cancellationToken);

        if (found.Refusal is not null)
        {
            return found.Refusal;
        }

        change(found.Block!);
        await database.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    // --- Resolution ------------------------------------------------------------------

    /// <summary>
    /// The caller's own configuration record, which is what a block hangs on.
    /// </summary>
    /// <remarks>
    /// Read from the session, never from the request. That is what makes the ownership guarantee
    /// structural: a client-supplied id can narrow access in this system but never widen it, the
    /// same property <see cref="PatientDataGuard"/> was shaped around.
    /// </remarks>
    private static Task<Professional?> CallerAsync(
        ClinicDbContext database,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken) =>
        database.Professionals.FirstOrDefaultAsync(
            professional => professional.UserId == actor.UserId() && professional.DeactivatedAtUtc == null,
            cancellationToken);

    private static async Task<(TimeBlock? Block, IResult? Refusal)> ResolveOwnedAsync(
        Guid blockId,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(database, actor, cancellationToken);

        if (caller is null)
        {
            return (null, NotConfigured());
        }

        var block = await database.TimeBlocks.FirstOrDefaultAsync(
            candidate => candidate.Id == blockId, cancellationToken);

        if (block is null)
        {
            return (null, CatalogRefusals.NotFound());
        }

        if (block.ProfessionalId != caller.Id)
        {
            // Ownership, not role: this caller is a professional and professionals may manage
            // blocks — just not this one. The distinction is the second level of authorization
            // change 2 built, applied for the first time to something that is not patient data.
            return (null, ApiError.Result(ErrorCodes.OwnershipDenied, StatusCodes.Status403Forbidden));
        }

        return (block, null);
    }

    // --- Wall clock in, instants stored ----------------------------------------------

    /// <summary>
    /// The submitted wall-clock times as instants, or null if either is not a time at all.
    /// </summary>
    /// <remarks>
    /// An unparseable value reports <c>block.invalid_range</c> rather than
    /// <c>validation.invalid_format</c>. From the professional's side both are the same mistake in
    /// the same field with the same remedy, and one code per user-meaningful failure is the
    /// catalogue's own rule.
    /// </remarks>
    private static (Instant StartsAt, Instant EndsAt)? Parse(
        SaveTimeBlockRequest request,
        ClinicTimezone timezone)
    {
        if (WallClockText.ParseDateTime(request.StartsAt) is not { } start
            || WallClockText.ParseDateTime(request.EndsAt) is not { } end)
        {
            return null;
        }

        return (Resolve(timezone, start), Resolve(timezone, end));
    }

    /// <summary>
    /// Wall clock to instant, resolved exactly as the solver resolves working hours.
    /// </summary>
    /// <remarks>
    /// The same lenient resolver, deliberately. If a professional blocks 02:30 on a
    /// spring-forward date, that local time does not exist — refusing would be pedantic about a
    /// clock change they did not make, and resolving it differently from the way their working
    /// hours are resolved would let a block miss the hours it was meant to cover.
    /// </remarks>
    private static Instant Resolve(ClinicTimezone timezone, LocalDateTime local) =>
        timezone.Zone.ResolveLocal(local, Resolvers.LenientResolver).ToInstant();

    private static TimeBlockResponse Describe(TimeBlock block, ClinicTimezone timezone) =>
        new(
            block.Id,
            WallClockText.Format(block.StartsAt.InZone(timezone.Zone).LocalDateTime),
            WallClockText.Format(block.EndsAt.InZone(timezone.Zone).LocalDateTime),
            block.IsActive);

    private static IResult InvalidRange() =>
        ApiError.Result(ErrorCodes.BlockInvalidRange, StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// The caller holds the professional role but has no clinical configuration yet.
    /// </summary>
    /// <remarks>
    /// A real state, not an edge case: change 2 invites a professional and 3b's S7 creates their
    /// record on the administrator's first save (design E1), so a claimed invitation can sit in
    /// between. There is no schedule to block time in, and creating the record from here would
    /// let a professional self-create the clinical configuration an administrator owns. The
    /// remedy is administrative, so the refusal is the catalogue's not-found rather than a new
    /// code for a state that resolves itself.
    /// </remarks>
    private static IResult NotConfigured() => CatalogRefusals.NotFound();
}
