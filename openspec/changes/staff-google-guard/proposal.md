## Why

Validating change 4 with a real Google client exposed a door nobody meant to leave open. A
professional signed in on **S0** *before* an administrator had invited them. The staff surface
sent them through the same Google flow the patient portal uses, that flow found no record for
their address, and change 2's provisioning rule did what it says: an unknown Google email
becomes a **patient**. They ended up with a patient account they never asked for — and then
could not be invited as a professional at all, because S11 refuses an address another live
account already holds (`auth.email_already_in_use`).

Two defects in one incident. The first is that S0 promises "the account the clinic registered
for you" and delivers just-in-time patient provisioning instead; nothing about a staff sign-in
should ever *create* an account. The second is that the recovery path `00-context.md` §5
describes — deactivate the wrong account, invite the address anew — is documented but not
actually reachable: `disable` turns an account off without releasing its address, and S11 lists
staff only, so a patient account created by mistake is invisible to the administrator who has
to clear it.

Running this change's own validation guide then turned up the **mirror image on the patient
portal**: a professional signing in on P1 was admitted, and P7 answered "no such patient record"
— because a professional has no patient row and never will. Quieter and worse, an unclaimed
invitation was *claimed* on the way in. Same defect, opposite direction: a surface establishing a
session for someone it cannot serve. So the fix is stated as one symmetric rule rather than as a
guard bolted onto S0.

It lands **before 5a** deliberately (`05-openspec-workflow.md` §7). Change 5 stacks five more
Google-reachable screens on this flow; fixing the door first means those screens are validated
against a sign-in that behaves.

## What Changes

- **The Google flow becomes surface-scoped, and each surface admits only the role it serves.**
  A flow started from S0 accepts exactly one thing: a `User` with `Role=Professional` — either a
  `PendingClaim` invitation it claims, or one already claimed signing in again. Anything else
  is refused with **no account created and no session established**:
  - no record for the address → `auth.not_provisioned`, a translated "ask administration to
    register your access";
  - an address belonging to an internal account → `auth.google_failed` (**unchanged**);
  - an address belonging to an existing **patient** → `auth.use_patient_sign_in`, which names
    the door that *is* theirs. Establishing a patient session inside the staff console produces
    a signed-in user for whom every screen is forbidden — a worse answer than a clear refusal.
- **The same rule applies at the portal, in the other direction.** Found by running this
  change's own validation guide: a **professional** signing in on **P1** used to get a session,
  and then P7 answered `patient.not_found`, because a professional has no patient record and
  never will. Quieter and worse, an unclaimed invitation was **claimed** on the way in — a write
  performed on the wrong surface. P1 now admits only `Role=Patient` and refuses a professional
  with `auth.use_staff_sign_in`, before any write and before any session. Same defect as the S0
  hole, same fix, opposite direction.
- **P1's provisioning itself is untouched.** An unknown Google email on P1 still becomes a
  patient with its minimal record and its data-processing consent, exactly as change 2 built it.
  That is the one respect in which the surfaces still differ, and it is deliberate: the portal
  provisions, the console does not. The divergence is decided by **where the flow was started**
  — carried in the flow's own `HttpOnly` state cookie — not by anything in the token. The same
  Google identity is a legitimate patient on P1 and a refused stranger on S0.
- **The recovery path becomes real, not just documented.** A new administrator action
  **deactivates** an account: a soft-delete (I10) that ends its access *and* releases its
  address, because `User` email uniqueness is already evaluated over live records only. Paired
  with a by-address lookup so an administrator can reach the account holding an address S11
  does not list, this makes "deactivate and invite anew" an operation rather than a sentence in
  a document. It also unsticks the account the validation run created.
- **The refusal message reaches the screen it is written for.** `RequireAuth` drops the query
  string when it bounces an unauthenticated visitor to the sign-in route, so the `authError`
  code the callback reports has never actually arrived at S0 or P1 — the Alert both screens
  already render for it could not fire. Fixing that is not optional decoration here: "a clean
  refusal in both locales" *is* this change's user-facing half. The same fix restores
  `auth.google_failed`'s message on both surfaces.

## Capabilities

### New Capabilities
<!-- None. This change corrects and extends an existing capability. -->

