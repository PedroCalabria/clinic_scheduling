using Clinic.Domain.Configuration;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using NpgsqlTypes;

namespace Clinic.Api.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapping for the scheduling tables.
/// </summary>
/// <remarks>
/// <para>
/// The fourth configuration file, which is the trigger 3b recorded for re-examining the
/// no-naming-convention-package decision. Re-examined and kept, on the same single ground: an
/// inferred rename does not appear in a migration diff, and this project's review model is
/// reading the diff. The typing cost is real and the reviewability is worth more.
/// </para>
/// <para>
/// <b>NodaTime's Npgsql plugin, revisited (task 5.4): still not needed.</b> 3b deferred this on
/// the grounds that it stored no instants, and <c>availability-read</c> does — so the question was
/// genuinely live rather than repeated. The answer was unchanged because <see cref="Instant"/>
/// converts to <c>DateTimeOffset</c> exactly, with a zero offset, which Npgsql already maps to
/// <c>timestamptz</c>. Four lines below versus a dependency that would also take over the
/// <c>time</c> and <c>date</c> mappings 3b deliberately hand-wrote. That deferral named change 5's
/// range work as the case that could change the answer.
/// </para>
/// <para>
/// <b>Asked a third time by <c>booking-core</c>, and this is where the deferral ends
/// (design B6).</b> The range column now exists, and the answer is still no:
/// <c>NpgsqlRange&lt;DateTime&gt;</c> maps <c>tstzrange</c> natively, so
/// <see cref="InstantConverters.Range"/> is a handful of lines, and the Dapper SQL builds ranges
/// with <c>tstzrange(@from, @to, '[)')</c> from ordinary parameters. What changes is that the
/// question stops being re-asked every change and gets an end condition instead: <b>the plugin
/// becomes correct when a query needs to express a NodaTime type in SQL itself</b> — an
/// <c>Interval</c> or a <c>LocalDate</c> as a parameter to a range function, or a NodaTime type in
/// a <c>WHERE</c>. Nothing in the remaining build order requires that. Adopting it late costs one
/// careful read of the diff, because it would also take over 3b's hand-written <c>time</c> and
/// <c>date</c> mappings.
/// </para>
/// </remarks>
internal static class InstantConverters
{
    /// <summary>
    /// An instant, as Postgres <c>timestamp with time zone</c>.
    /// </summary>
    /// <remarks>
    /// The mirror image of <see cref="WallClockConverters"/>, and the pairing is the point.
    /// Working hours are a rule and must never become a <c>timestamptz</c>, which Postgres would
    /// shift by session timezone. A block is an event and must never become a <c>time</c>, which
    /// would throw away the only date it will ever have. Both halves of 00-context.md §5 are now
    /// expressed in the schema, and both have a test asserting the column type.
    /// </remarks>
    internal static readonly ValueConverter<Instant, DateTimeOffset> Instant = new(
        instant => instant.ToDateTimeOffset(),
        stored => NodaTime.Instant.FromDateTimeOffset(stored));

    /// <summary>
    /// An appointment's time range, as Postgres <c>tstzrange</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bound flags are the load-bearing part.</b> <c>LowerBoundIsInclusive: true</c> and
    /// <c>UpperBoundIsInclusive: false</c> make this a <c>[)</c> range, which is exactly
    /// <see cref="BusyInterval"/>'s half-open overlap comparison. A closed range here would make
    /// the database refuse two appointments that abut — a visit ending at 10:00 and one starting
    /// at 10:00 — while the solver went on offering both, so the read and the floor would
    /// disagree about the same pair. Npgsql normalises a discrete range's bounds; a timestamp
    /// range is continuous and keeps what it is given, which is why this is stated rather than
    /// assumed.
    /// </para>
    /// <para>
    /// <see cref="DateTime"/> rather than <see cref="DateTimeOffset"/>, unlike the scalar
    /// converter above, because <c>NpgsqlRange&lt;DateTime&gt;</c> with UTC values is Npgsql's
    /// canonical mapping for <c>tstzrange</c>. <c>Instant</c> converts to a UTC
    /// <c>DateTime</c> exactly, so nothing is lost either way.
    /// </para>
    /// </remarks>
    internal static readonly ValueConverter<TimeRange, NpgsqlRange<DateTime>> Range = new(
        range => new NpgsqlRange<DateTime>(
            range.Start.ToDateTimeUtc(),
            lowerBoundIsInclusive: true,
            range.End.ToDateTimeUtc(),
            upperBoundIsInclusive: false),
        stored => TimeRange.Between(
            NodaTime.Instant.FromDateTimeUtc(DateTime.SpecifyKind(stored.LowerBound, DateTimeKind.Utc)),
            NodaTime.Instant.FromDateTimeUtc(DateTime.SpecifyKind(stored.UpperBound, DateTimeKind.Utc))));
}

