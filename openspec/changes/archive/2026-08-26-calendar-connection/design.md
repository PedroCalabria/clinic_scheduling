## Context

Five increments built a closed system. This one opens it, and the opening is narrow on purpose:
**a professional grants us permission to write to their Google Calendar, and we keep the credential
that permission produces.** Nothing is written to a calendar in this change.

Three things are already true, and this change wires rather than invents them:

- **`ConsentType.CalendarSync = 2`** has been a shipped, unused enum value since change 2.
- **The sign-in flow is deliberately narrow.** `StartGoogleSignIn` asks for `openid email profile`
  and sends no `access_type=offline`; `GoogleTokenExchange` models only `id_token` and says in a
  comment that nothing in that change may store a refresh token. Both carry a note naming *this*
  change as the one that widens the picture — and the note in `StartGoogleSignIn` contains the
  specific warning that made incremental authorization a decision rather than a detail.
- **`02-domain-model.md` §4 already describes `CalendarConnection`**, and `03-nfr.md` §2 has said
  "OAuth calendar tokens encrypted at rest" since planning. Nothing has needed a key until now.

The constraint that shapes almost everything below: **there is no scheduler.** Hangfire is change
6b's, and adding it here to power one status probe would be exactly the "what problem does it solve
*here*?" failure `00-context.md` §5 warns against. Every path in this change is request/response,
and the design has to be honest about what that costs.

## Goals / Non-Goals

**Goals:**

- A professional connects their Google Calendar from S2 and the screen says so — demonstrable
  without any part of 6b existing.
- The refresh token is protected at rest under a key that is not in the repository, and a test
  proves the stored bytes are not the token.
- The calendar grant is a **separate moment** from signing in, and the two flows cannot be confused
  for one another in either direction.
- Calendar consent is recorded, versioned, and withdrawable on the same screen that granted it.
- A revoked grant is visible on S2, with a plain statement of *when* that was last checked.
- CI still needs no Google credentials.

**Non-Goals:**

- Any scheduler, job, or recurring work. The two documented Hangfire revisit triggers
  (`SessionStore`, `Session`) stay open; 6b collects them.
- Any propagation: no event created, updated or deleted; no outbox; no `external_event_id`. Booking,
  cancel and reschedule are untouched.
- Anything inbound: no webhook, no `syncToken`, no watch channel, no external `TimeBlock`, no
  `ReconciliationConflict`, no S6.
- Any effect on availability. Connecting a calendar changes no slot until change 7.
- Access-token *storage*. The short-lived access token is used within the request that obtained it
  and never persisted (K3).
- Key rotation procedure. The envelope is versioned so rotation is addable; the runbook is
  deliberately not written (Open Question 1, decided).

## Decisions

### K1 — Incremental authorization from S2, not a widened sign-in and not a second login

The calendar scope is requested by a deliberate click on S2, over the identity grant change 2
already holds, sending `include_granted_scopes=true`.

*Alternatives rejected:*

- **Widen the sign-in scope list.** One flow, no second screen — and every patient signing in would
  be asked for calendar permission they will never use, on the flagship consent screen, at the
  moment they are least willing to think about it. `01-requirements.md`'s two-moment consent exists
  precisely to avoid this, and `08-google-setup.md` promises it to whoever reads the setup doc.
- **Ask for the calendar scope during a professional's sign-in only.** Discriminating by role at the
  authorization request means knowing the role before authenticating — the thing
  `00-context.md` §5 says is never inferred from the identity provider. It also makes "I'll do it
  later" impossible.

**`include_granted_scopes=true` is load-bearing, not decoration.** Without it Google issues a grant
covering the calendar scope *alone*, silently replacing the identity grant obtained at sign-in.
Nothing breaks visibly at first — the session is ours, not Google's — so the damage surfaces later,
somewhere unrelated. `StartGoogleSignIn`'s comment names this exact trap; this decision is that
comment being honoured.

### K2 — A separate endpoint, a separate callback, a separate state cookie

The connect flow is `GET /api/calendar/connect` → Google → `GET /api/calendar/connect/callback`.
It does not reuse `/api/auth/google/*`, does not reuse `AuthCookies.OAuthState`, and does not reuse
`GoogleOAuthState`.

