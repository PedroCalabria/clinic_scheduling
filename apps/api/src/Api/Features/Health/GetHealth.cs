using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Clinic.Api.Features.Health;

/// <summary>
/// The <c>GET /api/health</c> slice — the shape every later feature follows
/// (Decision K, 00-context.md §3): one folder owning its endpoint, handler, and
/// response contract, with the endpoint calling the handler directly. No MediatR.
/// </summary>
/// <remarks>
/// The route is <c>/api/health</c>, not <c>/health</c>: Caddy proxies <c>/api/*</c>
/// without stripping the prefix (design D2), so the API owns the full public path and
/// the same relative URL works through Caddy, through the Vite dev proxy, and in tests.
/// </remarks>
internal static class GetHealth
{
    /// <summary>Name of the database check, as reported in the response.</summary>
    internal const string DatabaseCheckName = "database";

    internal static IEndpointRouteBuilder MapGetHealth(this IEndpointRouteBuilder endpoints)
    {
        // AllowAnonymous is explicit: the health endpoint stays reachable once
        // authentication lands in change 2 (identity-session).
        endpoints.MapGet("/api/health", HandleAsync)
            .AllowAnonymous()
            .WithName("GetHealth");

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HealthCheckService healthChecks,
        CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(cancellationToken);

        var response = new HealthResponse(
            Status: report.Status.ToString(),
            Checks: report.Entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Status.ToString()));

        // Healthy -> 200; Degraded or Unhealthy -> 503. A caller (or Compose healthcheck)
        // can rely on the status code alone without parsing the body.
        var statusCode = report.Status == HealthStatus.Healthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;

        return Results.Json(response, statusCode: statusCode);
    }

    /// <summary>
    /// Deliberately minimal: status strings only.
    /// </summary>
    /// <remarks>
    /// The framework's report entries also carry exception details, descriptions, and
    /// durations. Those are NOT projected here — the endpoint is anonymous and publicly
    /// reachable through Caddy, so the body must never disclose a connection string,
    /// credentials, a host name, or a stack trace. Failure detail goes to the logs
    /// (correlated), not to the caller.
    /// </remarks>
    private sealed record HealthResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("checks")] IReadOnlyDictionary<string, string> Checks);
}
