## 1. Domain core (first real content in the protected core)

- [x] 1.1 Add `Role`, `UserStatus`, and `AuthProvider` to `Domain`, and the `User` entity with `email`, `authProvider`, `externalSubjectId`, `passwordHash`, `role`, `status`, and soft-delete marker (I10)
- [x] 1.2 Enforce in `User` that `role` and `authProvider` cannot change after creation (A5), exposing creation through intent-named factories rather than a settable surface
- [x] 1.3 Add `Patient` (1:1 with `User`, minimal PII only) and `Consent` (`type`, `version`, `grantedAt`, `revokedAt`), with revocation recorded rather than the grant erased
- [x] 1.4 Declare the `IPasswordHasher` port in `Domain` (plain interface — the implementation lives in `Api`, per A7)
- [x] 1.5 Model the ownership rule as a domain concept so both `Api` and its tests reference one definition of "this user owns this patient data" (A8)
- [x] 1.6 Unit-test the invariants: role immutability, `authProvider` immutability, consent revocation preserving the grant, and the ownership rule's true/false cases
- [x] 1.7 Confirm `Domain` still references no infrastructure — the `ForbidInfrastructureReferences` target and `DomainBoundaryTests` must both stay green with the new types in place

## 2. Persistence & migration

- [x] 2.1 Add EF configurations for `User`, `Patient`, `Consent`, and `AccessLog` under the `Api` infrastructure folder, alongside the existing marker context
- [x] 2.2 Add the `Session` entity and its configuration: opaque id, `userId`, `expiresAt`, revocation marker, and creation metadata (A1)
- [x] 2.3 Add a unique index on email scoped to non-deleted rows, and a unique index on (`authProvider`, `externalSubjectId`) — soft-delete must not block re-use of an email forever, and the subject index is what makes repeat sign-in a single lookup
- [x] 2.4 Add the index supporting session lookup by opaque id, since it runs on every authenticated request
- [x] 2.5 Generate the EF migration for all five tables and apply it against the containerized PostgreSQL through the existing startup step
- [x] 2.6 Verify the marker table and migration from change 1 still apply cleanly in sequence (the migration chain, not just the new migration, is what CI runs)

## 3. Session mechanism (A1, A2)

