## Context

Change 1 left a stack that serves traffic and a `Domain` project that is deliberately empty. This change puts the first real content in the protected core and, more importantly, decides the mechanisms every later change will inherit without re-deciding: how a request becomes a principal, how a principal is authorized, and how a test says *as whom*.

Two constraints shape everything below. First, `00-context.md` §5 pins the session mechanism (Option C) and the shadcn output location, so those are inputs here, not choices — this document records *why* and the consequences that follow from them. Second, the hybrid identity model is asymmetric on purpose: internal accounts are created by an administrator and never federated; patients arrive through Google and are provisioned on the spot; professionals sit between the two, pre-created by an administrator and *claimed* through Google. Most of the interesting design is in that asymmetry, not in either login path taken alone.

Decisions are numbered `A1…A14` to stay distinguishable from change 1's `D1…D9`, the screen inventory's `P`/`S` identifiers, and the domain invariants' `I`.

## Goals / Non-Goals

**Goals:**

- One session abstraction that both login paths produce and no downstream code can distinguish, so authorization is written once.
- Revocation that is true by construction rather than by convention — the same standard change 5 will hold `EXCLUDE` to.
- Two-layer authorization (role + ownership) expressed as reusable primitives, proven by tests that show the refusals, not only the successes.
- Test seams that make the *federated* path testable offline and let any later test act as any role in one line.
- A role that is never inferred from an identity provider, and never mutated by signing in.
- The first real UI, with the shadcn primitive location and the staff app-shell settled once.

**Non-Goals:**

- Any clinical or scheduling configuration. A professional gets an identity here; their specialties, durations, and working hours are change 3.
- Any OAuth authorization for calendar access — no scope request, no refresh token, and therefore no token-encryption key in this change (A6).
- Session housekeeping as a background job. Expiry is enforced on read; the sweep waits for Hangfire (change 6).
- Self-service staff registration, password reset by email, or 2FA. Staff accounts are administrator-created; there is no mail sender until change 8, so a reset-by-email flow has no transport and would be scaffolding.
- Patient-facing account deletion workflows. LGPD scope is awareness, not data-subject-request tooling (`01-requirements.md` anti-scope).

## Decisions

### A1 — Session: opaque id in the cookie, the `Session` row as the only authority

The cookie carries a high-entropy opaque identifier and nothing else. Every authenticated request resolves the caller by a single indexed lookup on that identifier; the row carries `userId`, `expiresAt`, and a revocation marker. A custom `AuthenticationHandler` performs that lookup and materializes a `ClaimsPrincipal`; `[Authorize]`, policies, and role checks then come from the framework unchanged.

**Alternatives considered.** *Full ASP.NET Core Identity* brings a password store, lockout, and 2FA scaffolding — but also its own `IdentityUser` schema, which collides head-on with the `User` entity `02-domain-model.md` already specifies, and roughly ten tables to serve two internal roles. It fails the project's own bar ("what problem does it solve here?"). *Framework cookie authentication with claims* is the conventional answer and cheaper to write, but the cookie then holds a signed copy of the principal, so revocation becomes a validation hook that must hit the database anyway — the same per-request cost with a stale-copy failure mode bolted on. Choosing Option C means the cost is paid for something: there is no second copy of the truth to go stale.

**Trade-off.** One database round-trip per authenticated request. It is a primary-key lookup on a small hot table, which is not the hot path this project worries about (that is the availability query, change 4). **Revisit trigger:** a measured latency problem, or horizontal scaling — the same trigger already documented for Redis in `04-architecture.md` §7. Do not pre-optimize with a cache; that reintroduces exactly the staleness Option C exists to avoid.

Only the password hash lives outside this scheme: `PasswordHasher<User>` from `Microsoft.AspNetCore.Identity` is used as a standalone hasher, with no Identity store, because hand-rolling PBKDF2 iteration counts and a versioned hash format is the wrong kind of originality.

### A2 — Cookie flags, and why `SameSite=Strict` is wrong here

The session cookie is `HttpOnly`, `Secure` always, `SameSite=Lax`, `Path=/`, with no `Domain` attribute (host-only).

`Secure` unconditionally, because browsers treat `localhost` as a secure context and will send `Secure` cookies over plain HTTP there — so an environment-conditional flag would buy nothing and add a way to ship the insecure branch. This mirrors D9's "one Caddyfile, config difference only".

`Lax` rather than `Strict` is a decision, not a default. Under `Strict`, the short-lived cookie holding the OIDC `state` would not be returned on the cross-site navigation back from Google, breaking sign-in in a way that looks like a nonce bug. `Lax` sends cookies on top-level navigations, which is exactly the redirect-return case and nothing more. `SameSite=None` is never used.

