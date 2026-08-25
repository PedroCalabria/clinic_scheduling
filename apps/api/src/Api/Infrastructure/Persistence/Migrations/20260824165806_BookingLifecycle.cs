using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The reschedule link — one nullable column, and three constraints deliberately untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a change that makes two new appointment states reachable alters no constraint.</b>
    /// A reader who has seen <c>BookingCore</c> will expect the three <c>EXCLUDE USING gist</c>
    /// constraints to need widening here, and they do not: their predicate is already
    /// <c>WHERE status = 'Scheduled'</c>, and both <c>Cancelled</c> and <c>Rescheduled</c> fall
    /// outside it. So a cancelled appointment leaves all three partial indexes the instant its
    /// status changes, with no migration and no second rule. That was 5a's design B10 being paid
    /// for, and stating it here is cheaper than the next person re-deriving it.
    /// </para>
    /// <para>
    /// <b>The index is not the redundant one 5a refused.</b> That change declined a fourth index
    /// because the professional exclusion constraint's GiST index already served the window read.
    /// This one is an equality lookup on a different column, which no existing index answers.
    /// </para>
    /// <para>
    /// <b>Additive, and a clean rollback.</b> Nothing has a non-null value yet and no prior
    /// change reads the column, so <c>Down</c> genuinely restores the previous schema rather than
    /// approximately restoring it. <c>ON DELETE RESTRICT</c> matches its four neighbours and
    /// describes an event that cannot happen — nothing deletes an appointment (I10).
    /// </para>
    /// </remarks>
    public partial class BookingLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "rescheduled_from_id",
                table: "appointments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_appointments_rescheduled_from_id",
                table: "appointments",
                column: "rescheduled_from_id");

            migrationBuilder.AddForeignKey(
                name: "FK_appointments_appointments_rescheduled_from_id",
                table: "appointments",
                column: "rescheduled_from_id",
                principalTable: "appointments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_appointments_appointments_rescheduled_from_id",
                table: "appointments");

            migrationBuilder.DropIndex(
                name: "IX_appointments_rescheduled_from_id",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "rescheduled_from_id",
                table: "appointments");
        }
    }
}