internal sealed class TimeBlockConfiguration : IEntityTypeConfiguration<TimeBlock>
{
    public void Configure(EntityTypeBuilder<TimeBlock> builder)
    {
        builder.ToTable("time_blocks");

        builder.HasKey(block => block.Id);
        builder.Property(block => block.Id).HasColumnName("id");

        builder.Property(block => block.ProfessionalId).HasColumnName("professional_id");

        builder.Property(block => block.StartsAt)
            .HasColumnName("starts_at_utc")
            .HasConversion(InstantConverters.Instant);

        builder.Property(block => block.EndsAt)
            .HasColumnName("ends_at_utc")
            .HasConversion(InstantConverters.Instant);

        // Enum as string, per the convention change 2 set: a migration diff that says 'Internal'
        // is reviewable; one that says 1 is a lookup exercise. It matters more than usual here,
        // because change 7 adds the second value to this column.
        builder.Property(block => block.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(block => block.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(block => block.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Ignore(block => block.IsActive);
        builder.Ignore(block => block.Interval);

        // How the availability read finds a window's blocks, and how S3 lists a professional's
        // own. No uniqueness: overlapping blocks are deliberately allowed (design F10), so there
        // is nothing here for a unique index to enforce.
        builder.HasIndex(block => new { block.ProfessionalId, block.StartsAt });

        builder.HasOne<Professional>()
            .WithMany()
            .HasForeignKey(block => block.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// Mapping for the appointment aggregate (design B3, B4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The three exclusion constraints are not here.</b> EF does not model
/// <c>EXCLUDE USING gist</c>, so they are hand-written in the migration, with explicit names the
/// booking slice maps refusals from. That is a seam worth knowing about: the schema's most
/// important guarantee lives in SQL this file does not describe, which is why the migration
/// carries its own explanation and an integration test asserts the constraint names.
/// </para>
/// <para>
/// <b>No soft-delete column</b>, deviating from 02 §9's ERD and argued in design B3: the status is
/// the lifecycle, terminal states are richer facts than a deleted flag, and a second way for a row
/// to stop counting would have to be honoured by the exclusion predicate too.
/// </para>
/// </remarks>
internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    /// <summary>The exclusion constraints, named here so the slice and the tests agree with the migration.</summary>
    internal const string ProfessionalExclusion = "appointments_professional_no_overlap";

    internal const string ResourceExclusion = "appointments_resource_no_overlap";

    internal const string PatientExclusion = "appointments_patient_no_overlap";

    /// <summary>The one status the exclusion predicates treat as occupying its time.</summary>
    /// <remarks>
    /// Spelled out here as well as in the migration because the two must agree exactly with
    /// <c>Appointment.IsLive</c>. Three places, one value, and a test that writes a terminal row
    /// and books over it is what proves they still do.
    /// </remarks>
    internal const string LiveStatus = nameof(AppointmentStatus.Scheduled);

    public void Configure(EntityTypeBuilder<Appointment> builder)
    {
        builder.ToTable("appointments");

        builder.HasKey(appointment => appointment.Id);
        builder.Property(appointment => appointment.Id).HasColumnName("id");

        builder.Property(appointment => appointment.PatientId).HasColumnName("patient_id");
        builder.Property(appointment => appointment.ProfessionalId).HasColumnName("professional_id");
        builder.Property(appointment => appointment.ResourceId).HasColumnName("resource_id");
        builder.Property(appointment => appointment.AppointmentTypeId).HasColumnName("appointment_type_id");

        // One column, and the one the constraints index. The column type is stated explicitly
        // rather than inferred, because `tstzrange` is the whole reason this table can be
        // protected by an exclusion constraint at all (design B4).
        builder.Property(appointment => appointment.Range)
            .HasColumnName("time_range")
            .HasColumnType("tstzrange")
            .HasConversion(InstantConverters.Range);

        // Enum as string, per the convention change 2 set. It matters more than usual here: the
        // exclusion predicates are written as WHERE status = 'Scheduled', so a diff that says 1
        // would make the schema's central guarantee unreadable.
        builder.Property(appointment => appointment.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(appointment => appointment.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(appointment => appointment.CreatedAtUtc).HasColumnName("created_at_utc");

        // Read-through and derived members. StartsAt and EndsAt exist for callers and are the
        // Range's own bounds; mapping them would produce two more columns that could disagree
        // with the range the constraints operate on.
        builder.Ignore(appointment => appointment.StartsAt);
        builder.Ignore(appointment => appointment.EndsAt);
        builder.Ignore(appointment => appointment.IsLive);
        builder.Ignore(appointment => appointment.Interval);

        // No index declared here for the window read: the professional exclusion constraint
        // creates a GiST index on (professional_id, time_range), which is exactly the access
        // path the busy-interval query needs. A fourth index added by reflex would be dead
        // weight on every insert (task 6.7).
        builder.HasOne<Patient>()
            .WithMany()
            .HasForeignKey(appointment => appointment.PatientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Professional>()
            .WithMany()
            .HasForeignKey(appointment => appointment.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Resource>()
            .WithMany()
            .HasForeignKey(appointment => appointment.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AppointmentType>()
            .WithMany()
            .HasForeignKey(appointment => appointment.AppointmentTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
