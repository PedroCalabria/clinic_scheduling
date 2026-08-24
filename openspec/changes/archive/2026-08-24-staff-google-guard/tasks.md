## 1. The error code and its translations

- [x] 1.1 Add `NotProvisioned = "auth.not_provisioned"` to `apps/api/src/Api/Infrastructure/Errors/ErrorResponse.cs`, beside `GoogleFailed`, with a comment naming the distinction: the token is valid, there is simply nothing to claim (`07-error-codes.md` already carries the catalogue entry).
- [x] 1.2 Add `errors.auth_not_provisioned` to `packages/shared/src/i18n/en.json` and `pt-BR.json`. The message tells the reader that this clinic has not registered an account for their address and that administration must do so — it must not suggest they retry, and must not imply an account was created. Verify with `pnpm check:i18n`.

## 2. Surface classification

- [x] 2.1 Add the staff base path as one named constant on the API side (`/staff`), placed so it reads as the counterpart of the per-app JS constant `00-context.md` §"Base paths" pins.
- [x] 2.2 Add a surface classification to `GoogleOAuthState` — the already-sanitized `ReturnPath` under the staff base path is the staff surface, anything else is the patient portal (design D1). Cover both the exact path and a deeper path, and that a protocol-relative or otherwise unsafe `returnTo` (already reduced to `/` by `SafeReturnPath`) classifies as the patient portal.
- [x] 2.3 Unit-test the classification in `Domain.UnitTests`, or in the integration suite if the type stays internal to `Api` — whichever keeps it tested without moving infrastructure into `Domain`.

## 3. The staff surface becomes claim-only

- [x] 3.1 In `CompleteGoogleSignIn.ResolveUserAsync`, thread the surface through and branch after the internal-account refusal (design D2): on the staff surface accept only `Role == Professional` — claiming a `PendingClaim` invitation, or reusing an already-claimed one — and return `auth.not_provisioned` for everything else, creating nothing.
- [x] 3.2 Leave the patient-portal branch untouched, including the user/patient/consent write and its single `SaveChangesAsync`. Confirm by diff that the create path has not moved.
- [x] 3.3 Log the refusal at warning level with the surface and whether an account existed, and never the email — enough for an operator to see "someone tried to sign in on S0 un-invited" without putting an address in the log.
- [x] 3.4 Verify the refusal is decided before any write: no `SaveChangesAsync`, no `Add`, and no session issuance is reachable on the staff refusal paths.

## 4. Recovery: deactivation and address lookup

- [x] 4.1 Add `POST /api/staff-accounts/{userId}/deactivate` (administrator policy, alongside the existing `disable`): soft-delete via `User.SoftDelete`, revoke the user's sessions, `404` `auth.account_not_found` for an unknown or already-deactivated id, and reachable for an account of **any** role including patient (design D4).
- [x] 4.2 Refuse deactivating the calling administrator's own account with `403` `auth.forbidden`, and log the successful deactivation at warning level naming actor, target, and the target's role.
- [x] 4.3 Add `GET /api/staff-accounts/by-email?email=...` (administrator policy): returns id, email, role, status and `awaitsClaim` for the live account holding that normalized address, `404` `auth.account_not_found` otherwise. It returns no personal data beyond the address the administrator supplied.
- [x] 4.4 Leave the existing `disable` action and its handler exactly as they are, so nothing relying on `auth.account_disabled` shifts.

## 5. Frontend — the refusal arrives, and the recovery is reachable

- [x] 5.1 Make `RequireAuth` preserve the current query string when it bounces to `signInPath` (design D6), so a refusal code returned to a guarded route reaches the sign-in screen. Confirm the existing `state: { from }` behaviour is unchanged.
- [x] 5.2 Add the two S11 client calls (`deactivateStaffAccount`, `findStaffAccountByEmail`) to `packages/shared/src/api/client.ts` beside the existing staff-account calls.
- [x] 5.3 In S11's invite form, turn the `auth.email_already_in_use` refusal into the recovery flow (design D5): look up who holds the address, show its role and status, and offer an explicitly confirmed deactivation followed by a retry of the invite — two confirmed steps, never one combined action. New copy in both locales.
- [x] 5.4 Check the S0 sign-in link still names a `/staff` return path, and that a professional's successful claim still lands inside the staff console rather than the patient portal.

## 6. Tests

