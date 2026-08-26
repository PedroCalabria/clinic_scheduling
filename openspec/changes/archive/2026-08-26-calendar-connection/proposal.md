## Why

Every increment so far has been a closed system: the clinic's own database answering questions
about the clinic's own data. Change 6 is where that stops — an appointment has to reach the
professional's Google Calendar, because a doctor who cannot see the booking in the calendar they
actually look at will book something over it. That is the failure the product exists to prevent,
and it is the one failure five increments of correct scheduling cannot touch.

Reaching Google needs two things that do not exist yet: **permission** to write to a
professional's calendar, and somewhere safe to keep the long-lived credential that permission
produces. Neither is a scheduling concern, both are load-bearing, and putting them in the same
change as the outbox would make one review cover a new secret, a new OAuth flow, a new screen, a
new table, a scheduler, a dispatcher and an idempotency contract. So change 6 splits at its natural
seam, exactly as change 5 did: **6a establishes the connection, 6b rides on it.** 6a is
demonstrable on its own — a professional connects their calendar and the screen says so — which is
the test `05-openspec-workflow.md` §W actually applies.

This is also the repo's first **secret encrypted at rest**. `03-nfr.md` §2 has said "OAuth calendar
tokens encrypted at rest" since planning; nothing has needed it until now, and
`GoogleTokenExchange` says so in a comment that names this change. The machinery lands here, with
one credential to protect, rather than arriving alongside a dispatcher.

## What Changes

**A professional authorizes calendar access, separately from signing in.** Change 2 deliberately
asked Google for `openid email profile` and nothing else, with no `access_type=offline`, so no
refresh token could arrive even by accident. This change adds a **second, distinct** Google flow —
started by a click on S2, never by signing in — that requests the calendar scope with offline
access, over the identity grant change 2 already holds (`include_granted_scopes=true`). Login stays
login; the two-moment consent `01-requirements.md` chose is now real rather than planned.

**The refresh token is stored encrypted, under a key that is not in the repository.** A new
required secret, a new `.env.example` entry, new Compose wiring, and a test that reads the stored
column and asserts it is not the plaintext token. The token is never logged, never returned to any
client, and never leaves the API.

**`CalendarConnection` becomes a real entity** — one per professional: provider, the encrypted
refresh token, granted scope, status (connected / revoked / disconnected), when it was connected,
and when its state was last observed. `02-domain-model.md` §4 has described it since planning.

**S2 — `/staff/calendar` — is built.** A professional's own screen: connect, current status,
reconnect after a revocation, **check connection** (an on-demand probe against Google), and
**disconnect** (full withdrawal — see below). Professional role only, own connection only;
reception and administrators have no S2 at all.

**Calendar consent is captured, versioned, and withdrawable.** `ConsentType.CalendarSync` has been
a shipped, unused enum value since change 2. A consent row is written when the connection is
established, at the configured version, and revoked when the professional disconnects — the same
record-don't-erase treatment `identity-session` gave data-processing consent.

**Disconnect is a real withdrawal, not a local forget.** It revokes the consent row, deletes the
stored refresh token, marks the connection disconnected, **and calls Google's revocation
endpoint**. A consent that cannot be withdrawn on the screen that granted it is a consent in name
only; withdrawing it locally while the grant lives on in the professional's Google account is worse
— it tells them something untrue.

**Ending a professional's access ends the authorization it came with.** Disabling or deactivating
an account withdraws that professional's calendar grant, the same way they could withdraw it
themselves. Revoking sessions ends access to *this* system; it says nothing about the standing
permission the clinic holds to write to somebody's personal calendar, and leaving that alive means
a switched-off account still carrying live write access to a private diary.

**Revocation is detected by an explicit probe, and the screen says when it last looked.** Nothing
calls Google on a schedule in this change, because there is no scheduler and 6b is where one is
justified. So S2 shows the last **observed** status together with the moment it was observed, and a
control that probes on demand (a refresh-token exchange — no calendar data is read). An
`invalid_grant` from Google flips the status to revoked and S2 offers reconnect
(`calendar.consent_revoked`). Honest about what a screen can know without a job behind it; 6b's
dispatcher becomes the continuous detector.

### What this change does NOT touch

- **No scheduler.** No Hangfire, no background job, no recurring work. Every path here is
  request/response. The two documented Hangfire revisit triggers (the session-expiry sweep in
  `SessionStore`, and its twin in `Session`) stay open and are 6b's to collect.
