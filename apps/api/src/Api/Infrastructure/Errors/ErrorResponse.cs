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

    /// <summary>
    /// The current password offered while changing a password does not match — 401.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="InvalidCredentials"/> on purpose. That code's message names the
    /// email as well as the password, which is right on a sign-in form and wrong on the
    /// change-password screen, where there is no email field and the remedy is one field rather
    /// than two. A shared code forced a message that told the user to check something that was
    /// not on screen.
    /// </remarks>
    internal const string CurrentPasswordInvalid = "auth.current_password_invalid";

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

    /// <summary>
    /// A Google sign-in started from the staff surface by an address the clinic has not
    /// registered — 403 (added in <c>staff-google-guard</c>).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="GoogleFailed"/>, and the distinction is the remedy: there the
    /// sign-in itself went wrong and the user may have a password to try instead, here the
    /// token is perfectly valid and there is simply nothing to claim. The only way forward is
    /// for an administrator to register the address, which is what the message says.
    /// <para>
    /// Delivered as a redirect carrying the code, not as a 403 body — the callback is reached
    /// by a top-level navigation, so a JSON body would land in the address bar. 403 is the
    /// status this code MEANS, the same way <see cref="GoogleUnavailable"/> means 503 and is
    /// also delivered as a redirect (design D3).
    /// </para>
    /// </remarks>
    internal const string NotProvisioned = "auth.not_provisioned";

    /// <summary>
    /// A patient account arrived at the staff sign-in — 403 (added in
    /// <c>staff-google-guard</c>).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="NotProvisioned"/> because the remedy is different and that is
    /// the whole basis on which this catalogue splits codes: there, nobody has registered the
    /// address and administration has to; here, the address is perfectly good and simply belongs
    /// to the other door. Telling this visitor to "ask administration" would send them away to
    /// be told nothing was wrong.
    /// </remarks>
    internal const string UsePatientSignIn = "auth.use_patient_sign_in";

    /// <summary>
    /// A professional's account arrived at the patient portal — 403 (added in
    /// <c>staff-google-guard</c>).
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="UsePatientSignIn"/>, and the reason the surface rule is stated
    /// as "each surface admits the role it serves" rather than as a guard bolted onto S0. An
    /// internal account reaching the portal is refused earlier and differently
    /// (<see cref="GoogleFailed"/>) — that one is the account-takeover defence, not a wrong-door
    /// mistake.
    /// </remarks>
    internal const string UseStaffSignIn = "auth.use_staff_sign_in";

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

    /// <summary>
    /// A per-type duration was set for an appointment type whose specialty the professional
    /// does not hold — 422. The gate behind invariant I2 (added in
    /// <c>professional-configuration</c>).
    /// </summary>
    internal const string ConfigSpecialtyNotHeld = "config.specialty_not_held";

    /// <summary>
    /// A working-hour segment collides with one already stored, or a date already carries an
    /// exception — 409.
    /// </summary>
    internal const string ConfigWorkingHoursOverlap = "config.working_hours_overlap";

    /// <summary>
    /// A working-hour segment is impossible: its end is not after its start, which covers both
    /// zero-length and midnight-crossing — 422.
    /// </summary>
    internal const string ConfigWorkingHoursInvalid = "config.working_hours_invalid";

    /// <summary>
    /// The requested availability window is malformed or wider than the configured maximum — 400
    /// (added in <c>availability-read</c>).
    /// </summary>
    /// <remarks>
    /// A refusal rather than a truncation: a read that quietly answers a narrower question than
    /// it was asked is worse than one that says no, because the caller cannot tell.
    /// </remarks>
    internal const string AvailabilityWindowInvalid = "availability.window_invalid";

    /// <summary>
    /// An internal time block whose end does not follow its start — 422 (added in
    /// <c>availability-read</c>).
    /// </summary>
    /// <remarks>
    /// One code for both the reversed and the zero-length case, because they are one rule and one
    /// remedy. The translated message has to read sensibly for both, which is a check the
    /// validation guide makes a human confirm.
    /// </remarks>
    internal const string BlockInvalidRange = "block.invalid_range";

    /// <summary>Unhandled error — 500. Never leaks internals.</summary>
    internal const string ServerUnexpected = "server.unexpected";
}
