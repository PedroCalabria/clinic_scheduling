using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Time;
using Clinic.Domain.Configuration;
using Clinic.Domain.Identity;
using Clinic.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    ClinicTimezone timezone,
    ClinicScheduling scheduling,
    IOptions<AuthOptions> auth,
    ILogger<DevelopmentClinicSeed> logger) : IHostedService
{
    /// <summary>Presence of this specialty means the clinic has already been seeded.</summary>
    private const string MarkerSpecialty = "Cardiologia";

    private const string ProfessionalEmail = "dra.helena@clinic.local";

    /// <summary>
    /// The demo patient whose appointments make the subtraction visible.
    /// </summary>
    /// <remarks>
    /// A non-existent domain, like the professional's, and for the same reason: this is fixture
    /// data that exercises the API and the solver, never the browser sign-in. Validating P2 in a
    /// browser needs a real Google account (00-context.md §9) — the seed cannot stand in for one.
    /// </remarks>
    private const string PatientEmail = "paciente.demo@clinic.local";

    private const string PatientGoogleSubject = "seed-patient-google-subject";

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
        var instantNow = Instant.FromDateTimeOffset(now);
        var consentVersion = auth.Value.ConsentVersion;

        // --- The catalog (3a's entities) ---------------------------------------------
        var cardiology = Specialty.Define(MarkerSpecialty, now);
        var dermatology = Specialty.Define("Dermatologia", now);

        var consultingRoom = ResourceType.Define("Consultório", bufferMinutes: 15, now);
        var ultrasoundRoom = ResourceType.Define("Sala de ultrassom", bufferMinutes: 20, now);

        database.Specialties.AddRange(cardiology, dermatology);
        database.ResourceTypes.AddRange(consultingRoom, ultrasoundRoom);

        // Held in locals from booking-core on, because the seeded appointments name a concrete
        // room — the server assigns one at booking (domain-model F2) and a seed is the server.
        var consultingRoomOne = Resource.Define(consultingRoom.Id, "Consultório 1", now);
        var consultingRoomTwo = Resource.Define(consultingRoom.Id, "Consultório 2", now);
        var ultrasoundOne = Resource.Define(ultrasoundRoom.Id, "Ultrassom 1", now);

        database.Resources.AddRange(consultingRoomOne, consultingRoomTwo, ultrasoundOne);

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

        // --- Blocked time (availability-read's entities) ------------------------------
        // Two blocks inside the working week, so the subtraction is visible on a fresh stack
        // without anybody filling in a form first. This is what makes change 4 demonstrable at
        // all: without a producer, availability would be a computation nobody could see removing
        // anything.
        //
        // Placed relative to "now" rather than on fixed dates, because a hard-coded date drifts
        // into the past and then subtracts nothing — a seeded fixture that silently stops
        // demonstrating its own feature is worse than none.
        var today = instantNow.InZone(timezone.Zone).Date;
        var nextMonday = today.Next(IsoDayOfWeek.Monday);

        database.TimeBlocks.AddRange(
            // A Monday morning off: overlaps the 08:00-12:00 segment, so those slots disappear
            // while the afternoon's remain.
            TimeBlock.ForProfessional(
                professional.Id,
                AtClinicTime(nextMonday, 9, 0),
                AtClinicTime(nextMonday, 11, 0),
                now),

            // A Tuesday lunch extension that abuts the afternoon segment exactly, so the 13:00
            // slot is still offered — the half-open rule, visible rather than asserted.
            TimeBlock.ForProfessional(
                professional.Id,
                AtClinicTime(nextMonday.PlusDays(1), 12, 0),
                AtClinicTime(nextMonday.PlusDays(1), 13, 0),
                now));

        // --- A patient and their appointments (booking-core's entities) ---------------
        // The seam made visible on a fresh stack: without a booked appointment, "a booked slot
        // stops being offered" is a claim only the test suite has ever seen. With one, the very
        // first availability search on a new stack shows a gap somebody's visit made.
        //
        // A Google patient, exactly as just-in-time provisioning creates one (design B12), including
        // the data-processing consent at the configured version — which the booking gate now reads,
        // so a seeded patient holding a stale version would be unable to book their own demo.
        var patientUser = User.RegisterGooglePatient(PatientEmail, PatientGoogleSubject, now);

        var patient = Patient.Register(patientUser.Id, "Paciente Demonstração", PatientEmail, now);

        database.Users.Add(patientUser);
        database.Patients.Add(patient);
        database.Consents.Add(Consent.Grant(
            patientUser.Id, ConsentType.DataProcessing, consentVersion, now));

        // Wednesday and Thursday, deliberately NOT the Monday and Tuesday the blocks occupy: a
        // block and an appointment on the same morning would prove nothing that either proves
        // alone, and the Wednesday appointment would be refused outright by the I7 check the block
        // path now performs (task 13.2).
        var wednesday = nextMonday.PlusDays(2);
        var thursday = nextMonday.PlusDays(3);

        database.Appointments.AddRange(
            // A cardiology visit mid-morning: 40 minutes, so the slots it removes are visibly
            // narrower than the hour a naive fixture would suggest.
            Appointment.Book(
                new AppointmentBooking(
                    patient.Id,
                    professional.Id,
                    consultingRoomOne.Id,
                    cardiologyVisit.Id,
                    AtClinicTime(wednesday, 9, 0),
                    DurationMinutes: 40,
                    ProfessionalHoldsDurationForType: true,
                    consultingRoom.Id,
                    cardiologyVisit.RequiredResourceTypeId,
                    AppointmentSource.SelfService),
                scheduling.Parameters,
                instantNow,
                now),

            // An echocardiogram in the ultrasound room, so the resource half of the subtraction
            // has something to show too — a different room, a different duration, and a turnaround
            // buffer of twenty minutes rather than fifteen.
            Appointment.Book(
                new AppointmentBooking(
                    patient.Id,
                    professional.Id,
                    ultrasoundOne.Id,
                    echocardiogram.Id,
                    AtClinicTime(thursday, 14, 0),
                    DurationMinutes: 30,
                    ProfessionalHoldsDurationForType: true,
                    ultrasoundRoom.Id,
                    echocardiogram.RequiredResourceTypeId,
                    AppointmentSource.SelfService),
                scheduling.Parameters,
                instantNow,
                now),

            // --- booking-lifecycle: the one that CANNOT be changed ---------------------
            // P5 shows the cancellation cutoff as a locked state, and a locked state nobody can
            // see is a claim rather than a screen. So the seed carries an appointment inside the
            // cutoff as well as two outside it, and the very first load of P5 on a fresh stack
            // shows both halves of domain-model F3.
            //
            // Placed a few hours from NOW rather than at a clinic wall-clock time on a fixed
            // date, and that is the whole point: "inside the cutoff" is a fact about the distance
            // to the present, so a fixed date would stop being inside it the following day and
            // the locked state would quietly disappear from the demo. Six hours clears the
            // default one-hour lead time comfortably and sits well inside the default 24.
            //
            // It may land outside the professional's working hours, which is harmless: the seed
            // constructs appointments through the factory, working hours are the solver's
            // concern, and no slot is offered there for it to contradict.
            Appointment.Book(
                new AppointmentBooking(
                    patient.Id,
                    professional.Id,
                    consultingRoomOne.Id,
                    cardiologyVisit.Id,
                    instantNow + Duration.FromHours(6),
                    DurationMinutes: 40,
                    ProfessionalHoldsDurationForType: true,
                    consultingRoom.Id,
                    cardiologyVisit.RequiredResourceTypeId,
                    AppointmentSource.SelfService),
                scheduling.Parameters,
                instantNow,
                now));

        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded a development clinic: {Specialties} specialties, {Types} appointment types, "
            + "{Professional} with {Segments} working-hour segments and {Blocks} blocked periods, "
            + "and {Patient} with {Appointments} appointments.",
            2, 3, ProfessionalEmail, segments.Count, 2, PatientEmail, 3);
    }

    /// <summary>A clinic wall-clock time on a date, as the instant a block stores.</summary>
    /// <remarks>
    /// Strict resolution, unlike the solver's lenient one: a seed is written by this project
    /// rather than typed by a clinic, so a chosen time landing in a daylight-saving gap is a bug
    /// in the seed and should fail startup loudly instead of being quietly shifted.
    /// </remarks>
    private Instant AtClinicTime(LocalDate date, int hour, int minute) =>
        timezone.Zone.AtStrictly(date.At(new LocalTime(hour, minute))).ToInstant();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
