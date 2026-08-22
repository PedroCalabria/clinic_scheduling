using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProfessionalConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "professionals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_professionals", x => x.id);
                    table.ForeignKey(
                        name: "FK_professionals_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "professional_appointment_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_professional_appointment_types", x => x.id);
                    table.ForeignKey(
                        name: "FK_professional_appointment_types_appointment_types_appointmen~",
                        column: x => x.appointment_type_id,
                        principalTable: "appointment_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_professional_appointment_types_professionals_professional_id",
                        column: x => x.professional_id,
                        principalTable: "professionals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "professional_specialties",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_id = table.Column<Guid>(type: "uuid", nullable: false),
                    specialty_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_professional_specialties", x => x.id);
                    table.ForeignKey(
                        name: "FK_professional_specialties_professionals_professional_id",
                        column: x => x.professional_id,
                        principalTable: "professionals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_professional_specialties_specialties_specialty_id",
                        column: x => x.specialty_id,
                        principalTable: "specialties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "working_hours_exceptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_working_hours_exceptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_working_hours_exceptions_professionals_professional_id",
                        column: x => x.professional_id,
                        principalTable: "professionals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "working_hours_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_working_hours_templates", x => x.id);
                    table.ForeignKey(
                        name: "FK_working_hours_templates_professionals_professional_id",
                        column: x => x.professional_id,
                        principalTable: "professionals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_professional_appointment_types_appointment_type_id",
                table: "professional_appointment_types",
                column: "appointment_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_professional_appointment_types_professional_id_appointment_~",
                table: "professional_appointment_types",
                columns: new[] { "professional_id", "appointment_type_id" },
                unique: true,
                filter: "deactivated_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_professional_specialties_professional_id_specialty_id",
                table: "professional_specialties",
                columns: new[] { "professional_id", "specialty_id" },
                unique: true,
                filter: "deactivated_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_professional_specialties_specialty_id",
                table: "professional_specialties",
                column: "specialty_id");

            migrationBuilder.CreateIndex(
                name: "IX_professionals_user_id",
                table: "professionals",
                column: "user_id",
                unique: true,
                filter: "deactivated_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_working_hours_exceptions_professional_id_date",
                table: "working_hours_exceptions",
                columns: new[] { "professional_id", "date" },
                unique: true,
                filter: "deactivated_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_working_hours_templates_professional_id_day_of_week",
                table: "working_hours_templates",
                columns: new[] { "professional_id", "day_of_week" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "professional_appointment_types");

            migrationBuilder.DropTable(
                name: "professional_specialties");

            migrationBuilder.DropTable(
                name: "working_hours_exceptions");

            migrationBuilder.DropTable(
                name: "working_hours_templates");

            migrationBuilder.DropTable(
                name: "professionals");
        }
    }
}
