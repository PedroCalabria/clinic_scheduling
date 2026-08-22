using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.AdminConfig;

/// <summary>
/// S8 — specialties (spec: the catalog defines what the clinic offers).
/// </summary>
/// <remarks>
/// The simplest of the four slices, and therefore the one that shows the shape the other three
/// follow: one policy for the group, five operations, and the deactivation check that counts
/// <em>active</em> dependents and hands the number to the domain (design D2).
/// </remarks>
internal static class SpecialtyEndpoints
{
    internal static IEndpointRouteBuilder MapSpecialtyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Structural configuration is the administrator/front-desk line
        // (01-requirements.md §Roles). One policy on the group rather than per endpoint, so a
        // sixth operation added later cannot be born unprotected.
        var group = endpoints.MapGroup("/api/config/specialties")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/", ListAsync).WithName("ListSpecialties");
        group.MapPost("/", CreateAsync).WithName("CreateSpecialty");
        group.MapPut("/{id:guid}", RenameAsync).WithName("RenameSpecialty");
        group.MapPost("/{id:guid}/deactivate", DeactivateAsync).WithName("DeactivateSpecialty");
        group.MapPost("/{id:guid}/reactivate", ReactivateAsync).WithName("ReactivateSpecialty");

        return endpoints;
    }

    /// <summary>
    /// Every record, active and inactive — the screen distinguishes them rather than hiding
    /// the retired ones, because a retired specialty is what a reactivation acts on.
    /// </summary>
    private static async Task<IResult> ListAsync(
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var specialties = await database.Specialties
            .AsNoTracking()
            .OrderBy(specialty => specialty.Name)
            .Select(specialty => new SpecialtyResponse(
                specialty.Id,
                specialty.Name,
                specialty.DeactivatedAtUtc == null))
            .ToListAsync(cancellationToken);

        return Results.Ok(specialties);
    }

    private static async Task<IResult> CreateAsync(
        CatalogNameRequest request,
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

            var specialty = Specialty.Define(request.Name, clock.GetUtcNow());

            database.Specialties.Add(specialty);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Created(
                $"/api/config/specialties/{specialty.Id}",
                new SpecialtyResponse(specialty.Id, specialty.Name, IsActive: true));
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

    private static async Task<IResult> RenameAsync(
        Guid id,
        CatalogNameRequest request,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return CatalogRefusals.Required("name");
        }

        var specialty = await database.Specialties
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (specialty is null)
        {
            return CatalogRefusals.NotFound();
        }

        try
        {
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, request.Name, exclude: id, cancellationToken));

            specialty.Rename(request.Name);
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(new SpecialtyResponse(specialty.Id, specialty.Name, specialty.IsActive));
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
        var specialty = await database.Specialties
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (specialty is null)
        {
            return CatalogRefusals.NotFound();
        }

        // The predicate that matters: DeactivatedAtUtc == null on the DEPENDENT. Putting it on
        // the target instead would count retired appointment types and refuse a retirement that
        // should succeed — which is why an integration test asserts exactly that case.
        var activeAppointmentTypes = await database.AppointmentTypes
            .CountAsync(
                type => type.SpecialtyId == id && type.DeactivatedAtUtc == null,
                cancellationToken);

        try
        {
            specialty.Deactivate(activeAppointmentTypes, clock.GetUtcNow());
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(new SpecialtyResponse(specialty.Id, specialty.Name, specialty.IsActive));
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
        var specialty = await database.Specialties
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (specialty is null)
        {
            return CatalogRefusals.NotFound();
        }

        try
        {
            // A specialty points at nothing, so there is no reference to re-validate — only
            // the name, which another specialty may have taken while this one was retired.
            CatalogName.EnsureAvailable(
                await IsNameTakenAsync(database, specialty.Name, exclude: id, cancellationToken));

            specialty.Reactivate();
            await database.SaveChangesAsync(cancellationToken);

            return Results.Ok(new SpecialtyResponse(specialty.Id, specialty.Name, specialty.IsActive));
        }
        catch (CatalogRuleViolationException refusal)
        {
            return refusal.ToResult();
        }
    }

    /// <summary>
    /// Whether an active specialty other than <paramref name="exclude"/> holds this name.
    /// </summary>
    /// <remarks>
    /// <c>ToLower()</c> is not incidental: Npgsql translates it to <c>lower(name)</c>, which is
    /// exactly the expression the partial unique index is built on (design D3), so this check
    /// and the database floor agree and the check is index-backed rather than a scan.
    /// </remarks>
    private static Task<bool> IsNameTakenAsync(
        ClinicDbContext database,
        string name,
        Guid? exclude,
        CancellationToken cancellationToken)
    {
        var key = CatalogName.ComparisonKey(name);

        return database.Specialties.AnyAsync(
            candidate => candidate.DeactivatedAtUtc == null
                && candidate.Name.ToLower() == key
                && (exclude == null || candidate.Id != exclude),
            cancellationToken);
    }
}