**Consequence.** `Lax` alone is not CSRF protection for the API — see A3.

### A3 — Two request-forgery defences, named separately because they defend different things

They are routinely conflated, and conflating them here would leave a real hole.

```
  ┌── OIDC redirect flow ────────────────────────────────┐   ┌── cookie-authenticated API ──────┐
  │  state : ties the callback to the browser that       │   │  double-submit CSRF token:        │
  │          started the flow                            │   │  a non-HttpOnly cookie whose      │
  │  nonce : ties the ID token to that same request      │   │  value must be echoed in an       │
  │  both live in one short-lived HttpOnly cookie,        │   │  X-CSRF-Token header on every     │
  │  cleared on consumption -> a replay finds no cookie   │   │  unsafe method                    │
  └──────────────────────────────────────────────────────┘   └───────────────────────────────────┘
```

For the API, *double-submit* over ASP.NET's antiforgery: antiforgery is built around server-rendered views and needs a token-priming endpoint plus per-session server state to be used from an SPA, while double-submit is stateless and natural for a fetch client that already sets headers. A cross-site attacker can cause the browser to send the session cookie but cannot read the CSRF cookie to construct the matching header, which is the property that matters. `SameSite=Lax` remains the second layer; the required header is the first.

For the OIDC flow, `state` + `nonce` in a single short-lived cookie rather than a server-side auth-request table: the cookie is cleared when consumed, so a replay in the same browser finds nothing to match, and Google's authorization code is single-use on its side. **Trade-off:** two sign-in flows started concurrently in the same browser overwrite each other's cookie, and the older tab fails with `auth.google_failed`. Acceptable for a sign-in screen. **Revisit trigger:** if concurrent-tab sign-in ever matters, promote the cookie to an `AuthRequest` table keyed by `state`.

### A4 — Google is reached through two narrow seams, and `email_verified` is load-bearing

The callback does two things that touch the network: exchange the authorization code at Google's token endpoint, and validate the returned ID token. Both are substituted in tests:

- **`IGoogleIdTokenValidator`** — real implementation validates signature against Google's JWKS (with the framework's caching configuration manager) plus `iss`, `aud`, `exp`, and `nonce`. The test implementation validates against a locally generated RSA key, so integration tests exercise real validation logic against a token they minted, with no network.
- **The token exchange** goes through a named `HttpClient`, which tests replace with a stub handler returning a canned token response.

**Alternatives.** Using ASP.NET's built-in Google/OIDC handler would put the framework in charge of issuing the authentication cookie, which contradicts A1 and is awkward to test offline. Standing up a mock OIDC server (WireMock) would test more of the wire and cost a container plus fixture complexity in every test run; the seam gets the same coverage of *our* logic at a fraction of the weight. **Revisit trigger:** a real defect that only a full mock provider would have caught.

**`email_verified` must be `true`.** The invite-claim rule (A5) matches on email, so an unverified email would let anyone who can set an arbitrary `email` claim at some provider claim a prepared professional account. This single check is what makes email-based claiming safe, and it gets its own test.

### A5 — Provisioning: invite-first, and identity attributes that never change

```
   Google sign-in (sub, email, email_verified)
                    │
     ┌──────────────┴───────────────┐
     │  user with this sub?         │──── yes ──▶ sign in, touch nothing
     └──────────────┬───────────────┘
                    no
     ┌──────────────┴───────────────┐
     │  user with this email?       │
     └──┬────────────────────────┬──┘
        no                       yes
        │                        │
   create Patient          authProvider == Google (a prepared
   (JIT) + minimal PII      professional invitation)?
   + data-processing        ├── yes ──▶ CLAIM: bind sub to the
     consent               │            existing user, role kept
                            └── no  ──▶ REFUSE auth.google_failed
                                        (an internal staff account is
                                         never takeable via its email)
```

The refusal branch is the security point. Without it, controlling the mailbox of a front-desk account at Google would be enough to sign in as staff. With it, an internal account is reachable only through the password path.

Two invariants belong in `Domain` and get unit tests: **`role` is immutable after creation** and **`authProvider` is immutable after creation**. Role changes are therefore not a feature — an administrator disables one account and creates another, which keeps `AccessLog` history honest about who held what.

**Alternative rejected.** JIT-provision everyone as a patient and let an administrator promote later. It requires role mutation, and it leaves a `Patient` row behind for someone who was never a patient — colliding with soft-delete-only (I10) and muddying every ownership check downstream.