### Modified Capabilities
- `identity-session`: **modifies** "A user's role is provisioned deterministically, never
  inferred" — provisioning is now scoped to the surface the sign-in began on, each surface
  admits only the role it serves, and the staff surface creates nothing. **Modifies**
  "Administrators manage staff accounts and professional invitations" — an administrator can deactivate an account, which releases its address for
  re-invitation, and can resolve the account holding a given address. **Modifies** "Both
  surfaces present sign-in and guard authenticated routes" — a refusal reported by redirect has
  to reach the sign-in screen that translates it.

## Impact

- **`Api`** — `Features/Auth/CompleteGoogleSignIn.cs` gains the surface branch and the
  refusal-before-create ordering; `Infrastructure/Auth/Google/GoogleOAuthState.cs` gains the
  surface classification off the sanitized return path; `Features/StaffAccounts` gains the
  deactivate action and the by-address lookup; `Infrastructure/Errors/ErrorResponse.cs` gains
  `auth.not_provisioned`, `auth.use_patient_sign_in`, and `auth.use_staff_sign_in`.
- **`Domain`** — no new entity. `User.SoftDelete` already exists and already sets the status;
  nothing about roles, claiming, or immutability moves. **No role mutation is introduced**
  (`00-context.md` §5): recovery creates a *new* `User`, which is what keeps `AccessLog`
  history honest about who held which role when.
- **No migration.** The unique index `ix_users_email_live` is already filtered to
  `deleted_at_utc IS NULL`, which is exactly the "active records only" rule the recovery path
  needs. This change adds the test that pins it and the product action that uses it.
- **`packages/shared`** — `RequireAuth` preserves the query string across the sign-in bounce;
  the three new `errors.*` keys in pt-BR and en; the S11 client gains the two new calls.
- **`apps/staff`** — S11's invite form turns the 409 into the recovery flow (who holds this
  address, deactivate it, invite again) as an administrator-confirmed two-step. No new screen.
- **`docs/`** — `05-openspec-workflow.md` §7 and the `auth.not_provisioned` catalogue row were
  written ahead of this change as its input. Two needed correcting once the code existed:
  `07-error-codes.md` gains the two wrong-door codes and narrows `auth.not_provisioned` to its
  true meaning (no account at all), and `00-context.md` §5 now states the symmetric rule, that
  uniqueness is over **live** rather than loosely "active" records, and how `disable` and
  `deactivate` differ.
- **Tests** — the S0 outcomes, the P1 outcomes in both directions (JIT unchanged, professional
  refused, invitation not claimable through the wrong door), the deactivate-then-reinvite
  recovery, and the address-release property of the uniqueness rule.

### Not touched

- **P1's just-in-time patient provisioning.** Not narrowed, not conditioned, not moved. An
  unknown Google email on the patient portal still becomes a patient. If it did not, the
  product would have no patients. What *did* change at P1 is only who is admitted once a record
  already exists — a professional is sent to the staff console instead of into a session that
  cannot work. The proposal originally fenced P1 off entirely; validation showed the same defect
  living there, and shipping the principle on one surface only would have been the wrong kind of
  scope discipline.
- **The internal-account refusal.** A Google sign-in for an address belonging to an internal
  account is still `auth.google_failed`, on both surfaces, for the reason change 2 gave: it is
  a provider-level rule about account takeover, not a question of which door you came through.
  The password sign-in path is not touched at all, including what a disabled account reports.
- **The existing `disable` action.** It stays exactly as it is — an off-switch that keeps the
  address — so nothing already relying on `auth.account_disabled` shifts. Deactivation is a
  second, distinct action; the design records why they are not merged.
- **Any "promote user" or role-change feature.** A role never changes (`00-context.md` §5).
  This change makes deactivate-and-recreate work; it does not add the shortcut.
- **Token validation.** Signature, `iss`, `aud`, `exp`, `nonce`, `email_verified`, `state`
  single-use — all unchanged. The new rule runs strictly *after* every one of them.
- **Session mechanics, RBAC, ownership, `AccessLog`, consent.** Nothing here changes what a
  session is or what a role may do; it changes only which sign-ins produce one.
- **Booking and availability.** No appointment, slot, or solver work of any kind.
- **A patient-search surface.** The by-address lookup answers one question about one address an
  administrator already typed. Browsing patients belongs to change 5, where it has a purpose
  and an `AccessLog` reason.
