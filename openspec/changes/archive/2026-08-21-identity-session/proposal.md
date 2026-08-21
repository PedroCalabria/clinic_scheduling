## Why

Change 1 proved the substrate carries traffic; nothing yet proves *who* is sending it. Every change from 3 onward is defined in terms of an authenticated actor — the admin who configures the clinic, the patient who may only see their own appointments, the professional whose calendar is being synced — so identity is the gate all of them authenticate through. It is second in the build order (`05-openspec-workflow.md` §3) because it is the narrowest thing that unblocks the widest set of downstream work.

The riskiest part is not the login form. It is the **seams**: an authenticated test client and a substitutable Google-token validator. Without them, every later change either writes its integration tests against an unauthenticated API (proving nothing about authorization) or invents its own auth fixture under deadline pressure. Change 1's argument was "isolate the base-path problem before it compounds"; the same argument applies here to the test substrate. Those seams are the load-bearing deliverable of this change — the four screens are its demonstration.

## What Changes

- **Identity entities**, per `02-domain-model.md` §Identity: `User`, `Patient` (1:1, minimal PII), `Consent`, `AccessLog`, plus a `Session` table that the session mechanism owns. Soft-delete throughout (I10).
- **Session mechanism** — Option C as pinned in `00-context.md` §5: an opaque session id in the cookie, the `Session` row as the single source of truth, revocation effective on the very next request. A custom `AuthenticationHandler` performs the credential lookup only; `[Authorize]`, policies, and role checks come from the framework.
- **Two login paths converging on one session**: internal accounts (email/password, `PasswordHasher<T>` without the Identity store) and Google OIDC (server-side authorization-code flow, `state` + `nonce`, ID-token validation via JWKS).
- **Provisioning rules**, which are the interesting part of hybrid identity: an unknown Google email is provisioned just-in-time as a `Patient`; an email pre-created by an admin as `role = Professional` is **claimed** by that first matching Google sign-in. A role is therefore never guessed from the identity provider, and never mutated after the fact.
- **First-administrator bootstrap** — idempotent, from environment configuration, because `S11` cannot be the only way to create an administrator when no administrator exists yet. New `.env.example` entries.
- **Two-layer authorization made real**: RBAC by role, plus an ownership primitive enforced with a test that a patient reading another patient's profile is refused. `AccessLog` records staff access to patient data; a patient reading their own data is not logged.
- **Brute-force floor on the login endpoints**: the native .NET rate limiter (Decision R) plus account lockout — the login endpoint is the only publicly reachable write in this change.
- **CSRF, as two distinct mechanisms** that are routinely conflated: `state`/`nonce` protecting the OIDC redirect flow, and a separate defence protecting the cookie-authenticated JSON API.
- **New error codes** — the six auth codes added to `07-error-codes.md` §3 become real, each with matching pt-BR/en keys.
- **First real UI**: shadcn/ui initialized to emit primitives into `packages/shared` (`00-context.md` §2), consumed by both apps, plus screens **P1**, **S0**, **P7**, **S11** per `06-ui-surfaces.md` §4 — including the staff app-shell with role-conditioned navigation that every later staff screen mounts into.
- **Test seams**: an authenticated integration-test client that can act as any role, and `IGoogleIdTokenValidator` with a JWKS-backed implementation and a locally-signing test implementation, so the federated path is covered without network access.

**Professional Google consent is login-only here.** The calendar scope is requested later via incremental authorization when the professional connects their calendar (`01-requirements.md` §Hybrid identity model, and change 6). Consequence: this change stores no OAuth refresh token and therefore needs no token-encryption key — that security surface arrives with the capability that uses it.

## Capabilities

### New Capabilities

- `identity-session`: how a caller becomes an authenticated principal and what that principal may do — the two login paths converging on one app-owned session, session lifetime and revocation, how each role's `User` comes into existence, RBAC, ownership-based authorization on patient data, access logging, and consent capture.

### Modified Capabilities

None. `platform-health`'s requirements are unchanged — but one of them becomes a constraint this change must actively defend: `GET /api/health` MUST remain anonymous, which introducing authentication is exactly the sort of thing that silently breaks. It stays covered by the existing integration test and the compose-smoke tier.

## Impact

**Created:** an `Auth` (or `Identity`) slice under `Api/Features` following the pattern the health slice established, the first real `Domain` types (`User`, role, status, and the ownership rule — the project's first non-empty protected core), an EF migration for five tables, auth context + route guards + the staff app-shell in both frontends, and `packages/shared/src/ui` primitives from shadcn.

**Modified:** `packages/shared`'s API client gains 401 handling (a session that expired mid-session must land the user at sign-in, not at a silent failure); `.env.example` gains the Google client credentials and the seeded-admin variables; `docs/07-error-codes.md` is already updated and is now consumed.

**Dependencies introduced:** the ASP.NET authentication/authorization stack, `Microsoft.AspNetCore.Identity` for `PasswordHasher<T>` only, JWT/JWKS validation, the native rate limiter, and shadcn/ui + Radix on the frontend.

**Not touched:** no specialties, resource types, appointment types, professional durations, or working-hour templates (change 3 — this change gives a professional an *identity*, not a schedule); no availability solver; no `Appointment`, no `EXCLUDE` constraint, no professional-scoped lock (G1); no Google Calendar scope, OAuth refresh token, token encryption, outbox, or webhook; no Hangfire and therefore no session-sweep job (expiry-on-read, with the sweep a documented revisit trigger for change 6); no reminders; no `WorkingHoursException`. `S11` manages staff accounts and professional invitations only — patient records are created by the sign-in flow, not by an admin screen.

**Downstream:** every later change inherits the session cookie contract, the `[Authorize]`/policy conventions, the ownership primitive, `AccessLog`, the staff app-shell, the shadcn primitive location, and — most consequentially — the authenticated test client. Getting the test seams wrong here means change 5 tests booking without being able to say *as whom*, which is precisely the authorization story the project exists to demonstrate.
