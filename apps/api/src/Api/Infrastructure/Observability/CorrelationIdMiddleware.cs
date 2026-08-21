using Serilog.Context;

namespace Clinic.Api.Infrastructure.Observability;

/// <summary>
/// Establishes a correlation id for every request (design D8, 00-context.md §5).
/// </summary>
/// <remarks>
/// Reads <c>X-Correlation-ID</c> from the inbound request, generates one when absent,
/// pushes it into Serilog's <see cref="LogContext"/> for the request scope, and echoes it
/// in the response. Because the property lives in the ambient log context, every log entry
/// written while handling the request carries it without any call site passing it along.
///
/// Registered outermost so that the error-envelope middleware's logs are also correlated.
/// The same mechanism extends to Hangfire jobs and webhook handlers in changes 6-8, which
/// is why it is worth establishing now on a request path that has nothing to correlate yet.
///
/// <c>X-Correlation-ID</c> was chosen over W3C <c>traceparent</c>: trace context buys
/// distributed-tracing interoperability this project has no consumer for (single
/// deployable, no tracing backend — 03-nfr.md §4 keeps observability proportional).
/// </remarks>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    internal const string HeaderName = "X-Correlation-ID";
    internal const string LogPropertyName = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[LogPropertyName] = correlationId;

        // Set via OnStarting so the header is present even when a downstream component
        // (including the error-envelope middleware) writes the response.
        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            ctx.Response.Headers[HeaderName] = (string?)ctx.Items[LogPropertyName];
            return Task.CompletedTask;
        }, context);

        using (LogContext.PushProperty(LogPropertyName, correlationId))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        var inbound = context.Request.Headers[HeaderName].FirstOrDefault();

        return string.IsNullOrWhiteSpace(inbound)
            ? Guid.NewGuid().ToString()
            : inbound;
    }
}
