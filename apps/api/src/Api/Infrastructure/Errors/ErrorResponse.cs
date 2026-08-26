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
    /// <remarks>
    /// <b>The booking gate, from <c>booking-core</c> onward.</b> A patient must hold an active
    /// <c>DataProcessing</c> consent at the configured current version to book. Change 2 grants
    /// it at just-in-time provisioning and P7 lets a patient revoke it, so until change 5
    /// revocation was possible with nothing checking it — this is the code that closes that
    /// loop. Also returned when revoking a consent that is not in force.
    /// </remarks>
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

    /// <summary>
    /// The professional already holds an appointment over the requested time — 409, invariant
    /// I4 (added in <c>booking-core</c>).
    /// </summary>
    /// <remarks>
    /// Reported both when the pre-commit check sees the collision and when the exclusion
    /// constraint rejects a racing insert. Deliberately the same code for both: the caller sees
    /// the same failure and has the same remedy — pick another time — and one code per
    /// user-meaningful failure is the catalogue's own rule.
    /// </remarks>
    internal const string BookingSlotTaken = "booking.slot_taken";

    /// <summary>
    /// The professional has an internal block over the requested time — 409, the booking
    /// direction of invariant I7 (added in <c>booking-core</c>).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="BookingSlotTaken"/>, and the distinction is what the patient
    /// would do next: there somebody was faster and another time will do, here the professional
    /// declared themselves unavailable and the read that offered the slot is stale for a wholly
    /// different reason. Telling this patient "someone just booked it" would send them looking
    /// for a race that did not happen.
    /// <para>
    /// It is also the mirror of <see cref="BookingBlockOverlapsAppointment"/>, which named the
    /// other direction from the start. Having one direction named and the other overloaded
    /// would have been the asymmetry.
    /// </para>
    /// </remarks>
    internal const string BookingSlotBlocked = "booking.slot_blocked";

    /// <summary>
    /// The patient already holds an appointment over the requested time — 409, invariant I6
    /// (added in <c>booking-core</c>).
    /// </summary>
    /// <remarks>
    /// The catalogue had a code for the professional's collision and one for the room's, and
    /// none for the patient's, so the third exclusion constraint had no way to answer.
    /// Overloading <see cref="BookingSlotTaken"/> would tell a patient that somebody else took a
    /// slot they are themselves standing in.
    /// <para>
    /// It also makes a double-submitted confirmation self-defending: the second request overlaps
    /// the appointment the first one created, so it is refused here rather than by an
    /// idempotency mechanism this system does not have.
    /// </para>
    /// </remarks>
    internal const string BookingPatientBusy = "booking.patient_busy";

    /// <summary>The requested start lies outside the professional's candidate hours — 422.</summary>
    internal const string BookingOutsideWorkingHours = "booking.outside_working_hours";

    /// <summary>The requested start is sooner than the configured minimum lead time — 422 (I8).</summary>
    internal const string BookingLeadTimeViolation = "booking.lead_time_violation";

    /// <summary>The requested start is beyond the configured scheduling horizon — 422 (I8).</summary>
    internal const string BookingHorizonExceeded = "booking.horizon_exceeded";

    /// <summary>
    /// The professional holds no active duration for the requested appointment type — 422 (I2).
    /// </summary>
    /// <remarks>
    /// The duration is the qualification gate 3b built: it may only exist for a type whose
    /// specialty the professional holds, so its absence is exactly "not qualified for this kind
    /// of visit". Reachable in practice when a qualification is cleared between a search and a
    /// confirmation.
    /// </remarks>
    internal const string BookingSpecialtyMismatch = "booking.specialty_mismatch";

    /// <summary>
    /// Every active resource of the required type is occupied for the requested time — 409 (I5).
    /// </summary>
    internal const string BookingResourceUnavailable = "booking.resource_unavailable";

    /// <summary>
    /// An internal block would overlap one of that professional's live appointments — 409, the
    /// block direction of invariant I7 (added in <c>booking-core</c>).
    /// </summary>
    /// <remarks>
    /// Catalogued from the seed and unreachable until now: <c>availability-read</c> shipped block
    /// creation with no appointment check because there was nothing to race. This change created
    /// the racer, so the refusal became reachable as planned rather than as a repair.
    /// </remarks>
    internal const string BookingBlockOverlapsAppointment = "booking.block_overlaps_appointment";

    /// <summary>
    /// The appointment starts sooner than the cancellation cutoff, and the cutoff applies to this
    /// caller — 422 (domain-model F3, added in <c>booking-lifecycle</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is stated in terms of an <em>authority</em> rather than a role: the domain is told
    /// whether the cutoff applies and never asks who is calling. So this code names a refusal that
    /// a caller with the authority to act inside the cutoff simply does not receive, rather than a
    /// refusal that some later change has to special-case around.
    /// </para>
    /// <para>
    /// Only the patient path passes "the cutoff applies" today, because it is the only path there
    /// is. The front desk acting inside the cutoff arrived in <c>booking-desk</c>, and it did so by
    /// passing the other value — not by relaxing this rule. <c>BookingActor.CutoffApplies</c> is
    /// where the fact is established, and it is the only place in the system that produces
    /// <c>false</c>.
    /// </para>
    /// <para>
    /// <b>This code is about changing an appointment and never about creating one.</b> A booking
    /// too close to now is <see cref="BookingLeadTimeViolation"/>, which no role overrides — the
    /// lead time is the number the read and the write share so that availability cannot offer what
    /// booking refuses (design N1).
    /// </para>
    /// </remarks>
    internal const string BookingCutoffPassed = "booking.cutoff_passed";

    /// <summary>
    /// An appointment that does not exist — 404, on a path whose caller is entitled to know that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Still unreachable by a patient, and that half has not changed.</b> On a patient path, an
    /// appointment belonging to somebody else and an id that was never real both answer
    /// <see cref="OwnershipDenied"/>, so the endpoint cannot be used to enumerate appointment ids.
    /// The catalogue already settled that shape for <see cref="PatientNotFound"/> and the reasoning
    /// is the same one, not a new one.
    /// </para>
    /// <para>
    /// <b>Its caller arrived in <c>booking-desk</c>.</b> The cancel and reschedule routes admit
    /// staff as well as patients, and the branch between the two answers is
    /// <c>BookingActor.CannotReach</c> — one place, shared by both routes. A receptionist who
    /// mistypes an id is entitled to know they mistyped it; there is no appointment they are not
    /// entitled to reach, so absence carries no information they could misuse.
    /// </para>
    /// </remarks>
    internal const string BookingAppointmentNotFound = "booking.appointment_not_found";

    /// <summary>
    /// The appointment is already in a terminal state — 409 (added in
    /// <c>booking-lifecycle</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Beyond this change's stated brief, and flagged rather than absorbed</b> — the same
    /// treatment <see cref="BookingPatientBusy"/> got in 5a, and for the same reason: the
    /// catalogue had nothing that answers this honestly.
    /// <see cref="BookingAppointmentNotFound"/> would deny the existence of a row the patient is
    /// looking at on P5; <see cref="OwnershipDenied"/> is about who is asking rather than about
    /// what state the thing is in, and the patient does own it; and
    /// <see cref="BookingCutoffPassed"/> would give a time-based reason for a state-based refusal,
    /// which is precisely the confusion <see cref="BookingSlotBlocked"/> was split away from
    /// <see cref="BookingSlotTaken"/> to prevent.
    /// </para>
    /// <para>
    /// Ordinary rather than exotic: P5 open in two tabs, or a cancel followed by the back button.
    /// If a reviewer prefers to overload an existing code, the change is one mapping line and one
    /// i18n pair.
    /// </para>
    /// </remarks>
    internal const string BookingAppointmentNotChangeable = "booking.appointment_not_changeable";

    /// <summary>
    /// The professional has no calendar connection to act on — 422 (change 6a).
    /// </summary>
    /// <remarks>
    /// Reserved during planning in <c>07-error-codes.md</c> and first used here. It answers
    /// "check my connection" and "disconnect me" when there is nothing to check or disconnect;
    /// it is not what a professional who has simply never connected sees on S2, because reading
    /// a state of "not connected" is a successful read of a real state, not a refusal.
    /// </remarks>
    internal const string CalendarNotConnected = "calendar.not_connected";

    /// <summary>
    /// The provider no longer honours this authorization — 422 (change 6a).
    /// </summary>
    /// <remarks>
    /// The professional (or Google) withdrew the grant on Google's side. The remedy is to
    /// reconnect, and the distinction from <see cref="CalendarScopeDeclined"/> is exactly that
    /// remedy: here the permission existed and lapsed; there it was never given.
    /// </remarks>
    internal const string CalendarConsentRevoked = "calendar.consent_revoked";

    /// <summary>
    /// The provider could not be reached — 503 (change 6a).
    /// </summary>
    /// <remarks>
    /// An operator/network fact, not a caller mistake, and deliberately <b>not</b> recorded as a
    /// revocation (design K8): a Google outage that flipped a connection to revoked would tell a
    /// professional to reconnect something that is working. 6b reuses this code for a failed
    /// dispatch.
    /// </remarks>
    internal const string CalendarSyncFailed = "calendar.sync_failed";

    /// <summary>
    /// The authorization completed without granting calendar access — 422 (change 6a, design K5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// New in this change, because none of the reserved codes says this. Google's consent screen
    /// is granular: a professional can approve the request and untick calendar access, and the
    /// flow still returns a valid token response. Nothing about the redirect says the ask was
    /// refused.
    /// </para>
    /// <para>
    /// Distinct from <see cref="CalendarConsentRevoked"/> because the two need different
    /// sentences on the same screen. "You declined" invites granting permission; "it was
    /// revoked" invites reconnecting. Reporting one as the other sends the professional to the
    /// wrong action.
    /// </para>
    /// </remarks>
    internal const string CalendarScopeDeclined = "calendar.scope_declined";

    /// <summary>
    /// The connection could not be completed because no credential was obtained — 422 (design K6).
    /// </summary>
    /// <remarks>
    /// Google issues a refresh token only on the first grant for a client/user pair, so an
    /// authorization can succeed and carry nothing. When nothing is held either, there is no
    /// connection to record — and recording one anyway would mean a status of "connected" that
    /// 6b could never dispatch against.
    /// </remarks>
    internal const string CalendarConnectFailed = "calendar.connect_failed";

    /// <summary>Unhandled error — 500. Never leaks internals.</summary>
    internal const string ServerUnexpected = "server.unexpected";
}
