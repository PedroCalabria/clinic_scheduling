using Clinic.Domain.Configuration;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

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
/// the grounds that it stored no instants, and this change does — so the question is genuinely
/// live rather than repeated. The answer is unchanged because <see cref="Instant"/> converts to
/// <c>DateTimeOffset</c> exactly, with a zero offset, which Npgsql already maps to
/// <c>timestamptz</c>. Four lines below versus a dependency that would also take over the
/// <c>time</c> and <c>date</c> mappings 3b deliberately hand-wrote. Revisit if a query ever needs
/// to express a NodaTime type in SQL — change 5's range work is where that could happen.
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