- [x] 6.1 `GoogleSignInTests`: a staff-surface flow (`returnTo` under `/staff`) with an unknown email is refused with `auth.not_provisioned`, establishes no session, and leaves `users`, `patients`, and `consents` with no new row for that address.
- [x] 6.2 `GoogleSignInTests`: a staff-surface flow for a `PendingClaim` professional still claims the account and establishes the session — the existing test, kept green and extended to assert the session works.
- [x] 6.3 `GoogleSignInTests`: a staff-surface flow for an email belonging to an existing patient is refused with `auth.not_provisioned` and changes nothing about that patient.
- [x] 6.4 `GoogleSignInTests`: the patient-portal flow with an unknown email still provisions the patient, the patient record, and the consent — change 2's behaviour, asserted as unchanged.
- [x] 6.5 `GoogleSignInTests`: one identity signs in on the patient portal, then is refused from the staff surface — the divergence is the surface, not the token.
- [x] 6.6 `GoogleSignInTests`: the internal-account refusal is `auth.google_failed` from **both** surfaces.
- [x] 6.7 `StaffAccountTests`: deactivate a patient account, then invite the same address as a professional — succeeds, produces a new user with a new id and `Role=Professional`, and the deactivated row is still present and soft-deleted. This is the email-uniqueness-over-live-records property, pinned.
- [x] 6.8 `StaffAccountTests`: `disable` still leaves the address taken (`409 auth.email_already_in_use`), so the two actions are distinguishable by test and not only by name.
- [x] 6.9 `StaffAccountTests`: deactivation ends an active session on its next request; deactivating one's own account is `403 auth.forbidden`; a front-desk caller is `403 auth.forbidden`; the by-email lookup finds a patient account and reports `auth.account_not_found` for an unknown address.
- [x] 6.10 End-to-end recovery in one integration test: unknown email refused on S0, administrator resolves the address and finds nothing, invites it, the same identity claims it — the incident that motivated this change, run forward.
- [x] 6.11 Pin the staff sign-in entry so a future screen cannot drop the `/staff` return path (design D1 risk). Closed by construction instead of by a test: `apps/staff/src/config/signIn.ts` derives the prefix from the same constant as the router basename, so the omission is unrepresentable. The JS side has no unit-test tier, and a test that can only detect the regression is weaker than a shape that prevents it.

## 7. Documentation

- [x] 7.1 Update `README.md`'s status table: extend increment 2's "What a person can do" cell by one clause — a professional who has not been invited is told to ask administration rather than quietly becoming a patient. This change is a correction to increment 2, not a tenth increment, so it adds no row and the "nine increments" line does not move (`00-context.md` §8).
- [x] 7.2 Checked `docs/00-context.md` §5, `docs/07-error-codes.md`, and `docs/05-openspec-workflow.md` §7 against what was built. The error catalogue and the workflow doc match. §5 needed two corrections, because it is the durable substrate every later change inherits: "uniqueness over **active** records" was ambiguous and, read literally, wrong — a `Disabled` account still holds its address, so it now says **live (non-deactivated)** and spells out that `disable` keeps the address while `deactivate` releases it; and it did not say that an existing **patient** is refused on S0, or that the surface is decided by the return path fixed at flow start.

## 8. The mirror defect at the portal — found by running the validation guide

> Discovered while running §9.6 below: a **professional** signing in on **P1** was admitted
> and then met `patient.not_found` on P7, and an unclaimed invitation was claimed on the way
> in. The same defect as the S0 hole, in the other direction, so the rule became symmetric
> rather than an S0 guard. Group 9 was re-run afterwards.


- [x] 8.1 Add `auth.use_patient_sign_in` and `auth.use_staff_sign_in` to `docs/07-error-codes.md` — catalogue before code, as the project rule requires — and narrow `auth.not_provisioned`'s row to its true meaning: no account at all. Three remedies, three codes.
- [x] 8.2 Add both constants to `Infrastructure/Errors/ErrorResponse.cs`, each stating why it is not the other and why neither is `auth.google_failed`.
- [x] 8.3 Make `AdmitOnSurface` symmetric: the staff surface admits `Role.Professional`, the portal admits `Role.Patient`, and anything else is refused with the code naming the other door. Keep it a per-surface whitelist so a role added later is refused by default.
- [x] 8.4 Confirm the admission still runs *before* the claim, so an invitation reached from the portal is not claimed on the way in — this is the write the ordering was built to prevent, and it was actually happening.
- [x] 8.5 i18n for both codes in pt-BR and en. Each message names the surface to use, and the patient-at-the-staff-door message must not tell them to ask administration for access they already have.
- [x] 8.6 Tests: a professional is refused on P1 with no session, no patient row and no consent; an unclaimed invitation reached from P1 stays unclaimed and then claims normally from S0; the S0-patient tests move to `auth.use_patient_sign_in`.
- [x] 8.7 Update `docs/00-context.md` §5 to state the rule symmetrically — each surface admits the role it serves; the only asymmetry is what happens to an address with no account at all.
- [x] 8.8 Update the change artifacts to match: `proposal.md` (the P1 fence moves), `design.md` (D2 becomes symmetric, new D2a on splitting the codes), the spec delta, and `validation.md` (a check for the portal direction).

## 9. Definition of Done

> Every gate below was run again after group 8.

- [x] 9.1 `dotnet build` clean with warnings-as-errors; `pnpm build` clean for both apps and the shared package.
- [x] 9.2 Unit and integration tests green against a real PostgreSQL via Testcontainers.
- [x] 9.3 `pnpm check:i18n` green — pt-BR and en both carry every new key, and no screen asks for one that does not exist.
- [x] 9.4 `pnpm smoke` green against the running Compose stack.
- [x] 9.5 `openspec validate staff-google-guard --strict` passes.
- [ ] 9.6 `openspec/changes/staff-google-guard/validation.md` has been **executed** against the locally-running app with the real Google client, both locales, and its checks confirmed (`00-context.md` §9) — including that no patient row was created by the refused sign-in. **Left open deliberately: this one cannot be done for the maintainer.** It needs the configured Google client and two real Google accounts they control; the whole point of `00-context.md` §9 is that nobody ticks this box on someone else's behalf.
- [x] 9.7 README status table reflects what now works.