- [x] 3.1 Add a session store in `Api` that creates a session with a cryptographically strong opaque id, resolves one by id, and revokes one
- [x] 3.2 Make resolution reject expired and revoked sessions at read time (expiry-on-read — no sweep, per the non-goal), and record the revisit trigger in a code comment as change 1 did for the migration caveat
- [x] 3.3 Add the custom `AuthenticationHandler` that resolves the cookie to a `ClaimsPrincipal` carrying user id and role, and nothing else authorization-bearing
- [x] 3.4 Register authentication and authorization so `[Authorize]` is the default and anonymous access is an explicit opt-out; opt `GET /api/health` out by name
- [x] 3.5 Set the session cookie `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, host-only, with no environment-conditional branch (A2) — add the comment explaining why `Strict` breaks the OIDC return
- [x] 3.6 Map an unresolvable, expired, revoked, or forged session to `401 auth.session_expired` through the existing error envelope, disclosing nothing about which sessions exist
- [x] 3.7 Confirm `/api/health` is still anonymous and unchanged, through the integration test and by inspection of the endpoint's registration

## 4. Test seams (build before the rest depends on them — A13)

- [x] 4.1 Extend the integration fixture with `AsRole(...)` / `AsUser(...)`: seed a user and session directly, return an `HttpClient` carrying the cookie — one line at the call site
- [x] 4.2 Verify the fixture composes with Respawn: identity data is seeded per test and no reset leaves a test depending on startup bootstrap
- [x] 4.3 Add the `IGoogleIdTokenValidator` abstraction with the JWKS-backed implementation registered for the app
- [x] 4.4 Add the test implementation that validates tokens signed by a locally generated key, plus a helper minting ID tokens with arbitrary claims (`sub`, `email`, `email_verified`, `nonce`, `aud`, `iss`, `exp`)
- [x] 4.5 Route Google's token exchange through a named `HttpClient` and add the stub handler tests substitute for it
- [x] 4.6 Add one test proving the seam actually validates — a token with a bad signature, wrong `aud`, or expired `exp` must be rejected by the same code path the app uses

## 5. Internal-account authentication

- [x] 5.1 Implement `IPasswordHasher` in `Api` over `PasswordHasher<User>`, with no Identity store and no Identity schema (A1)
- [x] 5.2 Create the auth slice under `Api/Features` following the health slice's structure, with the sign-in endpoint
- [x] 5.3 Sign in on valid credentials: verify the hash, create the session, set the cookie, and return no session id or hash in the body
- [x] 5.4 Return `401 auth.invalid_credentials` for both a wrong password and an unknown email — identical status, code, and shape, so the response does not answer whether the account exists
- [x] 5.5 Return `403 auth.account_disabled` when credentials are correct but the account is disabled or locked
- [x] 5.6 Add the sign-out endpoint: revoke the session and clear the cookie
- [x] 5.7 Add `GET /api/auth/session` returning the current principal's identity and role, as the frontend's single source of truth (A11)
- [x] 5.8 Integration-test the full internal login flow end to end, including that a revoked session's next request is refused

## 6. Administrator bootstrap

- [x] 6.1 Add the idempotent bootstrap startup step, ordered after the migration step, creating the administrator from configuration when no administrator exists
- [x] 6.2 Make repeat runs a no-op that never overwrites a password the operator has since changed
- [x] 6.3 Add the forced-password-change marker and enforce it: the bootstrapped administrator still holding the supplied credential must change it before other work proceeds
- [x] 6.4 Emit a warning naming the account when the bootstrap credential is still in use, so the signal appears in logs and not only on a screen
- [x] 6.5 Add the bootstrap variables to `.env.example` with a comment that they are credentials, not settings
- [x] 6.6 Integration-test first-start creation, restart idempotency, and the forced change

## 7. Authorization: role, ownership, and access logging

- [x] 7.1 Define role policies for administrator, front desk, professional, and patient, and apply them at the endpoints rather than inside handlers
- [x] 7.2 Add the `OwnershipRequirement` and its handler for resource-based authorization, resolving the owner from the session only (A8)
- [x] 7.3 Return `403 auth.forbidden` for a role refusal and `403 auth.ownership_denied` for an ownership refusal, both distinct from the `401` an unauthenticated caller receives
- [x] 7.4 Add the patient profile-and-consents endpoints (read and update) as the first consumers of the ownership primitive
- [x] 7.5 Verify a client-supplied patient identifier can only narrow access and never widen it, including when it disagrees with the session
- [x] 7.6 Add `AccessLog` writing at the slices where staff read patient data, and confirm a patient reading their own data writes nothing (A9)
- [x] 7.7 Integration-test the refusals: front desk denied an administrator action, patient A denied patient B's profile on both read and update, and the staff-read path producing an access record

## 8. Login-path hardening

- [x] 8.1 Add the native rate limiter partitioned by client address, scoped to the login endpoints only (A10)
- [x] 8.2 Write the limiter's rejection through the error envelope as `429 auth.rate_limited` instead of the middleware's default empty response
- [x] 8.3 Add the per-account failed-attempt counter and lockout, resetting on success
- [x] 8.4 Add the API's double-submit CSRF defence: a readable token cookie plus a required header on unsafe methods, refusing the request when they disagree (A3)
- [x] 8.5 Integration-test that ordinary authenticated requests to non-login endpoints are unaffected by the login limiter, that lockout answers `403 auth.account_disabled`, and that a state-changing request missing the CSRF header is refused

## 9. Google OIDC path

- [x] 9.1 Add the sign-in start endpoint: build the Google authorization URL with identity scopes only, and issue the short-lived `state`+`nonce` cookie (A3, A6)
- [x] 9.2 Add the callback endpoint: validate `state`, exchange the code, validate the ID token including `nonce`, and clear the state cookie on consumption
- [x] 9.3 Reject a missing or mismatched `state` and a replayed callback with `401 auth.google_failed`
- [x] 9.4 Require `email_verified` and reject the sign-in otherwise — the check that makes email-based claiming safe (A4)
- [x] 9.5 Implement provisioning: known subject signs in unchanged; unknown email creates a patient with minimal PII and a versioned data-processing consent; an email prepared as a professional is claimed by binding the subject to that user
- [x] 9.6 Refuse a Google sign-in whose email belongs to an internal account with `auth.google_failed`, so a staff account is never takeable through its mailbox
- [x] 9.7 Make Google configuration optional: absent credentials leave the app starting normally with the internal path working, and the Google endpoints answering a clear configuration error (A14)
- [x] 9.8 Integration-test each provisioning branch, the repeat sign-in creating no duplicate, and that no refresh token or calendar scope is ever requested or stored

## 10. Staff account administration (S11's API)

- [x] 10.1 Add administrator-only endpoints to create an internal staff account, register a professional by email for later claiming, list accounts, and disable an account
- [x] 10.2 Reject a duplicate email with `409 auth.email_already_in_use`
- [x] 10.3 Make disabling an account revoke that user's existing sessions as well as preventing new ones
- [x] 10.4 Ensure account removal is soft-delete only (I10), and that a soft-deleted account's email can be re-used
- [x] 10.5 Integration-test creation of each account kind, the duplicate-email refusal, and that disabling ends an active session on its next request

## 11. Error codes and i18n

- [x] 11.1 Confirm every code used by this change exists in `docs/07-error-codes.md`, and add any that emerged during implementation there first
- [x] 11.2 Add pt-BR and en messages for every auth code and every new user-facing string, in the shared resource files
- [x] 11.3 Verify the i18n key check passes and that no component holds a hardcoded user-facing string
- [x] 11.4 Verify the frontend renders each auth error from its code rather than from any server-supplied prose

## 12. Shared UI foundation (A12)

- [x] 12.1 Initialize shadcn/ui with a single `components.json` in `packages/shared`, aliases pointing at `packages/shared/src/ui`
- [x] 12.2 Add the primitives these four screens need (button, input, label, form field, card, table, dropdown or select, dialog, toast/alert) and export them from the package
- [x] 12.3 Declare the shared package as an explicit Tailwind source in each app's stylesheet so classes used only inside `packages/shared` survive a production build
- [x] 12.4 Build both apps and confirm a shared primitive's classes are present in the emitted CSS — the check that A12's failure mode is actually closed
- [x] 12.5 Verify both apps typecheck against the new exports and that no primitive was duplicated into an app-local `components/ui`

## 13. Frontend: authentication plumbing

- [x] 13.1 Add the session query against `/api/auth/session` in `packages/shared` as the single source of truth for both apps (A11)
- [x] 13.2 Add CSRF token handling to the shared API client so unsafe requests carry the required header automatically
- [x] 13.3 Map `401` in the shared client to a session-ended signal that invalidates the session query
- [x] 13.4 Add the route-guard primitive both apps use, redirecting an unauthenticated visitor to sign-in — including on a full page load of a deep link
- [x] 13.5 Verify a session revoked server-side lands the user at sign-in with a translated explanation on the next request, not a silent failure

## 14. Patient portal screens (P1, P7)

- [x] 14.1 Build P1 — landing and sign-in: value line, "Sign in with Google", language switch, WCAG 2.1 AA keyboard and contrast baseline
- [x] 14.2 Wire the Google sign-in start and the post-callback return into the authenticated portal
- [x] 14.3 Build P7 — profile and consents: the patient's own minimal data and consent status, with what they may edit editable
- [x] 14.4 Guard P7 behind authentication and confirm it renders only the signed-in patient's own data
- [x] 14.5 Verify both screens in pt-BR and en with no missing-key fallback

## 15. Staff app: shell and screens (S0, S11)

- [x] 15.1 Build the staff app-shell — sidebar plus top bar with clinic name, user, language switch, and sign out — as the frame every later staff screen mounts into
- [x] 15.2 Make navigation role-conditioned, and confirm hiding an entry is an affordance only: requesting the destination directly is still refused by the API
- [x] 15.3 Build S0 — staff sign-in: internal email/password, plus Google for professionals, in one screen that makes the two paths obvious
- [x] 15.4 Build S11 — staff users admin: list accounts, create a front-desk or administrator account, register a professional by email, disable an account, surfacing `auth.email_already_in_use` from its code
- [x] 15.5 Restrict S11 to administrators in the UI and verify the API refuses a front-desk caller regardless
- [x] 15.6 Verify both screens in pt-BR and en with no missing-key fallback

## 16. Test tiers and CI

- [x] 16.1 Confirm the unit tier covers the domain invariants and the ownership rule, and the integration tier covers every scenario in the spec
- [x] 16.2 Extend the compose-smoke script with an internal-account login through Caddy, asserting the session cookie survives the proxy and an authenticated request then succeeds
- [x] 16.3 Extend compose-smoke with the built-CSS assertion from task 12.4 so the shared-primitive styling risk is guarded in CI, not in a README
- [x] 16.4 Confirm CI needs no Google credentials — the federated path is covered entirely through the A4 seams
- [x] 16.5 Verify the pipeline fails as intended on a deliberately removed ownership check, then revert the break — proves the authorization tier actually guards this change's central risk
- [ ] 16.6 Confirm the full pipeline is green on the branch

## 17. Documentation

- [x] 17.1 Document the Google Console setup as a prerequisite: creating the OAuth client and registering the local redirect URI, noting plain HTTP is permitted for `localhost` and no tunnel is needed
- [x] 17.2 Update `.env.example` so every variable this change reads is listed, credentials marked as such
- [x] 17.3 Update `docs/00-context.md` if any convention or pin changed during implementation, so the substrate doc stays the source of truth
- [x] 17.4 Resolve the design's open questions in the artifacts once implementation settles them — session lifetime, consent version source, patient PII captured at sign-in, and whether S11 lists patients

## 18. Definition of Done

- [x] 18.1 A seeded administrator signs in with internal credentials, and is required to change the bootstrap password
- [x] 18.2 That administrator creates a front-desk user in S11; a duplicate email is refused with `auth.email_already_in_use`
- [x] 18.3 RBAC denies a forbidden action: the front-desk user attempting an administrator action gets `403 auth.forbidden` (tested)
- [x] 18.4 Ownership denies cross-patient access: patient A reading or updating patient B's profile gets `403 auth.ownership_denied` (tested)
- [x] 18.5 Google sign-in with an unknown email provisions a patient; with a pre-invited professional email it claims the professional user (both tested through the token seam)
- [x] 18.6 Session revocation is effective on the very next request (tested), and disabling an account revokes its sessions
- [ ] 18.7 P1, S0, P7, and S11 are functional through Caddy, each rendering in pt-BR and en with no missing-key fallback
- [x] 18.8 `GET /api/health` is still anonymous and still green through the proxy
- [x] 18.9 Unit and integration tests green in CI against real PostgreSQL, using the authenticated test client and the Google-token seam
- [x] 18.10 `openspec validate identity-session --strict` passes