Reusing them is tempting — same provider, same state/nonce shape, same open-redirect guard — and it
is exactly the wrong economy. The two flows have **opposite obligations**:

|  | sign-in callback | connect callback |
|---|---|---|
| Establishes a session | **yes** — that is its whole job | **never** — the caller is already authenticated |
| May create a `User` | yes (JIT patient on P1) | **never** |
| Requires an existing session | no — the caller is anonymous | **yes** — professional, `[Authorize]` |
| Wants a refresh token | **never** | yes |
| Anonymous access | `.AllowAnonymous()` | refused |

Sharing a callback would mean one handler branching on which cookie it found, with a session-minting
path one mistaken branch away from a code that arrived through the calendar flow. Separate routes
make that structurally impossible rather than carefully avoided — the same reasoning
`staff-google-guard` applied to the two sign-in surfaces, where the flow's own `HttpOnly` state
cookie decides what the flow is allowed to do.

The connect flow's state record carries `state` only. There is **no nonce**, because no ID token is
validated here — the nonce exists to bind an ID token to a request, and this flow wants a refresh
token. `GoogleOAuthState.SafeReturnPath`'s open-redirect guard is reused as a shared helper; the
guard is a genuinely general rule, and duplicating it is how one copy quietly loses a case.

### K3 — Encrypt with AES-256-GCM under a key from configuration; store a versioned envelope

The refresh token is sealed with AES-256-GCM using a 32-byte key read from configuration
(`Calendar__TokenEncryptionKey`, base64). What lands in the column is a versioned envelope —
`v1.<nonce>.<ciphertext+tag>` — not a bare ciphertext.

*Alternatives rejected:*

- **ASP.NET Core Data Protection.** The obvious framework answer, and it fails on its key ring's
  lifecycle. By default the ring is written to the container filesystem, which Compose recreates —
  so every `down`/`up` produces a stored token nothing can decrypt, and the professional silently
  needs to reconnect. Persisting the ring properly means another package, another table, and a
  second key-management story to reason about. For **one secret with one lifetime**, an explicit key
  in the environment is less machinery and far more legible: you can point at the value that
  protects the token.
- **`pgcrypto` — encrypt in the database.** The key travels in the SQL statement, so it lands in
  query logs and in `pg_stat_statements`. It also moves a security-critical operation to the one
  place the domain is not allowed to reach.
- **A column marked sensitive, protected by disc encryption alone.** That is not encryption at rest
  in any sense that survives a database dump, and `03-nfr.md` §2 asks for the property, not the
  gesture.

**Why an envelope with a version prefix.** Rotation is not in scope, but a stored blob with no room
to say how it was made can only be rotated by a migration that decrypts everything with the one key
it can no longer assume is right. One prefix byte costs nothing today and is the difference between
rotation being additive and rotation being an incident.

**AES-GCM rather than AES-CBC + HMAC** because it is authenticated in one primitive, and the failure
mode of forgetting the authentication half is silent.

**The access token is not stored at all.** It expires in an hour and 6b can mint one from the
refresh token whenever it needs one; persisting it would be a second secret to protect for no gain.

### K4 — Missing key means the feature is off, never a token in clear

If the calendar feature is otherwise configured and the encryption key is absent or malformed, the
API **fails to start**. It does not start with the feature silently disabled, and it certainly does
not store the token unencrypted.

This is the same shape as `Clinic__Timezone` in change 3b: a value whose absence would make the
system quietly wrong is a startup failure, not a default. The distinction from `GoogleOptions`
matters and is deliberate — **an absent Google client is a supported configuration** (the whole
federated path degrades, by design, and CI depends on it), while **a present calendar feature with
no key is a misconfiguration**. So the rule is conditional: no calendar configuration at all → S2
reports `auth.google_unavailable` and everything else runs; calendar configuration present but
keyless → refuse to start.

### K5 — Verify the scopes Google actually granted; never assume the ask was honoured

Google's granular consent screen lets a professional approve the sign-in and **untick calendar
access**, and the flow still returns a perfectly valid token response. Nothing about the redirect
says the ask was refused.

