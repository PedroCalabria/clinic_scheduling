## 0. Before anything else

- [x] 0.1 **Register the second redirect URI and enable the Calendar API** in the Google Cloud Console (design K14). Both are manual, both are one-time, and both fail with a specific symptom when skipped — but the second one fails only at the *first probe*, which is late enough to waste a session. Do it before writing the flow, not before validating it
- [x] 0.2 Confirm the real Google account claimed as a professional (`08-google-setup.md` step 3) still signs in, and that it is an account whose calendar access you are willing to grant and then revoke. This change's guide cannot be run against `dra.helena@clinic.local` — a non-existent domain is not claimable (`00-context.md` §9)

## 1. Configuration and the key

- [x] 1.1 Add `CalendarOptions` bound from a `Calendar` section: the encryption key (base64), the authorization/token/revocation endpoints, the calendar scope, the connect redirect URI, and the flow-state lifetime. Keep it **separate from `GoogleOptions`** — `GoogleOptions.IsConfigured` is what makes an absent Google client a supported configuration, and widening it would make the whole federated path depend on a calendar key
- [x] 1.2 Validate at startup: calendar configuration present **and** key absent or not 32 bytes → the API refuses to start, naming the missing setting (design K4). No calendar configuration at all → it starts, and only S2 reports itself unavailable. Both halves get a test; the first is the one that would otherwise be discovered by finding a plaintext token in a database
- [x] 1.3 Add the settings to `.env.example` with the command that generates a key and a sentence saying this value must survive redeploys — losing it means every professional reconnects (design K3 risk)
- [x] 1.4 Wire the same variables through `infra/docker-compose.yml`, and confirm the stack still starts with all of them empty

## 2. Sealing the credential

- [x] 2.1 Add a token protector in `Infrastructure` doing AES-256-GCM with a versioned envelope — `v1.<nonce>.<ciphertext+tag>` (design K3). One class, two operations, no interface until there is a second implementation
- [x] 2.2 Unit-test it: round-trip; two seals of the same input differ (the nonce is per-operation, not per-key); a tampered envelope fails to open rather than returning garbage; an envelope with an unknown version fails with a message that says so
- [x] 2.3 Confirm the plaintext never reaches a log: the protector takes and returns strings and logs nothing, and the call sites log the *outcome*, never the material. This is a read-the-code check, and it belongs in the task list because it is the kind of thing a later helpful log line quietly breaks

## 3. The connection, as a domain entity

- [x] 3.1 Add `CalendarConnection` to `Clinic.Domain`: professional, provider, target calendar, sealed credential (opaque string), status (`Connected` / `Revoked` / `Disconnected`), connected-at, state-observed-at. It takes the sealed value and never interprets it (design K7)
- [x] 3.2 Give it the transitions and their guards: connect, observe-revoked, disconnect, reconnect. **A connection with no credential material cannot be `Connected`** — enforce it in the type, not at the call site, because that is the invariant that makes the status trustworthy
- [x] 3.3 Implement "keep what is stored when the provider returns nothing" (design K6) *in the aggregate*: a connect that supplies no credential either keeps the held one or refuses. Two guards were the point — this is the one that survives a provider behaviour change
- [x] 3.4 Unit-test every transition including the refusals, and confirm `DomainBoundaryTests` still passes — no crypto, no HTTP, no EF anywhere near this type

## 4. Persistence

- [x] 4.1 EF configuration and one migration adding `calendar_connections`, with a **unique index on `professional_id`** (design K10). The uniqueness is the thing that makes "which connection is the real one" a question nobody ever has to answer
- [x] 4.2 Keep the sealed credential column nullable — it is genuinely absent after a withdrawal, and a sentinel value would be a second way to say "none"
- [x] 4.3 Integration-test that a second connection for the same professional is refused by the database, not only by the handler. Same reasoning as the booking exclusion constraints: the application check gives the message, the constraint makes the guarantee

## 5. The connect flow

