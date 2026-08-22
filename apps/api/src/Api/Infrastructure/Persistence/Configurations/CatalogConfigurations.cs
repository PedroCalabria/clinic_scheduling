using Clinic.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Api.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapping for the catalog tables (design D8 — the schema is infrastructure and never leaks
/// into <c>Domain</c>).
/// </summary>
/// <remarks>
/// <para>
/// This file answers the revisit trigger change 2 recorded in
/// <see cref="IdentityConfigurations"/>: "revisit at change 3, when the clinic schema
/// multiplies the table count". Nine tables now, and the naming-convention package is still
/// rejected — snake_case is written once per property, it shows up in the migration diff where
/// it is reviewable, and inferring it would let a column rename happen without appearing in a
/// diff at all. The next revisit is 3b's five tables.
/// </para>
/// <para>
/// Two things here are deliberate and easy to undo by accident:
/// </para>
/// <para>
/// <b>Every foreign key is <c>Restrict</c>.</b> Under I10 nothing is ever deleted, so a
/// cascade rule is either dead configuration or — if a hard delete ever slipped in — a silent
/// data-loss path that would take a clinic's rooms with it.
/// </para>
/// <para>
/// <b>The unique indexes are not here.</b> Uniqueness is on <c>lower(name)</c> among active
/// rows (design D3), and EF's fluent API cannot express an index over an expression. Those
/// four indexes are raw SQL in the migration, which is why the migration has to be read rather
/// than assumed. What is configured here is the ordinary lookup index on the name, so a
/// duplicate check is not a sequential scan.
/// </para>
/// </remarks>
internal sealed class SpecialtyConfiguration : IEntityTypeConfiguration<Specialty>
{
    public void Configure(EntityTypeBuilder<Specialty> builder)
    {
        builder.ToTable("specialties");

        builder.HasKey(specialty => specialty.Id);
        builder.Property(specialty => specialty.Id).HasColumnName("id");

        builder.Property(specialty => specialty.Name)
            .HasColumnName("name")
            .HasMaxLength(CatalogName.MaxLength)
            .IsRequired();

        builder.Property(specialty => specialty.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(specialty => specialty.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(specialty => specialty.IsActive);

        builder.HasIndex(specialty => specialty.Name);
    }
}

internal sealed class ResourceTypeConfiguration : IEntityTypeConfiguration<ResourceType>
{
    public void Configure(EntityTypeBuilder<ResourceType> builder)
    {
        builder.ToTable("resource_types");

        builder.HasKey(type => type.Id);
        builder.Property(type => type.Id).HasColumnName("id");

        builder.Property(type => type.Name)
            .HasColumnName("name")
            .HasMaxLength(CatalogName.MaxLength)
            .IsRequired();

        // The F1 turnaround buffer. Change 4 reads this on every availability computation.
        builder.Property(type => type.BufferMinutes).HasColumnName("buffer_minutes");

        builder.Property(type => type.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(type => type.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(type => type.IsActive);

        builder.HasIndex(type => type.Name);
    }
}

internal sealed class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.ToTable("resources");

        builder.HasKey(resource => resource.Id);
        builder.Property(resource => resource.Id).HasColumnName("id");

        builder.Property(resource => resource.ResourceTypeId).HasColumnName("resource_type_id");

        builder.Property(resource => resource.Name)
            .HasColumnName("name")
            .HasMaxLength(CatalogName.MaxLength)
            .IsRequired();

        builder.Property(resource => resource.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(resource => resource.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(resource => resource.IsActive);

        builder.HasIndex(resource => resource.Name);

        // Change 4 asks "is a free resource of this type available?" — that question is this
        // index.
        builder.HasIndex(resource => resource.ResourceTypeId);

        builder.HasOne<ResourceType>()
            .WithMany()
            .HasForeignKey(resource => resource.ResourceTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AppointmentTypeConfiguration : IEntityTypeConfiguration<AppointmentType>
{
    public void Configure(EntityTypeBuilder<AppointmentType> builder)
    {
        builder.ToTable("appointment_types");

        builder.HasKey(type => type.Id);
        builder.Property(type => type.Id).HasColumnName("id");

        builder.Property(type => type.SpecialtyId).HasColumnName("specialty_id");
        builder.Property(type => type.RequiredResourceTypeId).HasColumnName("required_resource_type_id");

        builder.Property(type => type.Name)
            .HasColumnName("name")
            .HasMaxLength(CatalogName.MaxLength)
            .IsRequired();

        builder.Property(type => type.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(type => type.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(type => type.IsActive);

        builder.HasIndex(type => type.Name);

        // Both directions are read: "which visits does this specialty offer?" is the patient's
        // booking path, and both are the deactivation checks' dependent counts.
        builder.HasIndex(type => type.SpecialtyId);
        builder.HasIndex(type => type.RequiredResourceTypeId);

        builder.HasOne<Specialty>()
            .WithMany()
            .HasForeignKey(type => type.SpecialtyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ResourceType>()
            .WithMany()
            .HasForeignKey(type => type.RequiredResourceTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
