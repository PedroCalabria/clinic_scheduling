using System.Text.Json.Serialization;

namespace Clinic.Api.Infrastructure.Errors;

/// <summary>
/// The API's only error shape (Decision I, catalogue in docs/07-error-codes.md):
/// <c>{ "code": "domain.problem", "params": { ... } }</c>.
/// </summary>
/// <remarks>
/// The API never returns translated prose — the frontend maps <see cref="Code"/> to an
/// i18n key and interpolates <see cref="Params"/>. Adding a new code means adding it to
/// docs/07-error-codes.md first, with matching pt-BR + en keys as part of that change's
/// Definition of Done. Never invent per-slice shapes.
/// </remarks>
internal sealed record ErrorResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("params")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, object?>? Params = null);

/// <summary>
/// Error codes used by this change. The full catalogue lives in docs/07-error-codes.md.
/// </summary>
internal static class ErrorCodes
{
    /// <summary>Unhandled error — 500. Never leaks internals.</summary>
    internal const string ServerUnexpected = "server.unexpected";
}