So the callback reads the `scope` field of the token response and requires the calendar scope to be
present. If it is not, nothing is stored, no consent row is written, no connection is created, and
S2 reports `calendar.scope_declined` — a new code, because "you declined" and "you revoked" are
different facts about different moments, and a screen that conflates them tells the professional to
reconnect when what they need to do is tick the box.

This is the most likely real-world failure of the whole change, and it is invisible unless
explicitly checked.

### K6 — Send `prompt=consent`, and never overwrite a good token with nothing

Google returns a refresh token **only on the first grant** for a client/user pair. A professional
who connects, disconnects locally, and reconnects gets a token response with no `refresh_token` at
all — and a naive handler writes null over the credential it still had.

Two guards: the connect flow sends `prompt=consent` (which makes Google reissue one), **and** the
handler treats an absent refresh token as "keep what is stored". If there is nothing stored either,
that is a failure with its own message, not a connection recorded as healthy with no credential
behind it. The second guard is the one that matters — the first is a request parameter and can be
lost to a provider behaviour change; the second is ours.

### K7 — `CalendarConnection` is a domain entity; the encryption is not

The connection's **states and transitions** are a domain rule — connected, revoked, disconnected,
what each permits, and the fact that a connection without credential material cannot be connected.
That lives in `Clinic.Domain`, tested by the unit tier, and takes the sealed value as an opaque
string it never interprets.

The **sealing** is infrastructure and lives in `Api`. `Domain` referencing a crypto adapter would be
the boundary erosion `DomainBoundaryTests` exists to catch; the domain's rule is "there is
credential material", not "it is AES-GCM".

```
  Domain                             Api / Infrastructure
 ┌────────────────────────┐         ┌───────────────────────────┐
 │ CalendarConnection     │         │ TokenProtector            │
 │  Connect(sealedToken)  │◀────────│  Seal(plaintext) → v1.…   │
 │  MarkRevoked(observed) │  opaque │  Open(v1.…)   → plaintext │
 │  Disconnect(at)        │  string │  key ← Calendar__…Key     │
 │  status · observedAt   │         └───────────────────────────┘
 └────────────────────────┘
```

### K8 — Status is an observation with a timestamp, and the probe is explicit

S2 shows the last **observed** status and when it was observed. A "check connection" control probes
on demand: exchange the refresh token for an access token, touch no calendar data, record the
result and the moment. `invalid_grant` → revoked. A transport failure → the status is unchanged and
the screen says the check could not be completed (`calendar.sync_failed`) — an unreachable Google is
not evidence of a revoked grant, and recording it as one would tell a professional to reconnect a
connection that is fine.

*Alternatives rejected:*

- **Probe on every status read.** Ties a screen load to Google's availability and to Google's rate
  limits; an outage makes S2 broken rather than merely stale.
- **Auto-probe, throttled by a freshness window.** Looks self-updating and mostly is — but it hides
  a network call behind a page load, and it makes the validation guide wait out a timer to see the
  flip it is there to observe. A screen that says "checked 4 minutes ago" beside a button is more
  honest than one that silently decides how stale is acceptable.

**What this costs, stated plainly:** between two probes, S2 can show "connected" for a grant that
was revoked minutes ago. That is tolerable *only because nothing depends on it yet* — no event is
being written, no availability is being computed. **The revisit trigger is 6b**, where the
dispatcher calls Google continuously and becomes the real detector: a dispatch failing with
`invalid_grant` flips the status as a side effect of work that was happening anyway. This decision
is scoped to the window in which a connection has no consumer.

### K9 — Disconnect withdraws at Google too, and never leaves the professional stuck

Disconnect: read the sealed token, call Google's revocation endpoint (best effort), then commit the
local withdrawal — consent row revoked, credential material cleared, status `Disconnected`,
recorded in one transaction.

