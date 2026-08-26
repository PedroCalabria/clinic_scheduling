using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseIdentityOnDeactivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_provider_subject",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "ix_users_provider_subject",
                table: "users",
                columns: new[] { "auth_provider", "external_subject_id" },
                unique: true,
                filter: "external_subject_id IS NOT NULL AND deleted_at_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_provider_subject",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "ix_users_provider_subject",
                table: "users",
                columns: new[] { "auth_provider", "external_subject_id" },
                unique: true,
                filter: "external_subject_id IS NOT NULL");
        }
    }
}
