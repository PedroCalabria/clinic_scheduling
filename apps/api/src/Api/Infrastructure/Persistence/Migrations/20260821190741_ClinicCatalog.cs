using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClinicCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resource_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    buffer_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "specialties",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_specialties", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resources", x => x.id);
                    table.ForeignKey(
                        name: "FK_resources_resource_types_resource_type_id",
                        column: x => x.resource_type_id,
                        principalTable: "resource_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "appointment_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    specialty_id = table.Column<Guid>(type: "uuid", nullable: false),
                    required_resource_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointment_types", x => x.id);
                    table.ForeignKey(
                        name: "FK_appointment_types_resource_types_required_resource_type_id",
                        column: x => x.required_resource_type_id,
                        principalTable: "resource_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointment_types_specialties_specialty_id",
                        column: x => x.specialty_id,
                        principalTable: "specialties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_appointment_types_name",
                table: "appointment_types",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_appointment_types_required_resource_type_id",
                table: "appointment_types",
                column: "required_resource_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointment_types_specialty_id",
                table: "appointment_types",
                column: "specialty_id");

            migrationBuilder.CreateIndex(
                name: "IX_resource_types_name",
                table: "resource_types",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_resources_name",
                table: "resources",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_resources_resource_type_id",
                table: "resources",
                column: "resource_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_specialties_name",
                table: "specialties",
                column: "name");

            // Uniqueness is on lower(name) among ACTIVE rows only (design D3), and EF's fluent
            // API cannot express an index over an expression — so these four are hand-written.
            //
            // Two halves, both load-bearing. lower(name) makes "Cardiologia" and "cardiologia"
            // one specialty, because they are one specialty to every human who will read the
            // dropdown. The WHERE clause scopes it to active rows, which is what lets a retired
            // name be used again and a retirement therefore be reversible without stranding the
            // name forever.
            //
            // This is the one catalog rule with a real database floor. The in-use rule has none
            // available to it — soft-delete keeps the referenced row present, so a foreign key
            // is satisfied forever (see design, Context).
            foreach (var table in new[] { "specialties", "resource_types", "resources", "appointment_types" })
            {
                migrationBuilder.Sql(
                    $"""
                    CREATE UNIQUE INDEX "UX_{table}_active_name"
                        ON "{table}" (lower(name))
                        WHERE deactivated_at_utc IS NULL;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "specialties", "resource_types", "resources", "appointment_types" })
            {
                migrationBuilder.Sql($"""DROP INDEX IF EXISTS "UX_{table}_active_name";""");
            }

            migrationBuilder.DropTable(
                name: "appointment_types");

            migrationBuilder.DropTable(
                name: "resources");

            migrationBuilder.DropTable(
                name: "specialties");

            migrationBuilder.DropTable(
                name: "resource_types");
        }
    }
}