- [x] 5.1 Add the `CalendarSync` feature slice with `GET /api/calendar/connect`, authorized `Professional`, building the authorization URL with the calendar scope, `access_type=offline`, `include_granted_scopes=true` and `prompt=consent` (design K1, K6)
- [x] 5.2 **Test the constructed URL parameter by parameter**, `include_granted_scopes=true` above all. Omitting it silently replaces the identity grant, and the damage surfaces somewhere unrelated — `StartGoogleSignIn`'s comment has been warning about this since change 2, and a comment is not a test
- [x] 5.3 Add the connect flow's own state record and its own `HttpOnly` cookie, distinct from `AuthCookies.OAuthState` (design K2). State only, **no nonce** — no ID token is validated here. Share `SafeReturnPath` as a helper rather than copying the open-redirect guard
- [x] 5.4 Add `GET /api/calendar/connect/callback`, authorized `Professional`: match and **consume** the state, exchange the code, verify the granted scope, seal, save. It establishes no session and creates no user — assert both, because the sign-in callback one folder away does exactly those things
- [x] 5.5 **Done differently, and the deviation is the point.** The second operation went into a *new* `GoogleCalendarTokens` rather than into `GoogleTokenExchange`: that class asks for an `id_token` and is forbidden from storing a refresh token, this one does the opposite, and one class serving both would branch on which flow it is in — one mistaken branch away from a sign-in exchange returning a long-lived credential. That is design K2's argument applied a layer below the callback. `GoogleTokenExchange`'s stale remark is corrected and points at the new type
- [x] 5.6 Implement the declined-scope refusal (design K5): granted scopes lacking calendar → nothing stored, no connection, no consent, `calendar.scope_declined`. Test it with a token response whose `scope` omits the calendar scope — this is the most likely real-world failure in the change and is invisible unless asserted
- [x] 5.7 Test the callback refusals: no session; a replayed callback whose state was consumed; mismatched state; absent state. Each refuses **before** any exchange is attempted

## 6. Consent

- [x] 6.1 Write the `ConsentType.CalendarSync` row in the same transaction as the connection (design K12), at the configured version. Test that a failure to save the connection leaves no consent behind
- [x] 6.2 Narrow `GrantConsent` to the data-processing consent it was written for. It parses **any** `ConsentType` under the `Patient` policy today, so a patient can grant themselves a calendar consent that means nothing — harmless while the value is unused, not harmless now. Add the test that a patient asking for `CalendarSync` there is refused
- [x] 6.3 **The task's premise was wrong, and the fix is a small addition.** There is no "existing consent read" a professional can use: consents are read through P7's profile, a patient-only surface, so a professional's calendar consent would have been recorded and unviewable — `identity-session`'s widened "visible to the user they belong to" true on paper and false in the product. The connection response now carries the consent version and the moment it was granted, because S2 is the screen that obtained it. A patient-shaped endpoint serving one field to a different role was the alternative

## 7. Status and the on-demand check

- [x] 7.1 Add `GET /api/calendar/connection` returning status, target calendar, connected-at and **state-observed-at** (design K8). No credential material in the response — assert it on the serialized body, not on the DTO's shape
- [x] 7.2 Add `POST /api/calendar/connection/check`: exchange the stored credential for an access token, read no calendar content, record the result and the moment
- [x] 7.3 Map the three outcomes exactly: valid → connected, observation moment updated; `invalid_grant` → `Revoked` + `calendar.consent_revoked`; unreachable → **state unchanged**, `calendar.sync_failed`. The third is the one worth a dedicated test — recording an outage as a revocation tells a professional to reconnect something that is fine
- [x] 7.4 Refuse a check for a professional with no connection with `calendar.not_connected`, before any provider call
- [x] 7.5 Route every calendar endpoint through the principal with **no identifier in the path** (design K11), and add the tests that say so: a front-desk user and an administrator are refused on the role; there is no request shape by which a professional can name another's connection

## 8. Withdrawal

