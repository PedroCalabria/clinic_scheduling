using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Clinic.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The scheduling core: the <c>appointments</c> table, and the three exclusion constraints
    /// that make "no double-booking" true rather than claimed (design B3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two halves, and only the first is generated.</b> EF describes the table; it does not
    /// model <c>EXCLUDE USING gist</c>, so the constraints below are hand-written. That means the
    /// schema's most important guarantee lives in SQL the model snapshot does not describe — which
    /// is why the constraint names are constants on <c>AppointmentConfiguration</c>, the booking
    /// slice maps refusals from them, and an integration test asserts they exist under those names.
    /// </para>
    /// <para>
    /// <b>Why the predicate has exactly one clause.</b> <c>WHERE status = 'Scheduled'</c>, and
    /// there is no soft-delete column to also test — deviating from the ERD in 02 §9, and argued
    /// in design B3. An appointment's history is reconstructible from its status, and
    /// <c>Cancelled</c> / <c>Rescheduled</c> / <c>NoShow</c> are richer facts than a deleted flag.
    /// A second, weaker way for a row to stop counting would have to be honoured here too, and
    /// <b>two sources of truth for "is this row live" is how an exclusion constraint becomes
    /// decorative</b>: the day they disagree, the constraint stops protecting the case the
    /// application believes it protects. One clause, matching <c>Appointment.IsLive</c> exactly.
    /// </para>
    /// <para>
    /// <b>Rollback</b> is dropping the table, which takes its constraints with it. The
    /// <c>btree_gist</c> extension is deliberately left in place: it is harmless, may be shared,
    /// and dropping an extension another object depends on is the kind of rollback that fails
    /// halfway.
    /// </para>
    /// </remarks>
    public partial class BookingCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Before the table, because the `=` operator class for uuid inside a GiST index comes
            // from this extension. Creating the constraints without it fails with a message about
            // a missing operator class, which reads as a type problem rather than a missing
            // extension.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.CreateTable(
                name: "appointments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time_range = table.Column<NpgsqlRange<DateTime>>(type: "tstzrange", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointments", x => x.id);
                    table.ForeignKey(
                        name: "FK_appointments_appointment_types_appointment_type_id",
                        column: x => x.appointment_type_id,
                        principalTable: "appointment_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_patients_patient_id",
                        column: x => x.patient_id,
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_professionals_professional_id",
                        column: x => x.professional_id,
                        principalTable: "professionals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_resources_resource_id",
                        column: x => x.resource_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_appointment_type_id",
                table: "appointments",
                column: "appointment_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_patient_id",
                table: "appointments",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_professional_id",
                table: "appointments",
                column: "professional_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_resource_id",
                table: "appointments",
                column: "resource_id");

            // --- The floor (I4, I5, I6) ----------------------------------------------------
            //
            // Three constraints rather than one, because they are three invariants with three
            // remedies, and PostgreSQL names the one that was violated — which is what lets the
            // booking slice answer `slot_taken`, `resource_unavailable` or `patient_busy` instead
            // of one unhelpful code for all three.
            //
            // `&&` is range overlap, and `time_range` holds `[)` ranges, so two appointments that
            // abut do NOT overlap — the same comparison BusyInterval makes in C#. If these two
            // ever disagreed, the database would refuse a pair the solver had just offered.
            //
            // The four indexes above are EF's foreign-key indexes and serve FK checks and
            // ordinary equality lookups. No further index is added for the window read: each
            // constraint below creates a GiST index, and the professional one is exactly the
            // (professional_id, time_range) access path the busy-interval query wants.

            // I4 — one professional cannot be in two appointments at once.
            migrationBuilder.Sql("""
                ALTER TABLE appointments
                    ADD CONSTRAINT appointments_professional_no_overlap
                    EXCLUDE USING gist (professional_id WITH =, time_range WITH &&)
                    WHERE (status = 'Scheduled');
                """);

            // I5 — one room cannot host two appointments at once, whoever books them. Note this
            // operates on the RAW range: the turnaround buffer is applied by the solver only, so
            // two exactly-abutting bookings in one room stay theoretically race-possible. A
            // conscious trade-off carried forward from 02 §4 (P-4), not an oversight.
            migrationBuilder.Sql("""
                ALTER TABLE appointments
                    ADD CONSTRAINT appointments_resource_no_overlap
                    EXCLUDE USING gist (resource_id WITH =, time_range WITH &&)
                    WHERE (status = 'Scheduled');
                """);

            // I6 — one patient cannot be in two places at once. This is also what makes a
            // double-submitted confirmation self-defending: the second request overlaps the
            // appointment the first one created.
            migrationBuilder.Sql("""
                ALTER TABLE appointments
                    ADD CONSTRAINT appointments_patient_no_overlap
                    EXCLUDE USING gist (patient_id WITH =, time_range WITH &&)
                    WHERE (status = 'Scheduled');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table takes its three exclusion constraints and their GiST indexes
            // with it. btree_gist is left installed on purpose — see the remarks above.
            migrationBuilder.DropTable(
                name: "appointments");
        }
    }
}
