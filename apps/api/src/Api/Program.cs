using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Features.Auth;
using Clinic.Api.Features.Health;
using Clinic.Api.Features.Patients;
using Clinic.Api.Features.StaffAccounts;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Observability;
using Clinic.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

// --- Identity & session (Decision J, design A1) -------------------------------------
// Registers the session store, the password hasher, the custom authentication scheme, and
// the role policies — including the authenticated-by-default fallback, so an endpoint
// reaches the public internet only by saying AllowAnonymous out loud.
builder.Services.AddClinicAuth(builder.Configuration);
builder.Services.AddLoginRateLimiting();

// Registered AFTER the migration service so the schema exists when it runs (design A6).
// Idempotent, so it is safe on every boot rather than only the first.
builder.Services.AddHostedService<AdministratorBootstrap>();

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

app.Run();
