using Clinic.Domain.Configuration;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace Clinic.Api.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapping for the professional-configuration tables.
/// </summary>
/// <remarks>
/// <para>
/// The third configuration file, which answers the revisit trigger 3a recorded: fourteen tables
/// now, and explicit snake_case mapping is still the choice. The naming-convention package would
/// save real typing at this point, and the reason to keep refusing it has narrowed to one thing
/// — an inferred rename does not appear in a migration diff, and this project's review model is
/// reading the diff. If a fourth file appears, that argument should be re-examined rather than
/// repeated.
/// </para>
/// <para>
/// <b>Decision on NodaTime's Npgsql plugin (task 4.8): not needed.</b> The plugin exists to map
/// <c>Instant</c>, <c>ZonedDateTime</c> and friends onto Postgres timestamp types — which is
/// exactly what this change stores none of. What it stores is a time of day and a calendar date,
/// and those reach Postgres as <c>time</c> and <c>date</c> through the two converters below, in
/// eight lines and with no new dependency. Change 4 should revisit this if the solver ends up
/// wanting <c>Instant</c> columns; today it would be a dependency answering a question nobody
/// asked.
/// </para>
/// <para>
/// <b>Why the converters matter beyond convenience.</b> A <c>LocalTime</c> mapped to
/// <c>timestamptz</c> would be silently shifted by Postgres according to the session timezone,
/// which is the precise bug design E3 exists to prevent: hours entered correctly, read back an
/// hour out, and only in some deployments. <c>time</c> and <c>date</c> carry no zone, so the
/// schema itself makes that impossible. The audit columns (<c>created_at_utc</c>,
/// <c>deactivated_at_utc</c>) are genuinely instants — they record when a human acted — and stay
/// <c>timestamptz</c>.
/// </para>
/// </remarks>
internal static class WallClockConverters
{
    /// <summary>A time of day, with no zone. Postgres <c>time without time zone</c>.</summary>
    internal static readonly ValueConverter<LocalTime, TimeOnly> Time = new(
        local => new TimeOnly(local.Hour, local.Minute, local.Second),
        stored => new LocalTime(stored.Hour, stored.Minute, stored.Second));

    /// <summary>A calendar date, with no zone. Postgres <c>date</c>.</summary>
    internal static readonly ValueConverter<LocalDate, DateOnly> Date = new(
        local => new DateOnly(local.Year, local.Month, local.Day),
        stored => new LocalDate(stored.Year, stored.Month, stored.Day));
}

