using System.Reflection;
using Clinic.Domain;

namespace Clinic.Domain.UnitTests;

/// <summary>
/// Guards the protected-core boundary (Decision K / P-3, 00-context.md §3).
/// </summary>
/// <remarks>
/// The domain core is empty in change 1, so there are no invariants to test yet — I1-I10
/// arrive with the Appointment aggregate in change 5. What IS testable now is the property
/// the whole architecture rests on: that Domain stays free of infrastructure.
///
/// This complements, not duplicates, the ForbidInfrastructureReferences target in
/// Domain.csproj. That target inspects declared PackageReferences; this inspects the
/// COMPILED assembly's references, so it also catches infrastructure arriving transitively
/// through a project reference.
/// </remarks>
public sealed class DomainBoundaryTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Dapper",
        "Npgsql",
        "Serilog",
        "Hangfire",
    ];

    [Fact]
    public void Domain_assembly_references_no_infrastructure()
    {
        var domain = typeof(DomainAssemblyMarker).Assembly;

        var violations = domain
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Domain_assembly_is_loadable_and_named_as_expected()
    {
        var domain = typeof(DomainAssemblyMarker).Assembly;

        Assert.Equal("Clinic.Domain", domain.GetName().Name);
    }
}
