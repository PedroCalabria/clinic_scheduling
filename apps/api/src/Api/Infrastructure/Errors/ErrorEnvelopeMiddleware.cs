using System.Net.Mime;

namespace Clinic.Api.Infrastructure.Errors;

/// <summary>
/// Converts an unhandled exception into the <c>{ code, params? }</c> envelope
/// (task 2.6, docs/07-error-codes.md).
/// </summary>
/// <remarks>
/// Written as explicit middleware rather than <c>UseExceptionHandler</c> +
/// <c>AddProblemDetails</c> because this project's error contract is deliberately NOT
/// RFC 7807 ProblemDetails — it is a stable machine-readable code the frontend translates.
/// Routing a custom shape through the ProblemDetails pipeline means fighting it; a small
/// explicit middleware makes the contract exact and obvious.
///
/// The exception is logged in full (with the correlation id, since this sits inside
/// <c>CorrelationIdMiddleware</c>) and the response body carries only the code — no
/// message, no stack trace, no connection details.
/// </remarks>
internal sealed class ErrorEnvelopeMiddleware(RequestDelegate next, ILogger<ErrorEnvelopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled exception while handling {Method} {Path}.",
                context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted)
            {
                // Too late to rewrite the response; the log entry above is the record.
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = MediaTypeNames.Application.Json;

            await context.Response.WriteAsJsonAsync(new ErrorResponse(ErrorCodes.ServerUnexpected));
        }
    }
}