- [x] 8.1 Add `POST /api/calendar/connection/disconnect`: revoke the consent, clear the sealed credential, mark the connection disconnected — one transaction (design K9)
- [x] 8.2 Call the provider's revocation endpoint **best-effort**, before the local commit clears the material it needs. A failure does not block the withdrawal: refusing would leave a professional connected against their stated wish
- [x] 8.3 Return the partial outcome distinctly when the provider call failed, so the screen can say the authorization may still be listed in their Google account. **Do not report an unqualified success** — that is the system telling somebody something untrue about their own data
- [x] 8.4 Test: full withdrawal; withdrawal with the provider unreachable (local state still withdrawn, outcome reported as partial); disconnecting an already-revoked connection succeeds; reconnecting afterwards records a **new** consent at the version in force and leaves the revoked one intact
- [x] 8.5 Confirm the row is not deleted (I10) while the credential **is** cleared, and record the distinction at the call site: I10 governs records, and keeping a withdrawn professional's calendar key "for history" is data minimisation failing where it is easiest to get right (design K10)

## 9. S2 — the screen

- [x] 9.1 Add `apps/staff/src/features/calendar/` and the `/calendar` route, guarded to the professional role, with a navigation entry conditioned the same way. Reception and administration get neither the entry nor the route
- [x] 9.2 Render the three states — connected, revoked, never connected — each with the actions that belong to it, and show **when the state was last observed** beside it rather than presenting it as current fact (design K8)
- [x] 9.3 Wire connect (a top-level navigation, not a fetch — it leaves for Google), check, and disconnect **behind a confirmation dialog**. Reuse the shared dialog primitive; do not hand-roll focus handling
- [x] 9.4 Translate every string in pt-BR and en, including each status, each action, the confirmation, and every refusal code this change can produce. `pnpm check:i18n` green on both the consistency and the usage scan
- [x] 9.5 Handle the never-connected state as a state rather than an error (design Open Question 4): in this change a connected calendar does nothing yet, so a screen that scolds a professional for not connecting is claiming a benefit that does not exist
- [x] 9.6 **Run as part of the guide's check 10, keyboard pass included.** `booking-surface` archived with this unrun and named it its largest gap; `booking-desk` did not repeat it, and neither does this

## 10. Codes, docs and the README

- [x] 10.1 Add `calendar.scope_declined` to `07-error-codes.md` and confirm the three reserved `calendar.*` codes are used as the catalogue describes them — `not_connected`, `consent_revoked`, `sync_failed`. Reuse `auth.google_unavailable` for a missing client rather than minting a second code for the same operator fact
- [x] 10.2 Update `08-google-setup.md`: the two manual Console steps (design K14), what a granular consent screen looks like, how to revoke access in a Google account for the guide's sake, and the two new troubleshooting rows (`redirect_uri_mismatch` on the *calendar* URI, and Calendar API not enabled)
- [x] 10.3 Corrected: `StartGoogleSignIn` (its warning about change 6 is now a test, and it says so), `GoogleTokenExchange` (its "nothing may store a refresh token" is now scoped to the sign-in path, which is still true), `08-google-setup.md`'s "needs no token-encryption key yet", `Appointment.cs`'s `externalEventId` note, and the three "Hangfire arrives in change 6" comments, which now say 6b. **One extra**, flagged by `05` §7: `Appointment.cs` claimed `booking-desk` records `Completed`/`NoShow`. It shipped without doing so; the comment now points at `visit-outcome`
- [x] 10.4 Update `README.md`: the increment-6 status cell, and the **local-run section**, because this change adds a required environment variable — one of the three parts of that file that is allowed to move (`00-context.md` §8). One to three lines, in this change's own feature commit, not a rewrite
- [x] 10.5 §3 was already split into 6a/6b by the maintainer before apply started (along with a new `visit-outcome` change owning the orphaned terminal states). §7 updated here to record 6a as done, name what it deliberately does not do, and state plainly that its validation guide has not been run

## 12. Ending an account ends its calendar grant (design K16)

> Added during apply: the maintainer answered design Open Question 2 with **yes**, which turned a
> recorded non-decision into work.

- [x] 12.1 Extract the withdrawal sequence out of the disconnect endpoint into `CalendarWithdrawal`
  in `Infrastructure`, beside `SessionStore`. Three callers now mean the same thing by "withdraw",
  and three copies is how one of them quietly stops revoking at Google — a failure invisible from
  inside this system, because everything here would read as withdrawn while the grant stayed live