- **No outbox, no `external_event_id`, no propagation of any kind.** Not one calendar event is
  created, updated or deleted by this change. Booking, cancelling and rescheduling are byte-for-byte
  unchanged; the seam `booking-lifecycle` C9 and `booking-desk` both declared stays exactly where
  they left it.
- **Nothing inbound.** No webhook, no `syncToken`, no watch channel, no external `TimeBlock`, no
  `ReconciliationConflict`, no S6. Change 7.
- **Availability is unchanged.** A connected calendar contributes nothing to the tri-constraint
  solver until external blocks arrive in change 7. Connecting changes no slot.
- **The sign-in flow is unchanged.** `StartGoogleSignIn` keeps its identity-only scope list and its
  absent `access_type`. This change adds a flow beside it, not a widening of it.

## Capabilities

### New Capabilities

- `calendar-integration`: a professional's authorization to a external calendar provider — how it
  is obtained (incremental authorization, offline access), how the long-lived credential is
  protected at rest, what states the connection has and how each is observed, and how the
  professional withdraws it. Outbound propagation (6b) and inbound sync (change 7) add to this
  capability later; this change establishes only the connection it will ride on.

### Modified Capabilities

- `identity-session`: two requirements. **"Administrators manage staff accounts and professional
  invitations"** gains the calendar withdrawal — disabling and deactivating now do one more thing,
  and a reader of that requirement would otherwise have an incomplete picture of what those actions
  mean. And the **"Consent is captured and versioned"** requirement is worded around a
  *patient's* data-processing consent — "record a patient's consent", "visible to the patient they
  belong to". A professional's calendar consent is the second kind, and the first held by somebody
  who is not a patient, so the requirement widens from patient to user. The grant and revoke
  mechanics are unchanged; what changes is who a consent can belong to.

Deliberately **not** modified: the **"Google sign-in resolves to the same app-owned session"**
requirement, whose scenario *"Only login scope is requested"* remains true word for word — the
sign-in flow still asks for identity scopes only and still stores no refresh token. Its sentence
"no Google token SHALL be … persisted by this capability" was written with the qualifier that keeps
it true today. The new flow is a different flow in a different capability, and the design says why
they must not share a callback.

## Impact

**New — backend.** A `CalendarSync` feature slice in `apps/api/src/Api/Features/` (start
authorization, complete authorization, read status, probe, disconnect); `CalendarConnection` in
`Clinic.Domain` (the state machine for a connection is a domain rule, the encryption is not); a
token-protection adapter and its key option under `Infrastructure`; one EF migration.

**Modified — backend.** The staff-account disable and deactivate paths withdraw the calendar
authorization through one shared primitive (`CalendarWithdrawal`), which the S2 disconnect endpoint
also uses — three callers, one meaning. `AuthOptions`/`GoogleOptions` gain the calendar-flow settings (a second
redirect URI, the calendar scope, the revoke endpoint) — additive, and the existing
`IsConfigured` contract for the sign-in path is untouched, so a deployment with no Google client
still starts and still degrades exactly one login path. `GoogleTokenExchange` gains a second
operation (it currently models `id_token` alone, on purpose); the comment saying nothing may store
a refresh token is now false and is corrected rather than left standing.

**New — frontend.** `apps/staff/src/features/calendar/` (S2), one route, one navigation entry
conditioned on the professional role, and pt-BR + en keys for the screen and every status.

**Modified — docs.** `07-error-codes.md` gains the codes this change actually returns (three of the
`calendar.*` codes were reserved during planning; at least the declined-scope case has no code
yet). `08-google-setup.md` gains the **manual Google Console step** this change introduces — a
second registered redirect URI and the Calendar API enabled on the project — plus what to expect on
a granular consent screen. `.env.example` gains the token-encryption key and the calendar redirect
URI. `README.md`'s status cell for increment 6 moves to *partly running*, in this change's own
feature commit (`00-context.md` §8).

**Operational — a new required secret.** The encryption key is required whenever the calendar
feature is enabled: absent, the feature refuses to start rather than falling back to storing a
token in clear. Existing deployments need one new value; `.env.example` documents how to generate
it.

**Human validation is gated on a real Google account.** This change's guide cannot be run against
seed data — `dra.helena@clinic.local` is a non-existent domain and is not claimable. It needs a
real Google account claimed as a professional (`00-context.md` §9), and it is the first check in
this project that requires *revoking* access in a real Google account and coming back. That is the
same wall `staff-google-guard` 9.6 is still standing at; this change plans the account in rather
than discovering it at validation time.
