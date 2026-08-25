using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Features.Auth;
using Clinic.Api.Features.Availability;
using Clinic.Api.Features.Booking;
using Clinic.Api.Features.Health;
using Clinic.Api.Features.Patients;
using Clinic.Api.Features.StaffAccounts;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Observability;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Api.Infrastructure.Scheduling;
using Clinic.Api.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// --- Observability (03-nfr.md §4) ---------------------------------------------------
// Structured JSON to stdout; the container runtime owns log shipping. No heavy stack.
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

// --- Persistence (Decision L) -------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Default");

if (string.IsNullOrWhiteSpace(connectionString))
{
    // Fail loudly at startup rather than surfacing an opaque Npgsql error on first request.
    throw new InvalidOperationException(
        "Connection string 'Default' is not configured. Set ConnectionStrings__Default — see .env.example.");
}

builder.Services.AddDbContext<ClinicDbContext>(options => options.UseNpgsql(connectionString));

// Migrations run at host startup, before traffic is served (design D5). Registered first
// so it precedes every other hosted service.
builder.Services.AddHostedService<DatabaseMigrationStartupService>();

// --- Time (Decision H) --------------------------------------------------------------
// Required configuration with no default, validated against the zone database before the
// app serves traffic. A wrong-but-plausible zone id would otherwise surface in change 4 as
// appointments an hour out, not as an error (design E3).
builder.Services.AddOptions<ClinicTimeOptions>()
    .Bind(builder.Configuration.GetSection(ClinicTimeOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<ClinicTimeOptions>, ClinicTimeOptionsValidator>();
builder.Services.AddSingleton<ClinicTimezone>();

// --- Scheduling policy (design F8) --------------------------------------------------
// Defaults here, unlike the timezone above: a 15-minute slot step is right until a clinic
// says otherwise, whereas a timezone default is wrong for every clinic but one. Range
// attributes plus ValidateOnStart still refuse a nonsensical value at startup.
builder.Services.AddOptions<SchedulingOptions>()
    .Bind(builder.Configuration.GetSection(SchedulingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<ClinicScheduling>();

// --- The one schedule read (design B11) ---------------------------------------------
// Scoped, because it holds the request's DbContext — and that matters more than usual here:
// the booking path runs it inside a transaction it has already begun, so the appointment
// overlap query sees the snapshot the insert will be committed against. Shared by the
// availability read and the booking check so the two cannot see different busy sets.
builder.Services.AddScoped<ScheduleReader>();

// --- Identity & session (Decision J, design A1) -------------------------------------
// Registers the session store, the password hasher, the custom authentication scheme, and
// the role policies — including the authenticated-by-default fallback, so an endpoint
// reaches the public internet only by saying AllowAnonymous out loud.
builder.Services.AddClinicAuth(builder.Configuration);
builder.Services.AddLoginRateLimiting();

// The second limiter Decision R anticipated, on the endpoint 03-nfr.md §2 names as the
// abusable surface. Separate policy, separate budget, shared rejection envelope.
builder.Services.AddAvailabilityRateLimiting();

// Registered AFTER the migration service so the schema exists when it runs (design A6).
// Idempotent, so it is safe on every boot rather than only the first.
builder.Services.AddHostedService<AdministratorBootstrap>();

// A runnable demo clinic, development only and opt-in (design E6). Registered last, so both
// the schema and the administrator exist before it runs.
builder.Services.AddHostedService<DevelopmentClinicSeed>();

// --- Health checks ------------------------------------------------------------------
// AddDbContextCheck issues a real CanConnect against PostgreSQL, so the check covers
// actual database reachability rather than merely that configuration was parsed.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ClinicDbContext>(name: GetHealth.DatabaseCheckName);

var app = builder.Build();

// --- Pipeline -----------------------------------------------------------------------
// Correlation first (outermost) so every later log entry, including the error
// envelope's, carries the id.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ErrorEnvelopeMiddleware>();
app.UseSerilogRequestLogging();

// Who the caller is, then what they may do. Both before the endpoints, and after the error
// envelope so an authentication failure is still reported as { code } (design A1).
app.UseAuthentication();

// After authentication, because it reads the principal: an account still holding its
// bootstrap credential is held to replacing it (design A6).
app.UseMiddleware<PasswordChangeGate>();

// Before authorization so a forged state-changing request is refused whether or not its
// session would have been permitted (design A3).
app.UseMiddleware<CsrfMiddleware>();

app.UseAuthorization();

// Applies only where an endpoint asks for it by policy name (design A10).
app.UseRateLimiter();

app.MapGetHealth();
app.MapAuthEndpoints();
app.MapPatientEndpoints();
app.MapStaffAccountEndpoints();

// The clinic catalog (S8-S10). Four groups rather than one, because their rules differ where
// it matters — see the change's design, decision D4.
app.MapSpecialtyEndpoints();
app.MapResourceTypeEndpoints();
app.MapResourceEndpoints();
app.MapAppointmentTypeEndpoints();
app.MapProfessionalEndpoints();

// Availability (change 4). The read has no screen of its own until P2 lands in change 5; S3 is
// the surface, and it is what gives the read's subtraction something real to subtract.
app.MapAvailabilityEndpoints();
app.MapTimeBlockEndpoints();

// Booking (change 5a). The write that makes the read's promise true: P3 commits here, and the
// three exclusion constraints behind it are what make "no double-booking" a property of the
// schema rather than of the code above it.
app.MapBookingOptionsEndpoints();
app.MapBookingEndpoints();
app.MapAppointmentLifecycleEndpoints();

app.Run();
