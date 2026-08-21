using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the write path (Decision L). Reads on the availability hot path
/// go through Dapper instead, and arrive in change 4.
/// </summary>
/// <remarks>
/// Table and column names are mapped to snake_case explicitly rather than via a naming
/// convention package — one table does not justify a dependency, and the project's rule is
/// that every technology must answer what problem it solves here (04-architecture.md).
/// Revisit if the schema grows past the point where explicit mapping is tedious (change 3).
/// </remarks>
internal sealed class ClinicDbContext(DbContextOptions<ClinicDbContext> options) : DbContext(options)
{
    public DbSet<PlatformMarker> PlatformMarkers => Set<PlatformMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var marker = modelBuilder.Entity<PlatformMarker>();

        marker.ToTable("platform_marker");
        marker.HasKey(m => m.Id);
        marker.Property(m => m.Id).HasColumnName("id");
        marker.Property(m => m.RecordedAtUtc).HasColumnName("recorded_at_utc");
        marker.Property(m => m.Description).HasColumnName("description").HasMaxLength(200);
    }
}
