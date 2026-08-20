using Clinic.Api.Features.Health;
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

app.MapGetHealth();

app.Run();
