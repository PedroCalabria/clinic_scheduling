using Clinic.Domain.Configuration;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Clinic.Api.Infrastructure.Persistence;

/// <summary>
/// Provisions a complete, runnable clinic in development, so every change from
/// <c>availability-read</c> on has something to demonstrate against (00-context.md §5, design E6).
/// </summary>
/// <remarks>
/// <para>
/// Two guards, and both are load-bearing. It runs only in the Development environment, and only
/// when explicitly enabled — an environment check alone would seed any machine somebody happened
/// to start in Development, which includes a laptop pointed at a shared database.
/// </para>
/// <para>
/// Everything is constructed through the same domain factories the API uses, never by writing
/// rows. That is the point rather than tidiness: a seed built from raw SQL can create data the
/// API would have refused — a duration outside a held specialty, an overlapping segment — and
/// then the demo shows a state the product cannot actually produce. Going through the factories
/// means this file fails loudly the moment it contradicts a rule, which makes it a second, cheap
/// test of those rules.
/// </para>
/// <para>
/// Idempotent by presence check, in the shape <c>AdministratorBootstrap</c> established: if the
/// marker specialty exists, nothing is touched, so an operator's edits survive a restart.
/// </para>
/// </remarks>
internal sealed class DevelopmentClinicSeed(
    IServiceProvider services,
    IHostEnvironment environment,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<DevelopmentClinicSeed> logger) : IHostedService
{
    /// <summary>Presence of this specialty means the clinic has already been seeded.</summary>
    private const string MarkerSpecialty = "Cardiologia";

    private const string ProfessionalEmail = "dra.helena@clinic.local";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            // Silent rather than logged: in production this is not a skipped step, it is a
            // component that has no business existing.
            return;
        }

        if (!configuration.GetValue("Clinic:SeedDevelopmentData", defaultValue: false))
        {
            logger.LogInformation(
                "Development clinic seed is off. Set Clinic__SeedDevelopmentData=true to provision a demo clinic.");

            return;
        }

        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        if (await database.Specialties.AnyAsync(s => s.Name == MarkerSpecialty, cancellationToken))
        {
            logger.LogInformation("Development clinic already seeded; leaving it as it is.");

            return;
        }

        var now = clock.GetUtcNow();

        // --- The catalog (3a's entities) ---------------------------------------------
        var cardiology = Specialty.Define(MarkerSpecialty, now);
        var dermatology = Specialty.Define("Dermatologia", now);

        var consultingRoom = ResourceType.Define("Consultório", bufferMinutes: 15, now);
        var ultrasoundRoom = ResourceType.Define("Sala de ultrassom", bufferMinutes: 20, now);

        database.Specialties.AddRange(cardiology, dermatology);
        database.ResourceTypes.AddRange(consultingRoom, ultrasoundRoom);

        database.Resources.AddRange(
            Resource.Define(consultingRoom.Id, "Consultório 1", now),
            Resource.Define(consultingRoom.Id, "Consultório 2", now),
            Resource.Define(ultrasoundRoom.Id, "Ultrassom 1", now));

        var cardiologyVisit = AppointmentType.Define(
            cardiology.Id, consultingRoom.Id, "Consulta cardiológica", now);

        var echocardiogram = AppointmentType.Define(
            cardiology.Id, ultrasoundRoom.Id, "Ecocardiograma", now);

        var dermatologyVisit = AppointmentType.Define(
            dermatology.Id, consultingRoom.Id, "Consulta dermatológica", now);

        database.AppointmentTypes.AddRange(cardiologyVisit, echocardiogram, dermatologyVisit);

        // --- The professional (this change's entities) --------------------------------
        // Invited exactly as S11 would: a user with the role and no credential, awaiting a
        // Google sign-in. The seed does not fake a claimed identity, because a professional who
        // has never signed in is the state S7 must handle (design E1).
        var user = await database.Users.FirstOrDefaultAsync(
            candidate => candidate.Email == ProfessionalEmail, cancellationToken);

        if (user is null)
        {
            user = User.InviteProfessional(ProfessionalEmail, now);
            database.Users.Add(user);
        }

        var professional = Professional.ForUser(user.Id, now);
        database.Professionals.Add(professional);

        // Qualified in cardiology only — deliberately NOT dermatology, so the gate is
        // immediately visible on S7 and change 4 has an ineligible professional to exclude.
        database.ProfessionalSpecialties.Add(
            ProfessionalSpecialty.Grant(professional.Id, cardiology.Id, now));

        // Two durations, different lengths: the whole point of Decision C in one screen.
        database.ProfessionalAppointmentTypes.AddRange(
            ProfessionalAppointmentType.Set(
                professional.Id, cardiologyVisit.Id, 40, professionalHoldsSpecialty: true, now),
            ProfessionalAppointmentType.Set(
                professional.Id, echocardiogram.Id, 30, professionalHoldsSpecialty: true, now));

        // A split working week, open-ended, from a date safely in the past so the schedule is
        // already effective whenever this runs.
        var effectiveFrom = new LocalDate(2026, 1, 1);

        var segments = new List<WorkingHoursTemplate>();

        foreach (var day in new[]
                 {
                     IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday,
                     IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday,
                 })
        {
            // Built through Define with the running list, so the seed is subject to the same
            // overlap rule as a human — if this ever produced a conflict, startup would say so.
            segments.Add(WorkingHoursTemplate.Define(
                professional.Id, day, new LocalTime(8, 0), new LocalTime(12, 0),
                effectiveFrom, null, segments, now));

            segments.Add(WorkingHoursTemplate.Define(
                professional.Id, day, new LocalTime(13, 0), new LocalTime(17, 0),
                effectiveFrom, null, segments, now));
        }

        database.WorkingHoursTemplates.AddRange(segments);

        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded a development clinic: {Specialties} specialties, {Types} appointment types, "
            + "and {Professional} with {Segments} working-hour segments.",
            2, 3, ProfessionalEmail, segments.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