### A6 — Login-only Google consent, calendar scope by incremental authorization later

This change requests identity scopes only. The calendar scope is requested in change 6 with `include_granted_scopes=true`, at the moment the professional chooses to connect their calendar.

The reasoning is worth recording because it reinterprets the letter of an earlier doc (now updated — `01-requirements.md` §Hybrid identity model). Requesting calendar scope at sign-in would drag change 6's hardest work into change 2: `access_type=offline`, refresh-token storage, and a token-encryption key — a genuinely sensitive security surface, built with nothing consuming it and therefore nothing proving it works. It would also ask a professional for calendar access before they have seen why. Incremental authorization is Google's documented mechanism for exactly this, and the user-facing promise ("one seamless Google consent experience") survives; only the implementation splits across two changes. **Consequence to hold onto:** change 6 must use `include_granted_scopes=true`, or the professional's second consent silently drops the identity grant.

### A7 — What goes in `Domain`, and what stays in `Api`

The first real test of change 1's boundary.

| `Domain` (no infrastructure) | `Api` |
|---|---|
| `User`, `Role`, `UserStatus`, `AuthProvider`; the immutability invariants (A5); the ownership rule as a domain concept; `IPasswordHasher` as a port | EF configurations and the migration; `PasswordHasher<User>`; the `AuthenticationHandler`; cookie handling; the Google seams; rate limiting; the slices |
| `Patient`, `Consent` and their creation rules | `AccessLog` writing (A9) |

The `IPasswordHasher` port is a plain interface, so the boundary holds; `Api` supplies the ASP.NET-backed implementation. This is a small case, but it sets the pattern change 5 will lean on hard, so it is worth being deliberate about now rather than discovering the shape mid-`Appointment`.

### A8 — RBAC by policy, ownership by one guard built on the domain rule

Roles become named authorization policies applied at the endpoint. Ownership cannot be decided from the principal alone — it needs the thing being accessed — so it goes through a single reusable primitive rather than an endpoint attribute.

**Amended during implementation.** This decision first specified the framework's resource-based authorization (`IAuthorizationService.AuthorizeAsync(user, resource, OwnershipRequirement)`), which is the idiomatic ASP.NET answer. Writing it revealed that it answers allow/deny only, while this system needs a third fact out of the *same* evaluation: whether the access must be logged (A9). Getting that from a second, separate call is exactly the drift the `PatientDataAccessDecision` enum was shaped to prevent — an authorized path that quietly stops being an audited one.

So the implemented mechanism is a `PatientDataGuard` that evaluates the domain rule once and returns a three-valued decision (denied / allowed-as-owner / allowed-as-staff), writing the access record when the decision says to. The rule itself stays in `Domain`; the guard is the infrastructure around it.

**Why not ad-hoc checks in handlers:** that is precisely the duplication vertical slices are known to invite, and `04-architecture.md` §1 already names the mitigation (shared primitives, not repeated logic). One guard means change 5 protects appointments by reusing it rather than re-deriving it.

**The rule that makes it safe:** the owner is resolved *from the session*, never from a client-supplied identifier. The guard takes a principal and an already-loaded record, so there is no parameter through which a request could widen access — which is why the spec has an explicit scenario for a mismatched identifier.

### A9 — `AccessLog` is written explicitly at the slice, not by a filter

Only the slice knows *which* patient and *what* action; a global filter would have to infer both from route shape and would silently stop logging the moment a route changes. So the log call sits in the handlers that read patient data, and a patient reading their own data does not log (per spec).

**Trade-off, stated plainly:** this relies on discipline, and a future slice can forget. Mitigation: the table and helper land now so later changes have nothing to invent, and the staff-read path carries a test asserting the record exists. **Revisit trigger:** if a second or third slice forgets it, promote to a decorator around a narrow "read patient data" seam rather than a route-shape filter.

### A10 — Two independent brake mechanisms on the login path

