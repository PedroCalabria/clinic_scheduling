using Clinic.Domain.Calendar;
using Clinic.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Api.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapping for a professional's calendar authorization (change 6a).
/// </summary>
/// <remarks>
/// <para>
/// <b>The unique index on <c>professional_id</c> is the load-bearing line</b> (design K10).
/// Reconnecting updates the existing row, and the constraint is what makes that a fact rather
/// than a convention — with two rows permitted, "which connection is the real one" becomes a
/// question every later reader has to answer, and 6b's dispatcher would be the one answering it
/// wrong. The same reasoning the booking exclusion constraints follow: the handler gives the
/// message, the constraint gives the guarantee.
/// </para>
/// <para>
/// <b>The sealed credential column is nullable and stays that way.</b> A withdrawn connection
/// genuinely holds nothing (design K10), and a sentinel string would be a second way to say
/// "none" — which is how a null check and a sentinel check end up disagreeing.
/// </para>
/// <para>
/// The column is generously sized: what it holds is a base64url envelope around a Google
/// refresh token, and Google does not document a maximum length for those. A too-tight column
/// would fail at the one moment nothing can be retried, so this errs wide.
/// </para>
/// </remarks>
internal sealed class CalendarConnectionConfiguration : IEntityTypeConfiguration<CalendarConnection>
{
    public void Configure(EntityTypeBuilder<CalendarConnection> builder)
    {
        builder.ToTable("calendar_connections");

        builder.HasKey(connection => connection.Id);
        builder.Property(connection => connection.Id).HasColumnName("id");

        builder.Property(connection => connection.ProfessionalId)
            .HasColumnName("professional_id")
            .IsRequired();

        // One authorization per professional. See the remarks — this is the guarantee, not a hint.
        builder.HasIndex(connection => connection.ProfessionalId)
            .IsUnique()
            .HasDatabaseName("ix_calendar_connections_professional_id");

        builder.Property(connection => connection.Provider)
            .HasColumnName("provider")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(connection => connection.TargetCalendarId)
            .HasColumnName("target_calendar_id")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(connection => connection.SealedCredential)
            .HasColumnName("refresh_token_sealed")
            .HasMaxLength(4000);

        builder.Property(connection => connection.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(connection => connection.ConnectedAtUtc)
            .HasColumnName("connected_at_utc")
            .IsRequired();

        builder.Property(connection => connection.StateObservedAtUtc)
            .HasColumnName("state_observed_at_utc")
            .IsRequired();

        // Restrict rather than cascade, the same as every other reference to a professional: a
        // professional is soft-deleted (I10), never removed, so a cascade would only ever fire
        // for a hard delete that should not be happening in the first place.
        builder.HasOne<Professional>()
            .WithMany()
            .HasForeignKey(connection => connection.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