- [x] 12.2 Call it from **disable** and from **deactivate**. Not conditioned on the role at the call
  site: "does this account hold a calendar grant" is one question with one place to be asked
- [x] 12.3 Keep the provider call best-effort, now for a second reason — an administrator's account
  action must not fail because Google is unreachable. Tested: the account is still disabled, the
  sessions still revoked, the local withdrawal still done
- [x] 12.4 Make `CalendarTokenProtector` resolve its key **lazily**. It is now a dependency of the
  disable path, and disabling a staff account must keep working on a deployment that never
  configured a calendar (K4) — a constructor that threw would have made turning off an account fail
  because the clinic does not use Google Calendar. Three protector tests updated to assert at the
  moment of use, plus one asserting construction succeeds without a key
- [x] 12.5 Test an account that never connected one — most accounts. This is the test that catches
  the calendar work breaking account administration for every deployment with the feature off
- [x] 12.6 **Corrected during review, and the correction is the useful part.** This task first said
  the withdrawal made a reversible action irreversible. It does not: **there is no account
  re-enable** — `User` has `Disable()` and no `Enable()`, no endpoint, no screen, while every
  catalog entity and the `Professional` record all have `Reactivate()`. `00-context.md` §5's
  "reversible off-switch" describes intent, not behaviour. The test, the spec scenario, the design
  and the endpoint comment now assert what is true — the authorization ends rather than pausing —
  and the question a future re-enable inherits is written down rather than left to be discovered
- [x] 12.7 Spec: a new `calendar-integration` requirement, and the `identity-session` staff-accounts
  requirement modified — disabling and deactivating now do one more thing, and the requirement text
  has to say so

## 13. Restoring a disabled account (pedido durante a revisão)

> Added on request rather than through a change of its own: the gap surfaced while reviewing K16,
> and it is small enough that a branch-and-propose cycle would have cost more than the work.
> **It is `identity-session`'s gap, not the calendar's** — the calendar only made it visible.

- [x] 13.1 Add `User.Enable()`. The state to return to is **derived, not remembered**: an unclaimed
  federated invitation returns to `PendingClaim` and stays claimable, anything else to `Active`.
  A previous-status column would be a second source of truth to keep correct, and restoring an
  invitation as active would produce an account that may hold a session with no identity behind it
- [x] 13.2 Clear the failed-attempt streak on restore, so a lockout does not survive it — otherwise
  the next bad password re-locks the account and the restore looks broken. The same reasoning
  `SetPassword` already applies
- [x] 13.3 Refuse a **deactivated** account: deactivation released the address, so it may already
  belong to a live account. Restoring would produce two live accounts on one address, or fail
  against the filtered unique index — a database error standing in for the rule that means it
- [x] 13.4 `POST /api/staff-accounts/{userId}/enable`, administrator-only. No session is issued and
  none is reinstated: restoring makes the account able to sign in again, nothing more
- [x] 13.5 S11 shows **Restore access** where a disabled account previously showed no action at all,
  plus the client function and pt-BR/en keys. "Restaurar acesso" against status "Desativada"; no
  collision with retiring, which is worded as releasing the address
- [x] 13.6 Discharge the stale revisit trigger in `DeactivateAsync`, which said "there is no
  un-disable" and proposed collapsing the two actions. Now false, and collapsing them would remove
  the only reversible option an administrator has
- [x] 13.7 Answer K16's inherited question with a test: **restoring an account does not restore its
  calendar authorization**. The grant was handed back and the credential destroyed, and the consent
  stays revoked — silently regaining write access to somebody's personal calendar would be the wrong
  default even if it were possible
- [x] 13.8 Spec: the `identity-session` staff-accounts requirement gains the restore, its derived
  state, the deactivated refusal, and the calendar's non-return — four scenarios
- [x] 13.9 **A pre-existing flaky test found and fixed on the way** (`booking-desk`'s
  `The_day_says_whether_the_patient_may_still_change_each_appointment`). It booked an appointment
  three hours out and then read **today's** schedule; past 21:00 clinic time those are different
  days, so it failed at 22:33 and would have failed in CI at that hour. It now reads the day the
  appointment actually falls on. Not caused by this change — surfaced by running the suite late