The ordering follows from a question with only one defensible answer: **if Google's revoke call
fails, do we still withdraw locally?** Yes. The professional asked to withdraw; refusing leaves them
connected against their stated wish, and a retry button that keeps failing while Google is down is a
consent they cannot escape. So the local withdrawal is unconditional and the Google call is
best-effort — but the screen **says which happened**: on failure it tells them the grant may still
be listed in their Google account and where to remove it. Reporting plain success there would be the
system telling the professional something untrue about their own data.

Google's revoke endpoint is idempotent for an already-revoked token, so the common "they revoked it
in Google first, then pressed disconnect here" path needs no special case.

### K10 — One connection row per professional; reconnect reuses it; withdrawal clears the secret

A unique index on `professional_id`. Reconnecting after a revocation updates the existing row rather
than inserting a second, so "which connection is the real one" is never a question anyone has to
answer.

**On `I10` (soft-delete only).** Disconnecting does not delete the row — status carries the fact,
and the history of having been connected is worth keeping, exactly as `booking-core` argued for the
appointment. But the **credential material is cleared**, and that is not a violation of I10: I10
governs records, and a withdrawn consent whose secret we kept "for history" would be data
minimisation failing at the one point it is easiest to get right. The row remembers *that* there was
a connection; it does not keep the key to the professional's calendar after being asked to give it
up.

### K11 — Ownership by unreachability: the route carries no identifier

Every calendar endpoint is `/api/calendar/connection…` with **no id in the path**. The professional
is resolved from the principal.

This is stronger than checking ownership, because there is no parameter through which to name
someone else's connection. `PatientDataGuard` exists for the case where a resource *must* be
addressable by id and the check is therefore unavoidable; here it is avoidable, so it is avoided —
and **no `AccessLog` row is written**, because a calendar connection is the professional's own
record and contains no patient personal data. That trail is for staff reading *other people's*
data (`booking-desk` N-something is where it became load-bearing); writing rows for a professional
reading their own connection would dilute a log whose value is that every row means something.

Reception and administrators get no S2 route and no navigation entry: the endpoints are
`AuthorizationPolicies.Professional`. An administrator cannot connect a calendar on a professional's
behalf, and that is correct — the grant is the professional's to give.

### K12 — Consent in the same transaction, and close the patient endpoint's back door

The `ConsentType.CalendarSync` row is written in the same transaction as the connection: a
connection without its consent record, or a consent for a connection that failed to save, are both
states nobody should have to reason about.

While here, one shipped hole gets closed. `GrantConsent` (`POST /api/patients/me/consents/{type}/grant`)
parses **any** `ConsentType` under the `Patient` policy, so a patient can today grant themselves a
`CalendarSync` consent that means nothing. Harmless while the value is unused; not harmless once it
gates a real authorization. That endpoint is narrowed to the data-processing consent it was written
for. A professional's calendar consent has exactly one producer: completing the connect flow.

### K13 — The target calendar is recorded explicitly as `primary`, not assumed by 6b

The connection stores the target calendar id, set to `primary`. No chooser is built.

Recording it costs one column now and saves 6b from hard-coding `"primary"` at the call site, where
it would become a string in a dispatcher that nobody can later change without a migration. If a
professional ever needs to target a secondary calendar, that is a screen and a column update rather
than a change to how events are addressed.

### K14 — Google Console prerequisites are manual, documented, and not required to develop

Two manual steps land in `08-google-setup.md`: **register the second redirect URI**
(`http://localhost:8080/api/calendar/connect/callback`) and **enable the Google Calendar API** on
the project. Neither can be automated, both fail loudly and specifically when skipped
(`redirect_uri_mismatch`, and an API-not-enabled error on the first probe), and both are listed with
the symptom they produce so the troubleshooting table earns its keep.

Development and CI need neither: the token exchange goes through a typed `HttpClient` whose handler
tests replace — the change-2 seam (`00-context.md` §6), reused rather than reinvented. What tests
substitute is Google's *transport*; the envelope, the state check, the scope verification and the
whole domain state machine run for real.

### K16 — Ending a professional's access ends the authorization it came with

**Decided by the maintainer during apply** (Open Question 2, answered *yes*). Disabling or
deactivating an account withdraws that professional's calendar authorization: consent revoked,
credential cleared, connection disconnected, grant handed back to Google.

