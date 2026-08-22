using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.AdminConfig;

/// <summary>
/// S10 — the kinds of visit the clinic offers.
/// </summary>
/// <remarks>
/// The entity with two outbound references, so it is the one where design D5 has the most to
/// check: create, edit, and reactivate all insist that both the specialty and the required
/// resource type are active. Given an appointment type, change 4 derives eligible professionals
/// (I2) and qualifying resources (I3) — which only holds if both references are still real.
///
/// No duration here, by decision: see <see cref="AppointmentTypeResponse"/>.
/// </remarks>
internal static class AppointmentTypeEndpoints
{
    internal static IEndpointRouteBuilder MapAppointmentTypeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/config/appointment-types")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/", ListAsync).WithName("ListAppointmentTypes");
        group.MapPost("/", CreateAsync).WithName("CreateAppointmentType");
        group.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateAppointmentType");
        group.MapPost("/{id:guid}/deactivate", DeactivateAsync).WithName("DeactivateAppointmentType");
        group.MapPost("/{id:guid}/reactivate", ReactivateAsync).WithName("ReactivateAppointmentType");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var types = await database.AppointmentTypes
            .AsNoTracking()
            .Join(
                database.Specialties.AsNoTracking(),
                type => type.SpecialtyId,
                specialty => specialty.Id,
                (type, specialty) => new { type, specialty })
            .Join(
                database.ResourceTypes.AsNoTracking(),
                pair => pair.type.RequiredResourceTypeId,
                resourceType => resourceType.Id,
                (pair, resourceType) => new { pair.type, pair.specialty, resourceType })
            // Order on the joined columns, project last — see the note in ResourceEndpoints.
            .OrderBy(row => row.specialty.Name)
            .ThenBy(row => row.type.Name)
            .Select(row => new AppointmentTypeResponse(
                row.type.Id,
                row.type.Name,
                row.specialty.Id,
                row.specialty.Name,
                row.resourceType.Id,
                row.resourceType.Name,
                row.type.DeactivatedAtUtc == null))
            .ToListAsync(cancellationToken);

        return Results.Ok(types);
    }

    private static async Task<IResult> CreateAsync(
        AppointmentTypeRequest request,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return CatalogRefusals.Required("name");
        }

        if (!await AreReferencesActiveAsync(
                database, request.SpecialtyId, request.RequiredResourceTypeId, cancellationToken))
        {
            return CatalogRefusals.NotFound();
        }

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, request.Name, exclude: null, cancellationToken));

            var type = AppointmentType.Define(
                request.SpecialtyId,
                request.RequiredResourceTypeId,
                request.Name,
                clock.GetUtcNow());

            database.AppointmentTypes.Add(type);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Created(
                $"/api/config/appointment-types/{type.Id}",
                await DescribeAsync(database, type, cancellationToken));
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
        catch (DomainRuleViolationException)
        {
            return CatalogRefusals.Invalid("name");
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        AppointmentTypeRequest request,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return CatalogRefusals.Required("name");
        }

        var type = await database.AppointmentTypes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (type is null)
        {
            return CatalogRefusals.NotFound();
        }

        if (!await AreReferencesActiveAsync(
                database, request.SpecialtyId, request.RequiredResourceTypeId, cancellationToken))
        {
            return CatalogRefusals.NotFound();
        }

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, request.Name, exclude: id, cancellationToken));

            type.Rename(request.Name);
            type.Reassign(request.SpecialtyId, request.RequiredResourceTypeId);

            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(await DescribeAsync(database, type, cancellationToken));
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
        catch (DomainRuleViolationException)
        {
            return CatalogRefusals.Invalid("name");
        }
    }

    private static async Task<IResult> DeactivateAsync(
        Guid id,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var type = await database.AppointmentTypes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (type is null)
        {
            return CatalogRefusals.NotFound();
        }

        // Nothing references a kind of visit yet. In change 5 an appointment will, and this is
        // where that count arrives.
        type.Deactivate(clock.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(await DescribeAsync(database, type, cancellationToken));
    }

    private static async Task<IResult> ReactivateAsync(
        Guid id,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var type = await database.AppointmentTypes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (type is null)
        {
            return CatalogRefusals.NotFound();
        }

        var specialtyIsActive = await database.Specialties.AnyAsync(
            specialty => specialty.Id == type.SpecialtyId && specialty.DeactivatedAtUtc == null,
            cancellationToken);

        var resourceTypeIsActive = await database.ResourceTypes.AnyAsync(
            resourceType => resourceType.Id == type.RequiredResourceTypeId
                && resourceType.DeactivatedAtUtc == null,
            cancellationToken);

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, type.Name, exclude: id, cancellationToken));

            type.Reactivate(specialtyIsActive, resourceTypeIsActive);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(await DescribeAsync(database, type, cancellationToken));
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
    }

    /// <summary>
    /// Both references must exist AND be active — the foreign key only gives the first half,
    /// and soft-delete makes it permanently satisfied (design, Context).
    /// </summary>
    private static async Task<bool> AreReferencesActiveAsync(
        ClinicDbContext database,
        Guid specialtyId,
        Guid requiredResourceTypeId,
        CancellationToken cancellationToken)
    {
        var specialtyIsActive = await database.Specialties.AnyAsync(
            specialty => specialty.Id == specialtyId && specialty.DeactivatedAtUtc == null,
            cancellationToken);

        if (!specialtyIsActive)
        {
            return false;
        }

        return await database.ResourceTypes.AnyAsync(
            type => type.Id == requiredResourceTypeId && type.DeactivatedAtUtc == null,
            cancellationToken);
    }

    private static Task<bool> IsNameTakenAsync(
        ClinicDbContext database,
        string name,
        Guid? exclude,
        CancellationToken cancellationToken)
    {
        var key = CatalogName.ComparisonKey(name);

        return database.AppointmentTypes.AnyAsync(
            candidate => candidate.DeactivatedAtUtc == null
                && candidate.Name.ToLower() == key
                && (exclude == null || candidate.Id != exclude),
            cancellationToken);
    }

    private static async Task<AppointmentTypeResponse> DescribeAsync(
        ClinicDbContext database,
        AppointmentType type,
        CancellationToken cancellationToken)
    {
        var specialtyName = await database.Specialties
            .Where(specialty => specialty.Id == type.SpecialtyId)
            .Select(specialty => specialty.Name)
            .FirstAsync(cancellationToken);

        var resourceTypeName = await database.ResourceTypes
            .Where(resourceType => resourceType.Id == type.RequiredResourceTypeId)
            .Select(resourceType => resourceType.Name)
            .FirstAsync(cancellationToken);

        return new AppointmentTypeResponse(
            type.Id,
            type.Name,
            type.SpecialtyId,
            specialtyName,
            type.RequiredResourceTypeId,
            resourceTypeName,
            type.IsActive);
    }
}
