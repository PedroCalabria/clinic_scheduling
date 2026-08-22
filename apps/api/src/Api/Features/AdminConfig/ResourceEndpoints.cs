using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.AdminConfig;

/// <summary>
/// S9, lower half — the concrete rooms and equipment.
/// </summary>
/// <remarks>
/// Nothing in this change references a resource, so retirement always succeeds. The rule worth
/// reading here runs the other way: a resource points at its type, so both create and
/// reactivate have to insist that type is <em>active</em> — not merely that the row exists,
/// which the foreign key already guarantees and which soft-delete makes permanently true
/// (design D5).
/// </remarks>
internal static class ResourceEndpoints
{
    internal static IEndpointRouteBuilder MapResourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/config/resources")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/", ListAsync).WithName("ListResources");
        group.MapPost("/", CreateAsync).WithName("CreateResource");
        group.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateResource");
        group.MapPost("/{id:guid}/deactivate", DeactivateAsync).WithName("DeactivateResource");
        group.MapPost("/{id:guid}/reactivate", ReactivateAsync).WithName("ReactivateResource");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        // Ordering happens on the joined columns and the projection comes last. The other way
        // round does not translate: once the rows are projected through a constructor, EF can
        // no longer see which column an ordering key came from, and the query fails at runtime.
        var resources = await database.Resources
            .AsNoTracking()
            .Join(
                database.ResourceTypes.AsNoTracking(),
                resource => resource.ResourceTypeId,
                type => type.Id,
                (resource, type) => new { resource, type })
            .OrderBy(pair => pair.type.Name)
            .ThenBy(pair => pair.resource.Name)
            .Select(pair => new ResourceResponse(
                pair.resource.Id,
                pair.resource.Name,
                pair.type.Id,
                pair.type.Name,
                pair.resource.DeactivatedAtUtc == null))
            .ToListAsync(cancellationToken);

        return Results.Ok(resources);
    }

    private static async Task<IResult> CreateAsync(
        ResourceRequest request,
        ClinicDbContext database,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return CatalogRefusals.Required("name");
        }

        if (!await IsResourceTypeActiveAsync(database, request.ResourceTypeId, cancellationToken))
        {
            return CatalogRefusals.NotFound();
        }

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, request.Name, exclude: null, cancellationToken));

            var resource = Resource.Define(request.ResourceTypeId, request.Name, clock.GetUtcNow());

            database.Resources.Add(resource);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Created(
                $"/api/config/resources/{resource.Id}",
                await DescribeAsync(database, resource, cancellationToken));
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
        ResourceRequest request,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return CatalogRefusals.Required("name");
        }

        var resource = await database.Resources
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (resource is null)
        {
            return CatalogRefusals.NotFound();
        }

        if (!await IsResourceTypeActiveAsync(database, request.ResourceTypeId, cancellationToken))
        {
            return CatalogRefusals.NotFound();
        }

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, request.Name, exclude: id, cancellationToken));

            resource.Rename(request.Name);
            resource.Retype(request.ResourceTypeId);

            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(await DescribeAsync(database, resource, cancellationToken));
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
        var resource = await database.Resources
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (resource is null)
        {
            return CatalogRefusals.NotFound();
        }

        resource.Deactivate(clock.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(await DescribeAsync(database, resource, cancellationToken));
    }

    private static async Task<IResult> ReactivateAsync(
        Guid id,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var resource = await database.Resources
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (resource is null)
        {
            return CatalogRefusals.NotFound();
        }

        var typeIsActive =
            await IsResourceTypeActiveAsync(database, resource.ResourceTypeId, cancellationToken);

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, resource.Name, exclude: id, cancellationToken));

            resource.Reactivate(typeIsActive);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(await DescribeAsync(database, resource, cancellationToken));
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
    }

    private static Task<bool> IsResourceTypeActiveAsync(
        ClinicDbContext database,
        Guid resourceTypeId,
        CancellationToken cancellationToken) =>
        database.ResourceTypes.AnyAsync(
            type => type.Id == resourceTypeId && type.DeactivatedAtUtc == null,
            cancellationToken);

    private static Task<bool> IsNameTakenAsync(
        ClinicDbContext database,
        string name,
        Guid? exclude,
        CancellationToken cancellationToken)
    {
        var key = CatalogName.ComparisonKey(name);

        return database.Resources.AnyAsync(
            candidate => candidate.DeactivatedAtUtc == null
                && candidate.Name.ToLower() == key
                && (exclude == null || candidate.Id != exclude),
            cancellationToken);
    }

    /// <summary>Resolves the type's name so the response is readable without a second call.</summary>
    private static async Task<ResourceResponse> DescribeAsync(
        ClinicDbContext database,
        Resource resource,
        CancellationToken cancellationToken)
    {
        var typeName = await database.ResourceTypes
            .Where(type => type.Id == resource.ResourceTypeId)
            .Select(type => type.Name)
            .FirstAsync(cancellationToken);

        return new ResourceResponse(
            resource.Id,
            resource.Name,
            resource.ResourceTypeId,
            typeName,
            resource.IsActive);
    }
}