The argument that settled it: revoking sessions ends access to *this* system and says nothing
about the standing permission the clinic holds to write to somebody's personal calendar. Leaving
that alive means an account the clinic has deliberately switched off still carrying live write
access to a private diary — the authorization outliving the role it was granted alongside, which
is backwards.

**One implementation, three callers.** The sequence moved out of the disconnect endpoint into
`CalendarWithdrawal`, beside `SessionStore` in `Infrastructure`. Three copies is how one of them
quietly stops revoking at Google, and that failure would be invisible from inside this system:
everything here would read as withdrawn while the grant stayed live.

**The provider call stays best-effort, and now for a second reason.** An administrator's account
action must not fail because Google is unreachable. The local withdrawal is unconditional; only
the confirmation is lost.

**The consequence, corrected during review.** This design first claimed the withdrawal made a
reversible action irreversible — "a re-enabled professional must reconnect". **That was wrong, and
it was wrong about the product rather than about the calendar: there is no re-enable.** `User` has
`Disable()` and no `Enable()`; no endpoint and no screen offers one, while every catalog entity and
the `Professional` record all have `Reactivate()`. `StaffAccountEndpoints` has said so since
`staff-google-guard` — *"tempting as that is given there is no un-disable"*.

So the accurate statement is narrower: **the withdrawal takes nothing away that was recoverable.**
A disabled account stays disabled today, and the calendar grant now ends with it. If an account
re-enable is ever added — `00-context.md` §5 describes disabling as "a reversible off-switch",
which is a description of intent rather than of behaviour — that change inherits one question:
whether restoring an account should expect its calendar to come back. It cannot, and should not
silently try; the professional reconnects in one click on S2. Recorded here so the question is
waiting for whoever builds it rather than being discovered then.

**One implementation detail this forced.** `CalendarTokenProtector` now resolves its key lazily
rather than in its constructor. It is a dependency of the withdrawal, which is on the disable
path — and disabling a staff account has to keep working on a deployment that never configured a
calendar, which K4 says is supported. A constructor that threw would have made turning off an
account fail because the clinic does not use Google Calendar.

### K15 — Why no scheduler here, restated as a decision rather than an omission

Hangfire is the tool this change would reach for to keep status fresh, expire half-finished flows,
and retry a failed revoke. It is not added, because each of those is a nicety here and none is
correctness: nothing consumes the connection yet. Adding a scheduler to power a status badge would
be the anti-pattern this project has refused five times, and it would land in the change least able
to justify it — 6b has the dispatcher, the retries, the dead-letter, and the two revisit triggers
already waiting for it.

The half-finished-flow case is handled the way change 2 handled it: the connect state cookie carries
its own short lifetime, so an abandoned flow expires in the browser rather than needing a sweep.

## Risks / Trade-offs

**[A stale "connected" badge]** S2 can show a grant that was revoked minutes ago (K8). → Bounded by
the fact that nothing consumes the connection in this change; the timestamp beside the status makes
the staleness visible rather than implied; the "check connection" control makes it resolvable on
demand. **Revisit trigger: 6b's dispatcher**, which detects revocation as a side effect of work.

**[The encryption key is lost or changed]** Every stored token becomes undecryptable, and every
professional must reconnect. → Not silent: `Open` fails loudly and the connection is reported as
needing reconnection rather than as broken. The `.env.example` entry states plainly that this value
must survive redeploys, and the versioned envelope (K3) means a future rotation can keep old
ciphertext readable instead of orphaning it.

**[A granular consent screen quietly declines calendar access]** The single most likely real
failure. → K5 turns it into an explicit, coded refusal with its own message; a test asserts a
token response whose `scope` lacks the calendar scope stores nothing at all.

**[`include_granted_scopes` omitted or lost]** The identity grant is silently replaced, and the
damage appears somewhere unrelated. → Asserted by a test against the constructed authorization URL,
not left to a code comment. This is the second time the project has made a URL parameter a tested
fact rather than a convention.

**[Two Google flows drift apart]** Two callbacks, two state cookies, two token-exchange paths — the
duplication K2 accepts deliberately. → The genuinely general piece (the open-redirect guard) is
shared; the rest is deliberately separate, and the table in K2 is the record of why, so a later
reader tempted to unify them meets the reason first.

