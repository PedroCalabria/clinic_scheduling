## Context

Change 2 built one Google flow with one provisioning rule, and that rule was written before
either surface existed as something a person could click. `CompleteGoogleSignIn.ResolveUserAsync`
resolves a verified identity in three steps — by subject, then by email, then create-a-patient —
and the third step is unconditional. Both surfaces enter through the same
`/api/auth/google/start` and leave through the same `/api/auth/google/callback`, so both get the
third step.

That was invisible while nobody had a real Google client. With one configured, the first
professional to click "Sign in with Google" on S0 before being invited became a patient
(`00-context.md` §9 is exactly the reason this surfaced in validation and not in CI: the offline
Google double exercises the token, not the human's order of operations).

The same guide, run against the first cut of this change, found the other half of it: a
**professional signing in on P1** was admitted, landed on P7, and read "no such patient record".
The `patient.not_found` code was doing its job — a professional has no patient row — but the
session should never have existed. And on the way in, an unclaimed invitation was *claimed*
through the portal. Both incidents are one sentence: **a surface established a session for
someone it cannot serve.** The design below therefore states the rule for both surfaces at once;
treating it as an S0 guard is what produced the second bug.

Two constraints shape the fix. First, the refusal must be **before** any write: the damage is
not the refusal, it is the row. Second, whatever distinguishes the surfaces must not be
forgeable into *more* access than the caller already has, and must not be something a future
screen can silently omit — the failure mode this change exists to close is precisely a silent
default.

Three facts about the existing code do most of the work here, and the design leans on them
rather than adding machinery:

- the flow already carries an `HttpOnly` state cookie written at **start** and consumed at
  **callback**, holding a `returnPath` already reduced to a local path by `SafeReturnPath`;
- `ix_users_email_live` is already filtered to `deleted_at_utc IS NULL`, and every by-email
  lookup in the codebase already filters the same way;
- `User.SoftDelete` already exists on the aggregate and already sets the status.

## Goals / Non-Goals

**Goals:**

- A Google sign-in started on S0 never creates an account, and never establishes a session for
  anyone but a professional the clinic registered.
- Neither surface ever establishes a session for a role it cannot serve, and the refusal names
  the surface that can.
- P1 keeps change 2's just-in-time patient provisioning byte for byte.
- The divergence is decided by where the flow started, in a place the callback request cannot
  restate, and defaults to the *restrictive* answer when a staff surface is involved.
- The deactivate-and-invite-anew recovery in `00-context.md` §5 becomes an operation an
  administrator can actually perform, including on an account S11 does not list.
- The refusal is a sentence the user reads, on the screen they were sent back to, in both
  product languages.

**Non-Goals:**

- Changing P1's *provisioning* — an unknown address there still becomes a patient. Who is
  admitted once a record exists is in scope; what happens when no record exists is not.
- Changing the internal-account refusal or the password sign-in path.
- Role mutation in any form. Recovery is deactivate-and-recreate; that is a decision, not a
  gap (`00-context.md` §5).
- A patient-management surface. Change 5 owns patient browsing.
- Any new persistence: no migration, no schema change, no new entity.

## Decisions

### D1 — The surface is derived from the flow's own return path, not from a new parameter

The callback classifies the flow by the `returnPath` it reads out of the state cookie: a path
under the staff base path (`/staff`) is the **staff** surface; everything else is the **patient
portal**. One helper on `GoogleOAuthState`, one branch in `ResolveUserAsync`.

*Why this and not an explicit `surface=staff` query parameter on `/start`:* an explicit
parameter needs a default, and every available default is worse than this inference. Defaulting
to *patient* means a staff entry point that forgets the parameter silently regains
just-in-time provisioning — the exact bug being fixed, reintroduced by omission, invisibly.
Defaulting to *staff* breaks P1 the day someone links to sign-in without it. The return path,
by contrast, cannot be omitted by a staff screen even in principle: every staff route lives
under the `/staff` basename by construction (`00-context.md` §"Base paths" pins `base` and
`basename` to one per-app constant precisely so they cannot drift), so a staff sign-in that
means to come back to a staff screen *has* to name a `/staff` path. And when a staff entry point
does get it wrong, the failure is loud rather than silent: the professional is redirected to the
patient portal after signing in, which someone notices in the first minute.

*Why not two route pairs* (`/api/auth/google/staff/start` + its own callback), which would make
the guard structural rather than inferential: it needs a second `redirect_uri` registered in the
Google Cloud console, so it turns a code change into an operator change in `08-google-setup.md`
for every deployment, and it doubles the surface the state/nonce/replay tests have to cover. The
inference costs a small duplicated constant on the API side (`/staff`, which the JS side already
holds); the routes cost setup no other part of this project needs.

*Security argument for reading it from the cookie:* the value is fixed at `start`, in a cookie
the browser owner can technically forge but gains nothing by forging. Forging `/staff` only
narrows what they are allowed (claim-only), and forging `/` from S0 yields patient provisioning
they could obtain by simply visiting P1 — no privilege is reachable that was not already. The
one residual is self-inflicted: deliberately mangling your own `returnTo` from S0 can still
create a patient account and thereby block your own invitation. The recovery path (D4) is the
answer to that, and it is a far better trade than an authorization decision that fails open.

### D2 — Each surface admits the role it serves, as a whitelist, before any write

Ordering, stated as ordering because that is the whole defect:

1. subject / email resolution (unchanged reads);
2. the internal-account refusal (`auth.google_failed`, unchanged — it is a provider-level rule
   about mailbox takeover and applies on both surfaces);
3. **the surface admission**: on staff accept only `Role == Professional`, on the portal accept
   only `Role == Patient`, and refuse anything else by naming the other door;
4. on staff, for an admitted professional: claim the `PendingClaim` invitation if unclaimed,
   reuse it if already claimed;
5. on the portal, for an address with **no account at all**: create the patient, exactly as
   change 2 did. This is the one asymmetry, and it is the point of the change.

A whitelist per surface rather than a list of exclusions, so a role added later is refused by
default rather than admitted by omission.

**The rule was originally written for S0 only, and that was wrong.** The draft reasoned that a
patient on S0 must be refused because "the alternative is a session for a user every staff screen
forbids". Running the validation guide showed the identical sentence is true of a professional on
P1 — they were admitted, and P7 answered `patient.not_found`, because a professional has no
patient row and never will. Worse, an unclaimed invitation was *claimed* on the way in: a write
performed on the surface that had no business performing it, which is precisely what step 3
sitting above step 4 exists to prevent. Applying the principle to one surface and not the other
was not scope discipline, it was an unfinished rule. So D2 is now symmetric, and the asymmetry is
confined to what happens when there is nothing to admit at all (step 5).

*Rejected:* refusing at `/start` by checking the address before the redirect. There is no
address to check before Google has authenticated the user, so this cannot exist.

*Rejected:* letting the wrong-door user in and fixing the screens instead — a "you have no
patient record" empty state on P7, a "you are not staff" page in the console. That multiplies by
every screen either surface ever gains, and each one is a chance to forget. One refusal at the
door is one place.

### D2a — A wrong door gets its own code per direction, and `auth.not_provisioned` narrows

Three refusals where the draft had one:

| Situation | Code | Remedy the message gives |
|---|---|---|
| S0, no account anywhere | `auth.not_provisioned` | ask administration to register your access |
| S0, the address is a patient | `auth.use_patient_sign_in` | use the patient portal |
| P1, the address is a professional | `auth.use_staff_sign_in` | use the staff console |

`07-error-codes.md` splits codes on *user-meaningful failure*, and the test of that is whether
the remedy differs. These three remedies are three different places to go, so one code cannot
carry them: the draft sent a patient standing at the staff door to "ask administration to
register your access", which would have them queue up to be told nothing was wrong.

*Rejected:* one `auth.wrong_sign_in_surface` code carrying `params: { surface }` for the frontend
to interpolate. The contract supports it, but the sentence differs by more than a noun — where to
go, and why that door exists — and a translated sentence assembled from a parameter reads like
one. Two codes, two sentences, both written by a human in both languages.

*Rejected:* naming the codes `*_required`. `auth.password_change_required` and
`auth.consent_required` mean "do this first, then continue"; these mean "you are in the wrong
place". `use_*` says that without ambiguity.

Note what does **not** change: an internal account reaching either surface through Google is
still `auth.google_failed`. That refusal is the account-takeover defence — controlling a staff
mailbox must not be enough to sign in as staff — and folding it into a friendly wrong-door
message would be a security regression dressed as consistency.

### D3 — The wrong-door codes are carried as a redirect, and 403 is their nominal status

`07-error-codes.md` lists all three at 403, and they stay listed at 403 — that is the status the
codes *mean*, the same way `auth.google_unavailable` is listed at 503 and is also delivered as a
redirect today. The callback is reached by a top-level browser navigation, so returning a JSON
body would put `{"code":"auth.not_provisioned"}` in the address bar. Change 2 already decided
this for every other refusal in this flow and gave the reason: at least one refusal here is an
ordinary human mistake and deserves a translated sentence. An un-invited professional is now the
clearest example of that.

**This deliberately reads differently from the brief's "403 auth.not_provisioned".** The
integration tests assert what the flow actually does — a `302` back to the surface the flow
started from, carrying `authError=<code>`, with no `User` row and no session — because asserting
a 403 would require breaking the flow's established delivery pattern to satisfy the shape of the
assertion. If a JSON-bodied 403 is genuinely wanted, it is a change to how *all* Google refusals
are reported, and it belongs to its own change.

### D4 — Recovery is a soft-delete, and it is a second action rather than a redefinition of `disable`

"Uniqueness over active records only" is already true in the database: `ix_users_email_live` is
filtered to `deleted_at_utc IS NULL`, and `CreateAsync`, `SignIn`, and the Google resolution all
filter identically. So the address is released by **soft-deleting** the account, and by nothing
else. This change adds the product action that does it — `POST
/api/staff-accounts/{userId}/deactivate` calling `User.SoftDelete` and revoking the sessions —
plus `GET /api/staff-accounts/by-email` so an administrator can reach the account holding an
address that S11 does not list. "Deactivate" is the word the catalog already uses for
soft-deleting an entity (3a) and the word `00-context.md` §5 uses for this recovery, so it is
not a new vocabulary.

*Why not simply make the existing `disable` soft-delete,* collapsing two actions into one —
which is tempting, because the product has no un-disable, so a `Disabled` row is already
effectively permanent: because it would silently change what the **password** sign-in path
reports. A soft-deleted account is not found by `SignIn`'s lookup, so a deactivated internal
account would answer `auth.invalid_credentials` where it answers `auth.account_disabled` today —
a spec scenario and an existing test, on the one path this change is not allowed to touch.
Adding a second action changes nothing that exists. **Revisit trigger:** if `disable` is still
unused by any real workflow after change 5, collapse the two and take the sign-in-path spec
change deliberately.

*Why the by-address lookup rather than listing patients in S11:* it answers one question about
one address the administrator has just typed into the invite form, returning role, status and
nothing else. Listing patients is a surface with an `AccessLog` reason and a purpose, and both
arrive in change 5.

*Two guards on the new endpoint,* because it is the first destructive account action: an
administrator cannot deactivate their own account (`auth.forbidden` — otherwise the clinic can
lock itself out of S11), and the action is logged as a warning naming the actor, the target, and
the target's role. **Revisit trigger:** once appointments exist (change 5), deactivating an
account with future appointments needs a decision; today there is nothing to orphan.

### D5 — The recovery is two administrator-confirmed steps, never one button

S11's invite form already surfaces the 409. It now offers, on that refusal, to show who holds
the address and to deactivate that account before retrying the invite. Presented as two
confirmed steps rather than one "deactivate and invite" action, because the one-button version
is indistinguishable in the UI from the role-change feature `00-context.md` §5 rules out, and
would be read that way by whoever maintains it next. The two steps produce what the convention
promises: the old `User` retired with its history intact, and a **new** `User` with a new id and
`Role=Professional` — which is what keeps `AccessLog` honest about who held which role when.

### D6 — Preserving the query string on the sign-in bounce is part of this change, not a drive-by

`RequireAuth` bounces an unauthenticated visitor with `<Navigate to={signInPath}>`, which keeps
neither search nor hash. Both surfaces send the callback back to a *guarded* path (`/staff/` and
`/profile`), so the `authError` code lands on a guarded route, is bounced to the sign-in route
with the query dropped, and the Alert that both S0 and P1 already render for it never fires.
This change's entire user-facing half is a translated refusal on S0, so the bounce has to carry
the query.

Fixed in `RequireAuth` (one `to` object gaining `search`) rather than by pointing each surface's
`returnTo` at its own sign-in route, because the latter changes where a *successful* sign-in
lands — a patient would arrive at the landing page instead of their profile — trading a real UX
regression for the same result. Fixing it in the shared guard also repairs
`auth.google_failed`'s message on both surfaces, which has been equally invisible since change 2.

## Risks / Trade-offs

- **A future staff entry point passes a non-`/staff` return path and silently regains JIT
  provisioning.** → Closed by construction rather than by a test: the staff app gets a
  `staffGoogleSignInUrl` helper that prefixes the return path from the same constant the router
  basename derives from, so a staff screen cannot start the flow without it. (The draft design
  planned a test asserting the link; making the omission unrepresentable is strictly better, and
  it is what the JS side had no test tier for anyway.) Beyond that, the inference is hard to get
  wrong accidentally — staff routes are under the basename by construction — and loud when it is
  wrong, because the user lands on the patient portal after signing in.
- **A caller who mangles their own `returnTo` from S0 can still create a patient account for
  their own address.** → Accepted: no privilege is gained that P1 does not already offer, and
  the address is recoverable via D4. Closing it would mean a fail-open authorization default,
  which is worse.
- **Deactivation is a destructive administrator action reachable in two clicks from an invite
  refusal.** → Explicit confirmation naming the address and role, a self-deactivation guard, a
  warning-level audit log, and soft-delete only (I10) so the row and its history survive.
- **`disable` and `deactivate` sit side by side and differ only in whether the address is
  released.** → Named with the project's existing vocabulary and distinguished in the S11 copy;
  recorded above with a revisit trigger to collapse them.
- **The staff base path exists as a constant on both the API and the JS side.** → Duplication of
  one string that `00-context.md` pins as a project-wide decision and that nothing else may
  change unilaterally. Asserted from the API side by test, so a drift fails rather than
  misclassifies.
- **`auth.not_provisioned` is delivered as a 302 while catalogued at 403.** → Consistent with
  `auth.google_unavailable`; stated in D3 so the next reader does not treat it as an oversight.

## Migration Plan

No migration. No schema change, no data backfill, no configuration. The one piece of existing
data this change touches is the account the validation run created by mistake, and it is
cleared by the new recovery path itself — deactivate it in S11, invite the address as a
professional, claim it with the same Google account. That sequence is step 4 of `validation.md`,
so the fix demonstrates itself on the data that motivated it.

Rollback is a revert: nothing outside the code has changed state.

## Open Questions

None blocking. Two recorded for later:

- Whether `disable` survives change 5 as a distinct action, or collapses into `deactivate`
  (D4's revisit trigger).
- What deactivating an account with future appointments should do — undecided because there are
  no appointments until 5a.
