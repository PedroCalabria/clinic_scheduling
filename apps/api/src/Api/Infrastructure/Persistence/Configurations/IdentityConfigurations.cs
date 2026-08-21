using Clinic.Api.Infrastructure.Auth;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Api.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapping for the identity tables (design A7 — the schema is infrastructure, so it lives
/// here and never leaks into <c>Domain</c>).
/// </summary>
/// <remarks>
/// <para>
/// Change 1 mapped its one marker table inline in the context and recorded the revisit
/// trigger: "when explicit mapping gets tedious". Five tables is where that starts, so the
/// mapping moves into <see cref="IEntityTypeConfiguration{TEntity}"/> classes — still
/// explicit, still no extra dependency, but no longer piling up in one method. Adding a
/// naming-convention package was considered and rejected for now: it would rename columns
/// by inference, and the project's rule is that every dependency answers what problem it
/// solves here. Revisit at change 3, when the clinic schema multiplies the table count.
/// </para>
/// <para>
/// Enums are stored as strings, matching the ERD in 02-domain-model.md. A migration diff
/// that says <c>'Administrator'</c> is reviewable; one that says <c>4</c> is a lookup
/// exercise, and the storage saving is meaningless at this scale.
/// </para>
/// </remarks>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).HasColumnName("id");

        builder.Property(user => user.Email)
            .HasColumnName("email")
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(user => user.AuthProvider)
            .HasColumnName("auth_provider")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(user => user.ExternalSubjectId)
            .HasColumnName("external_subject_id")
            .HasMaxLength(255);

        builder.Property(user => user.PasswordHash)
            .HasColumnName("password_hash")
            .HasMaxLength(500);

        builder.Property(user => user.Role)
            .HasColumnName("role")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(user => user.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(user => user.MustChangePassword).HasColumnName("must_change_password");
        builder.Property(user => user.FailedSignInCount).HasColumnName("failed_sign_in_count");
        builder.Property(user => user.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(user => user.DeletedAtUtc).HasColumnName("deleted_at_utc");

        builder.Ignore(user => user.IsDeleted);
        builder.Ignore(user => user.AwaitsClaim);
        builder.Ignore(user => user.CanAuthenticate);

        // Email is unique among LIVE accounts only. Soft-delete is the project's only
        // deletion (I10), so an unfiltered unique index would make one deleted account
        // block that address forever — which is a data-retention decision masquerading as
        // a constraint.
        builder.HasIndex(user => user.Email)
            .IsUnique()
            .HasFilter("deleted_at_utc IS NULL")
            .HasDatabaseName("ix_users_email_live");

        // One provider identity maps to exactly one user. Filtered because an unclaimed
        // professional invitation legitimately has no subject id yet, and several of those
        // must be able to coexist.
        builder.HasIndex(user => new { user.AuthProvider, user.ExternalSubjectId })
            .IsUnique()
            .HasFilter("external_subject_id IS NOT NULL")
            .HasDatabaseName("ix_users_provider_subject");
    }
}

internal sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> builder)
    {
        builder.ToTable("patients");

        builder.HasKey(patient => patient.Id);
        builder.Property(patient => patient.Id).HasColumnName("id");
        builder.Property(patient => patient.UserId).HasColumnName("user_id").IsRequired();

        builder.Property(patient => patient.FullName)
            .HasColumnName("full_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(patient => patient.ContactEmail)
            .HasColumnName("contact_email")
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(patient => patient.ContactPhone)
            .HasColumnName("contact_phone")
            .HasMaxLength(40);

        builder.Property(patient => patient.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(patient => patient.DeletedAtUtc).HasColumnName("deleted_at_utc");

        builder.Ignore(patient => patient.IsDeleted);

        // 1:1 with User (02-domain-model.md). The relationship is declared without a
        // navigation property: Domain entities hold ids rather than object graphs, which
        // keeps the core free of lazy-loading behaviour it never asked for.
        builder.HasIndex(patient => patient.UserId)
            .IsUnique()
            .HasDatabaseName("ix_patients_user");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(patient => patient.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ConsentConfiguration : IEntityTypeConfiguration<Consent>
{
    public void Configure(EntityTypeBuilder<Consent> builder)
    {
        builder.ToTable("consents");

        builder.HasKey(consent => consent.Id);
        builder.Property(consent => consent.Id).HasColumnName("id");
        builder.Property(consent => consent.UserId).HasColumnName("user_id").IsRequired();

        builder.Property(consent => consent.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(consent => consent.Version)
            .HasColumnName("version")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(consent => consent.GrantedAtUtc).HasColumnName("granted_at_utc");
        builder.Property(consent => consent.RevokedAtUtc).HasColumnName("revoked_at_utc");

        builder.Ignore(consent => consent.IsActive);

        builder.HasIndex(consent => new { consent.UserId, consent.Type })
            .HasDatabaseName("ix_consents_user_type");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(consent => consent.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AccessLogConfiguration : IEntityTypeConfiguration<AccessLog>
{
    public void Configure(EntityTypeBuilder<AccessLog> builder)
    {
        builder.ToTable("access_log");

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).HasColumnName("id");
        builder.Property(entry => entry.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(entry => entry.PatientId).HasColumnName("patient_id").IsRequired();

        builder.Property(entry => entry.Action)
            .HasColumnName("action")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(entry => entry.OccurredAtUtc).HasColumnName("occurred_at_utc");

        // The question this table answers is "who looked at this patient's data, and when",
        // so that is the index.
        builder.HasIndex(entry => new { entry.PatientId, entry.OccurredAtUtc })
            .HasDatabaseName("ix_access_log_patient_time");

        builder.HasIndex(entry => entry.ActorUserId)
            .HasDatabaseName("ix_access_log_actor");

        // No cascade and no soft-delete marker: an audit trail that disappears with the
        // record it describes is not an audit trail.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");

        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).HasColumnName("id");

        builder.Property(session => session.TokenHash)
            .HasColumnName("token_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(session => session.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(session => session.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(session => session.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(session => session.RevokedAtUtc).HasColumnName("revoked_at_utc");

        // This index carries every authenticated request in the system (design A1), which is
        // the whole reason the per-request lookup is affordable.
        builder.HasIndex(session => session.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_sessions_token_hash");

        // Supports revoking every session a user holds — what disabling an account does.
        builder.HasIndex(session => session.UserId)
            .HasDatabaseName("ix_sessions_user");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