**[A revoke call fails and the professional believes they are fully disconnected]** → K9 reports the
partial outcome instead of a plain success, and names where to finish the job.

**[The new required secret breaks an existing deployment]** Anyone running the stack must add one
value. → It is required only when calendar configuration is present (K4), so a deployment that has
not configured the calendar feature is unaffected; `.env.example` and the README's local-run section
carry the generation command.

## Migration Plan

1. **Schema.** One EF migration adding `calendar_connections` (professional-unique, sealed token
   nullable, status, target calendar, connected-at, observed-at). Additive; no existing table is
   touched, so no rollback complexity beyond dropping the table.
2. **Configuration.** `Calendar__*` settings and the encryption key added to `.env.example` and to
   the Compose environment. A deployment that sets none behaves exactly as before.
3. **Google Console (manual, once).** Second redirect URI registered; Calendar API enabled (K14).
4. **Rollback.** Revert the migration and the feature slice. No other capability reads
   `calendar_connections`, and no booking path changed, so a rollback is local to this change —
   which is the practical dividend of shipping 6a with zero propagation.

## Open Questions

> Updated during apply. Two were settled by building the thing; two are still open, and which is
> which is stated rather than blurred.

1. **Key rotation — ANSWERED: accepted.** No rotation runbook ships, and "everybody reconnects" is
   the accepted answer at this scale. The envelope stays versioned (K3) so a real rotation remains
   additive whenever it is wanted, and reconnecting is a two-minute self-service action on S2 —
   against which a rotation procedure would be a second key-management story to keep correct for no
   present benefit. **The revisit trigger is armed** at the same place F8's is: roughly ten
   connected professionals, or any compliance requirement that names key rotation. Recorded here so
   6b inherits a decision rather than an open question.
2. **Disabling a user — ANSWERED: yes, it withdraws the calendar grant.** Implemented in this
   change; the reasoning, the shared primitive it required, and the irreversibility it introduces
   are written up as **K16** above rather than left in a question.
3. **A professional with no `Professional` row — answered, by following the precedent.** The
   connection is keyed on the `Professional` row, matching `02-domain-model.md` §4, and a claimed
   invitation that no administrator has configured yet is refused exactly as S3 refuses it:
   `config.not_found`, because the remedy is administrative and the state resolves itself once S7
   has been used. Inventing a second answer for the same state would have been the worse outcome —
   a professional would meet one behaviour on S3 and a different one on S2. Tested.
4. **What "never connected" should look like — answered provisionally, and the guide can overrule.**
   It is presented as a **state, not a problem**: a plain "no calendar connected", the sentence
   "nothing is required of you — connect when you want to", and no warning styling. The reasoning
   got firmer while building it, because the screen made the honest version obvious: in 6a a
   connected calendar does *nothing yet*, so pushing somebody to connect would be claiming a
   benefit that does not exist. S2 says that out loud in its own alert. **Check 11 of the validation
   guide asks a human whether that reads as honest or as an unfinished screen**, and their answer
   is the one that counts.

## What the implementation changed about this design

Recorded here rather than silently, because three things came out differently than written:

- **The token operations went into a new `GoogleCalendarTokens`, not into `GoogleTokenExchange`**
  (task 5.5). K2's argument — two flows with opposite obligations must not share a branch — applies
  a layer below the callback just as forcefully. The class that asks for an `id_token` and the class
  that asks for a refresh token are now separate types with separate substitution seams.
- **The connection response carries the calendar consent** (task 6.3). K12 said the consent is
  written; nothing said it could be *seen*. Consents are read through P7, a patient-only surface, so
  a professional's calendar consent would have been recorded and unviewable — making
  `identity-session`'s widened "visible to the user they belong to" true on paper and false in the
  product. S2 obtained the consent, so S2 shows what was agreed to and when.
- **`GrantConsent` was narrowed to data-processing** (K12), which was written as a side note and
  turned out to be the only shipped hole this change closes: that endpoint parsed *any*
  `ConsentType` under the patient policy.
