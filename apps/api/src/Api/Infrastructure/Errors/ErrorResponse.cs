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
/// Error codes this API emits. The full catalogue lives in docs/07-error-codes.md, and a
/// code goes in there BEFORE it appears here — the matching pt-BR and en keys are part of
/// the same change's Definition of Done.
/// </summary>
internal static class ErrorCodes
{
    /// <summary>Session missing, expired, revoked, or unrecognized — 401.</summary>
    internal const string SessionExpired = "auth.session_expired";

    /// <summary>Authenticated, but the role lacks the permission — 403.</summary>
    internal const string Forbidden = "auth.forbidden";

    /// <summary>A patient reaching data that is not theirs — 403.</summary>
    internal const string OwnershipDenied = "auth.ownership_denied";

    /// <summary>
    /// Wrong password OR unknown email — 401, deliberately the same code for both so the
    /// response never answers whether an account exists.
    /// </summary>
    internal const string InvalidCredentials = "auth.invalid_credentials";

    /// <summary>Account disabled by an administrator, or locked by the failed-attempt guard — 403.</summary>
    internal const string AccountDisabled = "auth.account_disabled";

    /// <summary>
    /// The bootstrap credential is still in place and must be replaced before anything else
    /// — 403 (design A6).
    /// </summary>
    internal const string PasswordChangeRequired = "auth.password_change_required";

    /// <summary>Too many login attempts — 429.</summary>
    internal const string RateLimited = "auth.rate_limited";

    /// <summary>The Google flow failed: bad state or nonce, invalid token, unverified email — 401.</summary>
    internal const string GoogleFailed = "auth.google_failed";

    /// <summary>
    /// No Google client is configured for this deployment, so the federated path is off — 503
    /// (design A14). Distinct from <see cref="GoogleFailed"/>: nothing the caller did is wrong.
    /// </summary>
    internal const string GoogleUnavailable = "auth.google_unavailable";

    /// <summary>A required consent has not been granted — 422.</summary>
    internal const string ConsentRequired = "auth.consent_required";

    /// <summary>Staff account creation with an email another user already holds — 409.</summary>
    internal const string EmailAlreadyInUse = "auth.email_already_in_use";

    /// <summary>An administrator acted on a staff account that does not exist — 404.</summary>
    internal const string AccountNotFound = "auth.account_not_found";

    /// <summary>
    /// Staff asked for a patient record that does not exist — 404. A patient never sees this;
    /// they get <see cref="OwnershipDenied"/>, so the response cannot be used to discover
    /// which records exist.
    /// </summary>
    internal const string PatientNotFound = "patient.not_found";

    /// <summary>Malformed or missing required field — 400.</summary>
    internal const string ValidationRequired = "validation.required";

    /// <summary>Field present but unusable — 400.</summary>
    internal const string ValidationInvalidFormat = "validation.invalid_format";

    /// <summary>
    /// A catalog entity cannot be retired while active records still reference it — 409
    /// (added in <c>clinic-catalog</c>).
    /// </summary>
    internal const string ConfigInUse = "config.in_use";

    /// <summary>An active catalog entity of that kind already holds the name — 409.</summary>
    internal const string ConfigDuplicateName = "config.duplicate_name";

    /// <summary>
    /// A catalog entity does not exist — 404. Also covers "exists but is inactive", because
    /// from the perspective of active data those are the same answer (design D5).
    /// </summary>
    internal const string ConfigNotFound = "config.not_found";

    /// <summary>Unhandled error — 500. Never leaks internals.</summary>
    internal const string ServerUnexpected = "server.unexpected";
}
