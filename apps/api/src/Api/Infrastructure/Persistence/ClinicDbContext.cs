using Clinic.Api.Infrastructure.Auth;
using Clinic.Domain.Configuration;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the write path, and for the availability read's bounded input
/// (Decision L).
/// </summary>
/// <remarks>
/// The Dapper half of Decision L was re-scoped by <c>availability-read</c>: the solver turned
/// out to be interval arithmetic in the domain core rather than range SQL (design F1), so the
/// availability read is an ordinary bounded query through this context. Dapper arrives with
/// change 5's booking write path, where the <c>tstzrange</c> columns and GiST indexes that
/// justify it actually exist.
/// </remarks>
/// <remarks>
/// Mapping lives in <see cref="IEntityTypeConfiguration{TEntity}"/> classes under
/// <c>Configurations</c>, discovered from this assembly. Change 1 mapped its single marker
/// table inline here and recorded the trigger for moving out — five identity tables is that
/// trigger. Naming stays explicit (snake_case spelled out, no convention package): a
/// dependency that renames columns by inference has to earn its place, and it has not yet.
/// </remarks>
internal sealed class ClinicDbContext(DbContextOptions<ClinicDbContext> options) : DbContext(options)
{
    public DbSet<PlatformMarker> PlatformMarkers => Set<PlatformMarker>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<Consent> Consents => Set<Consent>();

    public DbSet<AccessLog> AccessLog => Set<AccessLog>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Specialty> Specialties => Set<Specialty>();

    public DbSet<ResourceType> ResourceTypes => Set<ResourceType>();

    public DbSet<Resource> Resources => Set<Resource>();

    public DbSet<AppointmentType> AppointmentTypes => Set<AppointmentType>();

    public DbSet<Professional> Professionals => Set<Professional>();

    public DbSet<ProfessionalSpecialty> ProfessionalSpecialties => Set<ProfessionalSpecialty>();

    public DbSet<ProfessionalAppointmentType> ProfessionalAppointmentTypes =>
        Set<ProfessionalAppointmentType>();

    public DbSet<WorkingHoursTemplate> WorkingHoursTemplates => Set<WorkingHoursTemplate>();

    public DbSet<WorkingHoursException> WorkingHoursExceptions => Set<WorkingHoursException>();

    /// <summary>
    /// Professional unavailability. Internally sourced today; change 7 adds the external half
    /// to the same table.
    /// </summary>
    public DbSet<TimeBlock> TimeBlocks => Set<TimeBlock>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The change-1 marker table keeps its original inline mapping so the existing
        // migration and snapshot stay byte-for-byte the schema they described.
        var marker = modelBuilder.Entity<PlatformMarker>();

        marker.ToTable("platform_marker");
        marker.HasKey(m => m.Id);
        marker.Property(m => m.Id).HasColumnName("id");
        marker.Property(m => m.RecordedAtUtc).HasColumnName("recorded_at_utc");
        marker.Property(m => m.Description).HasColumnName("description").HasMaxLength(200);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClinicDbContext).Assembly);
    }
}