A rate limiter partitioned by client address on the login endpoints (Decision R's native middleware, its first real use), *and* a per-account failed-attempt counter that locks the account. They defend different attacks: many accounts from one source versus one account from many sources.

Two details that are easy to get wrong and are therefore specified: the rate limiter's rejection must be written through the error envelope as `429 auth.rate_limited` rather than the middleware's default empty response; and a locked account must answer `403 auth.account_disabled` while a wrong password answers `401 auth.invalid_credentials` — the account-existence question is already answered for someone who knows the correct password, so this pair leaks nothing that the correct password did not.

Scope: login endpoints only. The public availability search gets its own limiter in change 4, where its shape can be reasoned about against real query cost.

### A11 — The frontend has no second copy of the session

An `/api/auth/session` endpoint is the single source of truth, read through TanStack Query. Route guards read that query; sign-out and any `401` invalidate it. The shared API client maps `401` to a global "session ended" signal so an expired session lands the user at sign-in with a translated message instead of a silent failure.

**Alternative rejected:** storing the user in a React context populated at login. It is the obvious approach and it goes stale exactly when Option C is designed to be correct — an administrator disables an account, the API refuses, and the UI keeps rendering an authenticated shell. Making the server the authority on both sides keeps one story.

The staff app-shell (sidebar + top bar, role-conditioned navigation) is built here because S0 needs somewhere to land, and every staff screen from change 3 onward mounts into it. Hidden navigation is a UX affordance and never a security boundary — the API refuses the request regardless, and there is a spec scenario for it.

### A12 — shadcn primitives in `packages/shared`, with Tailwind 4's content detection handled explicitly

One `components.json` in `packages/shared` with aliases pointing at `packages/shared/src/ui`; both apps consume `@clinic/shared`. This is `00-context.md` §2, and the reason it is pinned is that the CLI's default (app-local `components/ui`) would produce two divergent copies of every primitive by change 5.

**The concrete risk** is not the components but the CSS: Tailwind 4 detects classes by scanning source, and classes that exist only inside `packages/shared` are outside each app's own tree. Left alone, the primitive renders unstyled in a production build while looking fine in dev. Mitigation: each app's stylesheet declares the shared package as an explicit source, and change 1's compose-smoke tier is extended with an assertion that a shared primitive's class survives the built CSS. This is the same category of failure as change 1's base-path problem — right in dev, wrong when built — so it gets the same treatment: a check that runs in CI rather than a note in a README.

### A13 — Test seams: mint sessions for convenience, but test login for real

Two distinct facilities, because conflating them makes every future test depend on the login endpoint:

1. **`AsRole(...)` / `AsUser(...)`** on the integration fixture — seeds a user and a session row directly and returns an `HttpClient` carrying the cookie. This is what change 3 onward uses; it must stay one line at the call site.
2. **Full-flow tests** for the login paths themselves — internal credentials end to end, and the Google callback through the A4 seams — because (1) deliberately bypasses them.

**The trade-off is real:** minting a session skips the code that issues sessions, so a bug in issuance is invisible to every test that uses (1). That is exactly why (2) exists and why it is not optional. Also worth stating: the fixture builds on change 1's Testcontainers + Respawn harness unchanged; Respawn's reset must not truncate away the bootstrapped administrator, so the fixture seeds identity data per test rather than relying on startup bootstrap.

Tier assignment: `Domain` unit tests cover the immutability invariants and the ownership rule; integration tests cover everything in the spec; the compose-smoke tier gains one internal-account login through Caddy — proving the cookie survives the proxy, which `WebApplicationFactory` structurally cannot show (change 1's D6 reasoning, unchanged).

### A14 — The Google path is optional configuration, and CI never needs credentials

Google client credentials are an out-of-repo, manually-created prerequisite. If they are absent, the API starts normally, the internal login path works, and the Google endpoints answer a clear configuration error rather than a stack trace. Consequences: a contributor can run and test everything except the live federated path with no Google project; CI needs no secrets, because A4's seams cover the federated path; and the manual Google Console setup (authorized redirect URI `http://localhost:8080/api/auth/google/callback` locally — Google permits plain HTTP for `localhost`, so no tunnel, which is change 7's problem) is documented as a setup step rather than assumed.

This deliberately differs from the connection string, which fails fast at startup (change 1): the database is required for the app to be meaningful, whereas the Google path is one of two login mechanisms and its absence should degrade, not stop.

## Risks / Trade-offs

- **This change is the largest so far** and strains the "reviewable in one sitting" rule (`05-openspec-workflow.md`, W). → Task order is deliberately layered so the internal-account half is complete and demonstrable before the Google half begins; if review fatigue hits, that boundary is a clean stopping line that could be split into a follow-up change without rework. Not splitting it up front keeps the eight-change build order intact and keeps "hybrid identity" reviewable as one story, which is the portfolio argument.
- **A database lookup per authenticated request** (A1). → Indexed primary-key lookup on a small table; revisit only on measurement, and not with a cache (see A1).
- **Session rows accumulate**, since expiry is enforced on read and nothing deletes them. → Bounded by session lifetime and traffic, which for this project is negligible; the sweep is a recorded revisit trigger for change 6, when Hangfire arrives and the job costs nothing to add.
- **`AccessLog` depends on discipline** (A9). → Test now, promote to a decorator if it is forgotten twice.
- **Shared-package Tailwind classes can vanish in a production build** (A12). → CI assertion, not documentation.
- **Session minting hides issuance bugs from most tests** (A13). → Full-flow login tests are mandatory, not optional.
- **`Secure` cookies require a secure context**, so serving the app over plain HTTP on a non-`localhost` hostname would silently drop the session cookie. → Local development is `localhost`; production is HTTPS via Caddy (D9). The failure mode is documented here because the symptom ("login succeeds but I'm immediately logged out") points nowhere near the cause.
- **Bootstrap credentials are real credentials.** An operator who ignores the forced change ships a known-password administrator. → Forced change on first sign-in is the primary control; the warning is the backstop, and it names the account so it appears in logs rather than only on a screen someone dismissed.

## Migration Plan

Greenfield — no data exists, so there is nothing to migrate, only to order correctly.

1. One EF migration adding `User`, `Patient`, `Consent`, `AccessLog`, `Session`, with a unique index on email (scoped to non-deleted rows, per soft-delete-only), a unique index on the provider subject identifier, and an index supporting session lookup.
2. Administrator bootstrap runs as a startup step **after** the migration step change 1 established, and is idempotent — it must be safe on every boot, not only the first.
3. Rollback is the down migration plus reverting the API; no external system holds state this change created, and no secrets are provisioned by it beyond configuration.
4. Deployment gains required configuration: the seeded-administrator variables, and the Google client credentials as optional (A14). `.env.example` is updated in the same change, per the convention change 1 set.

## Open Questions

All four are resolved; the reasoning is kept because each is a trade-off a later change may
want to revisit rather than rediscover.

- **Session lifetime and sliding expiry** — **resolved:** a fixed absolute lifetime, default
  8 hours, configurable as `Auth__SessionLifetime`. No sliding renewal, because renewing would
  mean writing to the session row on nearly every request — a real cost to take on only once
  somebody is actually annoyed by being signed out, and cheaper to add later than to remove.
- **Consent version source** — **resolved:** configuration (`Auth__ConsentVersion`). No screen
  edits consent text at runtime, so a table would be storage for a value that only changes with
  a deploy. Revisit if consent text ever becomes editable by an administrator.
- **Patient personal data captured at sign-in** — **resolved:** name and email from the
  provider; `contactPhone` starts empty and P7 offers it as optional. Collecting a phone number
  before any appointment exists is data gathered ahead of its purpose; booking (change 5) is
  where it acquires one.
- **Does S11 list patients?** — **resolved:** no. It manages staff accounts and professional
  invitations, and the listing filters patients out. A patient-search surface belongs with
  front-desk booking (change 5), where it has a purpose and an `AccessLog` reason.

## Discovered During Implementation

Recorded because each was a decision, not a detail, and the reasoning is worth keeping:

- **Session tokens are stored hashed.** The row holds SHA-256 of the token the cookie carries,
  and a lookup hashes what was presented. A1 is unchanged — the row is still the only authority
  and revocation is still immediate — but a leaked dump or backup now yields no usable sessions.
  Costs one hash per request.
- **The rate limiter's threshold moved into configuration** (`Auth__LoginAttemptsPerMinute`).
  Discovered by the tests: with the limit hard-coded, every test in the suite shared one window
  and unrelated tests started failing with `429`. The fix is also what A10's own reasoning
  asked for — the rule is domain, the number is operational.
- **Google failures report through a redirect, not a JSON body.** The callback is a top-level
  navigation, so a `401` with an envelope would put raw JSON in the address bar. At least one
  refusal here is an ordinary mistake rather than an attack — a staff member with an internal
  account clicking "Sign in with Google" — and it deserves a translated sentence. The
  destination always comes from the state cookie, never from the request, so this cannot become
  an open redirect. The spec scenarios were amended to match.
- **A `PatientDataGuard` replaced framework resource-based authorization** (see A8) so one
  evaluation of the domain rule drives both the decision and the audit record.
- **Three error codes were added to the catalogue** before use, per the project rule:
  `auth.password_change_required` (403), `auth.account_not_found` (404),
  `auth.google_unavailable` (503), plus `patient.not_found` (404) for the staff read path.
- **`ApiError.WriteAsync` clears the response**, which silently dropped the `Retry-After`
  header the limiter had just set. Headers that must survive an error envelope are now set
  through `OnStarting`, the same mechanism the correlation-id middleware uses. Found by a test
  asserting the header, which is the argument for asserting on it.