internal sealed class ProfessionalConfiguration : IEntityTypeConfiguration<Professional>
{
    public void Configure(EntityTypeBuilder<Professional> builder)
    {
        builder.ToTable("professionals");

        builder.HasKey(professional => professional.Id);
        builder.Property(professional => professional.Id).HasColumnName("id");

        builder.Property(professional => professional.UserId).HasColumnName("user_id");

        // Nullable by decision (design N10): the record is born on first configuration and an
        // invited professional may have neither record nor name. `Patient.full_name` is required
        // for the opposite reason — that record is born with a name from the identity provider.
        builder.Property(professional => professional.FullName)
            .HasColumnName("full_name")
            .HasMaxLength(200);

        builder.Property(professional => professional.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(professional => professional.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(professional => professional.IsActive);

        // 1:1 with the user, among active rows — the same partial-unique shape change 2 used for
        // Patient. Without it, "created on first save" (design E1) could race into two records
        // for one professional.
        builder.HasIndex(professional => professional.UserId)
            .IsUnique()
            .HasFilter("deactivated_at_utc IS NULL");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(professional => professional.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProfessionalSpecialtyConfiguration : IEntityTypeConfiguration<ProfessionalSpecialty>
{
    public void Configure(EntityTypeBuilder<ProfessionalSpecialty> builder)
    {
        builder.ToTable("professional_specialties");

        builder.HasKey(qualification => qualification.Id);
        builder.Property(qualification => qualification.Id).HasColumnName("id");

        builder.Property(qualification => qualification.ProfessionalId).HasColumnName("professional_id");
        builder.Property(qualification => qualification.SpecialtyId).HasColumnName("specialty_id");
        builder.Property(qualification => qualification.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(qualification => qualification.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(qualification => qualification.IsActive);

        // A specialty cannot be held twice at once. Partial, so a revoked qualification can be
        // granted again rather than colliding with its own history.
        builder.HasIndex(qualification => new { qualification.ProfessionalId, qualification.SpecialtyId })
            .IsUnique()
            .HasFilter("deactivated_at_utc IS NULL");

        // The gate's lookup direction: "does this professional hold this specialty?"
        builder.HasIndex(qualification => qualification.SpecialtyId);

        builder.HasOne<Professional>()
            .WithMany()
            .HasForeignKey(qualification => qualification.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Specialty>()
            .WithMany()
            .HasForeignKey(qualification => qualification.SpecialtyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProfessionalAppointmentTypeConfiguration
    : IEntityTypeConfiguration<ProfessionalAppointmentType>
{
    public void Configure(EntityTypeBuilder<ProfessionalAppointmentType> builder)
    {
        builder.ToTable("professional_appointment_types");

        builder.HasKey(duration => duration.Id);
        builder.Property(duration => duration.Id).HasColumnName("id");

        builder.Property(duration => duration.ProfessionalId).HasColumnName("professional_id");
        builder.Property(duration => duration.AppointmentTypeId).HasColumnName("appointment_type_id");
        builder.Property(duration => duration.DurationMinutes).HasColumnName("duration_minutes");
        builder.Property(duration => duration.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(duration => duration.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(duration => duration.IsActive);

        // One duration per professional per type. Two would leave change 4 choosing.
        builder.HasIndex(duration => new { duration.ProfessionalId, duration.AppointmentTypeId })
            .IsUnique()
            .HasFilter("deactivated_at_utc IS NULL");

        // Change 4 asks the other way round too: "which professionals can do this type?"
        builder.HasIndex(duration => duration.AppointmentTypeId);

        builder.HasOne<Professional>()
            .WithMany()
            .HasForeignKey(duration => duration.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AppointmentType>()
            .WithMany()
            .HasForeignKey(duration => duration.AppointmentTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkingHoursTemplateConfiguration : IEntityTypeConfiguration<WorkingHoursTemplate>
{
    public void Configure(EntityTypeBuilder<WorkingHoursTemplate> builder)
    {
        builder.ToTable("working_hours_templates");

        builder.HasKey(segment => segment.Id);
        builder.Property(segment => segment.Id).HasColumnName("id");

        builder.Property(segment => segment.ProfessionalId).HasColumnName("professional_id");

        // Enum as string, per the convention change 2 set: a migration diff that says
        // 'Monday' is reviewable; one that says 1 is a lookup exercise.
        builder.Property(segment => segment.DayOfWeek)
            .HasColumnName("day_of_week")
            .HasConversion<string>()
            .HasMaxLength(12);

        // Wall clock, no zone — see the note on WallClockConverters.
        builder.Property(segment => segment.StartTime)
            .HasColumnName("start_time")
            .HasConversion(WallClockConverters.Time);

        builder.Property(segment => segment.EndTime)
            .HasColumnName("end_time")
            .HasConversion(WallClockConverters.Time);

        builder.Property(segment => segment.EffectiveFrom)
            .HasColumnName("effective_from")
            .HasConversion(WallClockConverters.Date);

        builder.Property(segment => segment.EffectiveTo)
            .HasColumnName("effective_to")
            .HasConversion(WallClockConverters.Date);

        builder.Property(segment => segment.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(segment => segment.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(segment => segment.IsActive);
        builder.Ignore(segment => segment.Span);
        builder.Ignore(segment => segment.Period);

        // How both the overlap check and change 4's solver read this table.
        builder.HasIndex(segment => new { segment.ProfessionalId, segment.DayOfWeek });

        builder.HasOne<Professional>()
            .WithMany()
            .HasForeignKey(segment => segment.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkingHoursExceptionConfiguration : IEntityTypeConfiguration<WorkingHoursException>
{
    public void Configure(EntityTypeBuilder<WorkingHoursException> builder)
    {
        builder.ToTable("working_hours_exceptions");

        builder.HasKey(exception => exception.Id);
        builder.Property(exception => exception.Id).HasColumnName("id");

        builder.Property(exception => exception.ProfessionalId).HasColumnName("professional_id");

        builder.Property(exception => exception.Date)
            .HasColumnName("date")
            .HasConversion(WallClockConverters.Date);

        // Null for an unavailable-all-day exception, which is why these are nullable while a
        // template's are not.
        builder.Property(exception => exception.StartTime)
            .HasColumnName("start_time")
            .HasConversion(WallClockConverters.Time);

        builder.Property(exception => exception.EndTime)
            .HasColumnName("end_time")
            .HasConversion(WallClockConverters.Time);

        builder.Property(exception => exception.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(exception => exception.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(exception => exception.IsActive);
        builder.Ignore(exception => exception.IsUnavailableAllDay);
        builder.Ignore(exception => exception.Span);

        // One active exception per professional per date, so a date never has two conflicting
        // answers. This is the database floor under the domain rule.
        builder.HasIndex(exception => new { exception.ProfessionalId, exception.Date })
            .IsUnique()
            .HasFilter("deactivated_at_utc IS NULL");

        builder.HasOne<Professional>()
            .WithMany()
            .HasForeignKey(exception => exception.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
