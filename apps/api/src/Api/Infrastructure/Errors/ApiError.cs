using System.Net.Mime;
using System.Text.Json;

namespace Clinic.Api.Infrastructure.Errors;

/// <summary>
/// The one way this API says no: a catalogue code and a status, in the
/// <see cref="ErrorResponse"/> envelope (Decision I, docs/07-error-codes.md).
/// </summary>
/// <remarks>
/// Two entry points because refusals happen in two places. Endpoints return an
/// <see cref="IResult"/>; middleware, authentication handlers, and the rate limiter's
/// rejection callback only have an <see cref="HttpResponse"/> — and those are exactly the
/// places where a framework default would otherwise emit an empty body, leaving the
/// frontend nothing to translate.
/// </remarks>
internal static class ApiError
{
    internal static IResult Result(
        string code,
        int statusCode,
        IReadOnlyDictionary<string, object?>? parameters = null) =>
        Results.Json(new ErrorResponse(code, parameters), statusCode: statusCode);

    /// <summary>Writes the envelope directly, for code that has no <see cref="IResult"/> pipeline.</summary>
    internal static async Task WriteAsync(
        HttpResponse response,
        string code,
        int statusCode,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (response.HasStarted)
        {
            // Nothing useful left to do; the caller already has a partial response.
            return;
        }

        response.Clear();
        response.StatusCode = statusCode;
        response.ContentType = MediaTypeNames.Application.Json;

        await JsonSerializer.SerializeAsync(
            response.Body,
            new ErrorResponse(code, parameters),
            cancellationToken: cancellationToken);
    }
}
