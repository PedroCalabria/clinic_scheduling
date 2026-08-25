using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Persistence.Configurations;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The reschedule statement ordering, asserted at the SQL level (design C2, 02 §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists rather than a comment on the handler's test.</b> The near-move test in
/// <c>AppointmentLifecycleTests</c> was written believing it would fail if the handler wrote its
/// two statements in the wrong order. It does not — and that was found by deliberately reversing
/// them and watching every test still pass.
/// </para>
/// <para>
/// The reason is that EF Core does not emit statements in the order the code calls them. It builds
/// a command batch and orders it itself, and for a self-referencing insert whose foreign key points
/// at the row being updated it happens to emit the <c>UPDATE</c> first regardless. So the handler
/// is protected by an EF implementation detail, not by its own code — which is exactly the kind of
/// thing that holds until an EF upgrade, a batching-configuration change, or somebody splitting the
/// call differently.
/// </para>
/// <para>
/// So the rule is asserted where it is actually true — <b>in the database, against raw SQL</b> —
/// and the handler pins the order explicitly with two <c>SaveChanges</c> calls so that it does not
/// depend on the detail either. The first test proves the wrong order genuinely fails; the second
/// proves the right order genuinely succeeds. Together they are the reason C2 is a real constraint
/// rather than a story.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RescheduleOrderingTests(ApiFixture fixture)
{
    private ClinicBuilder Clinic => new(fixture);

    [Fact]
    public async Task Inserting_the_replacement_before_terminating_the_original_is_refused()
    {
        // THE ASSERTION THE WHOLE DESIGN DECISION RESTS ON. If this ever passes, the exclusion
        // constraints have been made deferrable or their predicate has been widened, and the
        // handler's careful ordering has silently become decoration.
        var (original, replacement) = await ArrangeNearMoveAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(original, replacement, terminateFirst: false));

        Assert.Equal("23P01", failure.SqlState);

        // WHICH constraint is not asserted, and that is deliberate — the same lesson booking-core
        // recorded for its concurrency tests. A near move overlaps on all three at once: the same
        // patient, the same professional, and the same room. PostgreSQL reports whichever it
        // checks first, which is a function of index order rather than of anything this project
        // decides, so pinning one name would be asserting an implementation detail of the
        // database.
        //
        // What matters is that it is refused at all. That is why the fault is invisible for a far
        // move: every one of these is an OVERLAP constraint, and a move to another day overlaps
        // nothing.
        Assert.Contains(
            failure.ConstraintName,
            new[]
            {
                AppointmentConfiguration.PatientExclusion,
                AppointmentConfiguration.ProfessionalExclusion,
                AppointmentConfiguration.ResourceExclusion,
            });
    }

    [Fact]
    public async Task Terminating_the_original_first_lets_the_replacement_in()
    {
        var (original, replacement) = await ArrangeNearMoveAsync();

        await ApplyAsync(original, replacement, terminateFirst: true);

        await fixture.WithDatabaseAsync(async database =>
        {
            Assert.Equal(
                AppointmentStatus.Rescheduled,
                (await database.Appointments.AsNoTracking().SingleAsync(a => a.Id == original)).Status);

            Assert.Equal(
                AppointmentStatus.Scheduled,
                (await database.Appointments.AsNoTracking().SingleAsync(a => a.Id == replacement.Id)).Status);
        });
    }

    [Fact]
    public async Task A_far_move_survives_the_wrong_order_which_is_why_the_near_move_is_the_test()
    {
        // The control, and the point of the whole exercise. Written so that nobody "simplifies" the
        // near-move fixture into a comfortable next-week value and deletes the coverage without
        // deleting a test.
        var (original, replacement) = await ArrangeNearMoveAsync(offsetHours: 2);

        await ApplyAsync(original, replacement, terminateFirst: false);

        await fixture.WithDatabaseAsync(async database =>
            Assert.Equal(
                AppointmentStatus.Scheduled,
                (await database.Appointments.AsNoTracking().SingleAsync(a => a.Id == replacement.Id)).Status));
    }

    /// <summary>
    /// A booked appointment plus the replacement a move would produce, not yet applied.
    /// </summary>
    /// <param name="offsetHours">
    /// How far the replacement moves. The default of a quarter of an hour makes the two ranges
    /// overlap, which is the only case that exercises the ordering.
    /// </param>
    private async Task<(Guid Original, Appointment Replacement)> ArrangeNearMoveAsync(double offsetHours = 0.25)
    {
        var clinic = await Clinic.BuildAsync(durationMinutes: 60);
        var (patient, user) = await fixture.AsRoleAsync(Role.Patient);
        using var _patient = patient;

        var patientId = await Clinic.PatientIdAsync(user.Id);
        var start = ClinicBuilder.At(clinic.Date, 9);

        var booked = await patient.PostAsync(
            "/api/appointments",
            new
            {
                appointmentTypeId = clinic.AppointmentTypeId,
                professionalId = clinic.ProfessionalId,
                startsAt = ClinicBuilder.Utc(start),
            });

        await ClinicBuilder.Succeeds(Task.FromResult(booked));

        var originalId = (await ClinicBuilder.Body(booked)).GetProperty("id").GetGuid();

        var replacement = Appointment.Book(
            new AppointmentBooking(
                patientId,
                clinic.ProfessionalId,
                clinic.RoomId,
                clinic.AppointmentTypeId,
                start + Duration.FromMinutes((int)(offsetHours * 60)),
                clinic.DurationMinutes,
                ProfessionalHoldsDurationForType: true,
                clinic.ResourceTypeId,
                clinic.ResourceTypeId,
                AppointmentSource.SelfService),
            SchedulingParameters.Of(15, 60, 60),
            SystemClock.Instance.GetCurrentInstant(),
            DateTimeOffset.UtcNow);

        return (originalId, replacement);
    }

    /// <summary>
    /// Applies the two statements in the given order, as raw SQL in one transaction.
    /// </summary>
    /// <remarks>
    /// Raw SQL rather than EF, precisely because EF's own batch ordering is the thing this file
    /// exists to stop relying on. What is asserted here is what PostgreSQL does with two statements
    /// in a given order, which is a fact about the schema and survives every framework choice above
    /// it.
    /// </remarks>
    private async Task ApplyAsync(Guid original, Appointment replacement, bool terminateFirst)
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        await using var transaction = await database.Database.BeginTransactionAsync();

        var connection = database.Database.GetDbConnection();
        var tx = transaction.GetDbTransaction();

        async Task Terminate() =>
            await connection.ExecuteAsync(new CommandDefinition(
                "update appointments set status = @status where id = @id",
                new { status = nameof(AppointmentStatus.Rescheduled), id = original },
                tx));

        async Task Insert() =>
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into appointments
                    (id, patient_id, professional_id, resource_id, appointment_type_id,
                     time_range, status, source, rescheduled_from_id, created_at_utc)
                values
                    (@id, @patientId, @professionalId, @resourceId, @appointmentTypeId,
                     tstzrange(@from, @to, '[)'), @status, @source, @rescheduledFrom, @createdAt)
                """,
                new
                {
                    id = replacement.Id,
                    patientId = replacement.PatientId,
                    professionalId = replacement.ProfessionalId,
                    resourceId = replacement.ResourceId,
                    appointmentTypeId = replacement.AppointmentTypeId,
                    from = replacement.StartsAt.ToDateTimeUtc(),
                    to = replacement.EndsAt.ToDateTimeUtc(),
                    status = nameof(AppointmentStatus.Scheduled),
                    source = replacement.Source.ToString(),
                    rescheduledFrom = original,
                    createdAt = DateTimeOffset.UtcNow,
                },
                tx));

        if (terminateFirst)
        {
            await Terminate();
            await Insert();
        }
        else
        {
            await Insert();
            await Terminate();
        }

        await transaction.CommitAsync();
    }
}