## 14. Uma conta desativada libera a identidade, não só o endereço

> Found by the maintainer while running this change's own validation: claiming a re-invited
> professional ended in `{"code":"server.unexpected"}` at the end of a real Google sign-in.
> **A defect in `identity-session`, not in the calendar** — 6a only got there first by being the
> change that finally exercised the path.

- [x] 14.1 Diagnose from the logs rather than the symptom: a `23505` on
  `ix_users_provider_subject`. Deactivation released the address — `ix_users_email_live` is
  filtered on `deleted_at_utc IS NULL`, so the re-invitation succeeded — while the deactivated row
  went on owning the Google subject, so the **claim** collided. The product's own documented
  recovery path (`00-context.md` §5, deactivate-and-invite-anew) was impassable for any Google
  account that had already been claimed
- [x] 14.2 **The code and the index disagreed, and the index was wrong.** `CompleteGoogleSignIn`
  resolves a subject with `DeletedAtUtc == null`, so the application already treated a deactivated
  account as no longer holding its identity; the index treated it as still holding it. Filter
  widened to `external_subject_id IS NOT NULL AND deleted_at_utc IS NULL`, which is what I10's
  "stops existing to the product" means and what the sibling email index already did
- [x] 14.3 One migration, `ReleaseIdentityOnDeactivation`. Index-only, reversible, no data touched
- [x] 14.4 The test that was missing — deactivate a **claimed** Google professional, invite the
  address anew, claim it with the same Google identity. It asserts the new account is a NEW row
  (recovery replaces rather than edits, keeping the access log honest) and that the retired row
  keeps its subject as history while no longer owning it. Nothing covered this before, which is
  why it shipped broken
- [x] 14.5 Applied to the running stack and confirmed against `pg_indexes`, so the maintainer's
  own blocked sign-in can proceed

## 11. Definition of Done

- [x] 11.1 **Measured locally: 255 → 273 domain unit, 324 → 389 integration, all green.** The domain gained the connection state machine (13); the integration tier gained the envelope (14, unit tests living in that assembly because the type is `internal` to `Api`), the startup rule (5), and the flow, probe and withdrawal (34, plus 6 for K16's withdrawal-on-disable and 5 for group 13's account restore). CI is the authority; these are the numbers the change was finished against
- [x] 11.2 Both SPA builds pass `tsc --noEmit`; `pnpm check:i18n` green; `pnpm check:readme` green
- [x] 11.3 `openspec validate calendar-connection --strict` passes
- [x] 11.4 A test reads the stored column directly and asserts **it is not the plaintext token** — the single assertion this whole change exists to make true
- [x] 11.5 A test asserts the authorization URL carries `include_granted_scopes=true`
- [x] 11.6 CI still needs no Google credentials: the token exchange goes through the substituted handler seam from change 2, while the envelope, the state check, the scope verification and the domain state machine all run for real (design K14)
- [x] 11.7 **Run 2026-08-25/26 against a local stack, in both locales, with a real Google account, including the revoke-in-Google-and-come-back check. All thirteen checks pass.** Four defects were found getting there, three of them in the configuration and documentation this guide prescribes and the fourth a shipped `identity-session` defect (group 14). Outcome recorded, including three things explicitly not examined
- [x] 11.8 **All four answered.** 1 and 2 were the maintainer's, and both came back during apply: **1 (key rotation) — accepted**, no runbook, revisit trigger armed at ~10 connected professionals or a compliance requirement; **2 (disabling a user) — yes**, which became design K16 and task group 12 rather than staying a note. Previously recorded: Written up in `design.md`. **3** — the connection is keyed on the `Professional` row and an unconfigured professional is refused exactly as S3 refuses them, following the precedent rather than inventing a second behaviour for the same state; tested. **4**, provisionally — "never connected" is presented as a state with no warning styling, because in 6a a connected calendar does nothing yet; guide check 11 asks a human to confirm that reads as honest.
- [ ] 11.9 Change archived into the living spec, creating the `calendar-integration` capability and folding the one modified `identity-session` requirement. State the requirement counts before and after, and confirm the MODIFIED operation found its target header
