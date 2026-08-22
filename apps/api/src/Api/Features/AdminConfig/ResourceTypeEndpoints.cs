using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.AdminConfig;

/// <summary>
/// S9, upper half — resource types and their turnaround buffer (F1).
/// </summary>
/// <remarks>
/// The one entity with two kinds of dependent, and therefore the slice where the deactivation
/// check is worth reading: a resource type is blocked by active resources of it <em>and</em> by
/// active appointment types requiring it, and either alone is enough. Counting only the first
/// is the plausible mistake, so both counts are taken and both are asserted.
/// </remarks>
internal static class ResourceTypeEndpoints
{
    internal static IEndpointRouteBuilder MapResourceTypeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/config/resource-types")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/", ListAsync).WithName("ListResourceTypes");
        group.MapPost("/", CreateAsync).WithName("CreateResourceType");
        group.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateResourceType");
        group.MapPost("/{id:guid}/deactivate", DeactivateAsync).WithName("DeactivateResourceType");
        group.MapPost("/{id:guid}/reactivate", ReactivateAsync).WithName("ReactivateResourceType");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var types = await database.ResourceTypes
            .AsNoTracking()
            .OrderBy(type => type.Name)
            .Select(type => new ResourceTypeResponse(
                type.Id,
                type.Name,
                type.BufferMinutes,
                type.DeactivatedAtUtc == null))
            .ToListAsync(cancellationToken);

        return Results.Ok(types);
    }

    private static async Task<IResult> CreateAsync(
        ResourceTypeRequest request,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return CatalogRefusals.Required("name");
        }

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, request.Name, exclude: null, cancellationToken));

            var type = ResourceType.Define(request.Name, request.BufferMinutes, clock.GetUtcNow());

            database.ResourceTypes.Add(type);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Created(
                $"/api/config/resource-types/{type.Id}",
                new ResourceTypeResponse(type.Id, type.Name, type.BufferMinutes, IsActive: true));
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
        catch (DomainRuleViolationException)
        {
            // Either the name is unusable or the buffer is negative. The buffer is the one the
            // caller is more likely to have got wrong on a form that has both.
            return CatalogRefusals.Invalid(request.BufferMinutes < 0 ? "bufferMinutes" : "name");
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        ResourceTypeRequest request,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return CatalogRefusals.Required("name");
        }

        var type = await database.ResourceTypes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (type is null)
        {
            return CatalogRefusals.NotFound();
        }

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, request.Name, exclude: id, cancellationToken));

            type.Rename(request.Name);
            type.ChangeBuffer(request.BufferMinutes);

            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(
                new ResourceTypeResponse(type.Id, type.Name, type.BufferMinutes, type.IsActive));
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
        catch (DomainRuleViolationException)
        {
            return CatalogRefusals.Invalid(request.BufferMinutes < 0 ? "bufferMinutes" : "name");
        }
    }

    private static async Task<IResult> DeactivateAsync(
        Guid id,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var type = await database.ResourceTypes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (type is null)
        {
            return CatalogRefusals.NotFound();
        }

        var activeResources = await database.Resources
            .CountAsync(
                resource => resource.ResourceTypeId == id && resource.DeactivatedAtUtc == null,
                cancellationToken);

        var activeAppointmentTypes = await database.AppointmentTypes
            .CountAsync(
                appointmentType => appointmentType.RequiredResourceTypeId == id
                    && appointmentType.DeactivatedAtUtc == null,
                cancellationToken);

        try
        {
            type.Deactivate(activeResources, activeAppointmentTypes, clock.GetUtcNow());
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(
                new ResourceTypeResponse(type.Id, type.Name, type.BufferMinutes, type.IsActive));
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
    }

    private static async Task<IResult> ReactivateAsync(
        Guid id,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var type = await database.ResourceTypes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (type is null)
        {
            return CatalogRefusals.NotFound();
        }

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, type.Name, exclude: id, cancellationToken));

            type.Reactivate();
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(
                new ResourceTypeResponse(type.Id, type.Name, type.BufferMinutes, type.IsActive));
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
    }

    private static Task<bool> IsNameTakenAsync(
        ClinicDbContext database,
        string name,
        Guid? exclude,
        CancellationToken cancellationToken)
    {
        var key = CatalogName.ComparisonKey(name);

        return database.ResourceTypes.AnyAsync(
            candidate => candidate.DeactivatedAtUtc == null
                && candidate.Name.ToLower() == key
                && (exclude == null || candidate.Id != exclude),
            cancellationToken);
    }
}
