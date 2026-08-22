# Project Context — Clinic Scheduling System

> **Purpose:** durable, cross-cutting context that every OpenSpec change inherits.
> Feed this into `openspec/config.yaml` `context:` (reference the file or paste the content), so no `/opsx:propose` has to restate the stack, layout, or conventions.
> **Source of truth:** docs `01`–`07`. This file is the *concrete* layer beneath them (the decision docs decide; this file pins).
> **Document language:** English.

---

## 1. Stack (pin explicitly for reproducible integration tests)

| Concern | Choice | Note |
|---|---|---|
| Backend runtime | **.NET 10 (LTS)** | pinned in `global.json` (SDK `10.0.400`, roll-forward `latestFeature`) |
| Database | **PostgreSQL 17** | image `postgres:17-alpine` — the **same tag in Compose and Testcontainers**, which is the point of pinning. `tstzrange` + `btree_gist` work on any modern version |
| Frontend | **React 19 + TypeScript + Vite** | |
| UI | **shadcn/ui + Tailwind** | Radix primitives → WCAG 2.1 AA baseline |
| Server state | **TanStack Query** | availability is volatile server state |
| i18n | **react-i18next** (pt-BR + en) | frontend owns translation; API returns codes + params |
| Node | **Node 22 (LTS)** | pinned via root `package.json` `engines` + CI `setup-node`; **pnpm 9** via `packageManager` |

> Pins were fixed in change 1 (`walking-skeleton`). Changing any of them is a deliberate act: update this table, `global.json`, the Compose/Testcontainers tags, and CI together, or integration tests stop being reproducible.

## 2. Monorepo layout

Workspace monorepo using **pnpm workspaces** (lightweight; Turborepo is overkill for two apps + one package).

```
/
├── apps/
│   ├── api/               .NET solution (see §3)
│   ├── patient-portal/    React SPA, public, router basename "/"
│   └── staff/             React SPA, internal, router basename "/staff"
├── packages/
│   └── shared/            API client, generated types, i18n resources, UI primitives (both apps consume)
├── infra/                 Caddyfile, docker-compose
├── docs/                  01–07 planning docs
└── openspec/              config.yaml, changes/, specs/
```

Only the JS side (`apps/patient-portal`, `apps/staff`, `packages/shared`) is in the pnpm workspace; `apps/api` is the .NET solution.

**shadcn/ui output:** configured to emit shared primitives into `packages/shared` (aliases point there), consumed by both apps — **not** the CLI's default app-local `components/ui`. Deciding this once avoids two divergent copies of every primitive. Established in change 2 (first real UI): `packages/shared/components.json` holds the configuration, `src/ui/cn.ts` the class-merge helper, and `src/ui/theme.css` the Consult Rio tokens as Tailwind `@theme` variables. Each app's stylesheet declares the shared package as a Tailwind `@source` — without it, classes used only inside `packages/shared` are stripped from the production build while looking fine in dev, so the compose-smoke tier asserts a shared primitive's classes survive.

**Primitives so far:** button, input, label, field, select, card, table, badge, alert — what the change-2 screens need — plus **dialog**, added by `clinic-catalog` when S8-S10 needed a modal form. Widgets the platform does not provide accessibly (dialog, combobox, popover) come from the shadcn CLI with their Radix dependencies when a screen requires one; wrapping a native `<input>` or `<select>` in Radix would be ceremony, so those are native and styled. The dialog is the first of those to be genuinely required — focus trapping, focus restoration to the trigger, Escape-to-close, and inert background content are a list this project would get subtly wrong by hand — and it is the only element in the system carrying a shadow, because it is the only one genuinely floating.

## 3. Backend structure (Decision K + P-3 made concrete)

Solution file: `apps/api/ClinicScheduling.slnx` (.NET 10 emits the newer XML solution format). Layout: `src/Api`, `src/Domain`, `tests/Domain.UnitTests`, `tests/Api.IntegrationTests`. Shared build settings live in `apps/api/Directory.Build.props` — warnings are errors.

- **Two projects:** `Api` + `Domain`.
  - `Domain` references **no** infrastructure (no EF, no Dapper, no ASP.NET). The **protected core** — the `Appointment` aggregate, invariants I1–I10, the state machine, the availability-solver contracts — lives here. The *compiler* enforces the boundary, two ways: the `ForbidInfrastructureReferences` MSBuild target in `Domain.csproj` fails the build on a forbidden `PackageReference`, and `DomainBoundaryTests` asserts the compiled assembly's references (catching infrastructure arriving transitively).
  - `Api` depends on `Domain` (never the reverse). Infrastructure (EF Core, Dapper, adapters) lives as folders within `Api`; the boundary that matters is the domain one.
- **Vertical Slice** organization inside `Api` (feature folders: Booking, Availability, CalendarSync, Reconciliation, AdminConfig). Endpoints call handlers directly — **no MediatR**.
- **Persistence (Decision L):** EF Core for writes (through the aggregate); Dapper for the availability read (hot path, raw SQL over `tstzrange`/GiST). CQRS-lite.
- **Schedule-mutation concurrency (G1):** the booking path and the internal-block-creation path both take a **professional-scoped transaction lock** (transaction-scoped advisory lock keyed on `professional_id`) so the cross-table appointment↔block check is race-safe.

## 4. Frontend structure (Decision M + Z1)

Two SPAs (patient-portal, staff) + `packages/shared`. Feature-folder structure mirroring backend slices. Each app has its own build and router `basename`. Staff app is a role-conditioned app-shell; patient portal is the design showcase.

## 5. Cross-cutting conventions (inherited by every change)

- **Error contract (Decision I):** the API returns `{ code, params? }`; the frontend translates. Codes come from the catalogue in `07-error-codes.md` — **reuse existing codes; add new ones there, never invent per-slice shapes.**
- **Time (Decision H):** store UTC (`tstzrange`); one configured **clinic timezone** for display and for converting wall-clock working hours to UTC instants. Config: `Clinic__Timezone` (IANA id, e.g. `America/Sao_Paulo`) in `.env.example`. Conversion uses **NodaTime** (explicit handling of DST-ambiguous/nonexistent local times) rather than fixed offsets — Brazil has no DST today, so an offset bug would be invisible in tests and only surface if the law changes or the clinic moves. `professional-configuration` (3b) introduces `Clinic__Timezone` + NodaTime and **stores** wall-clock working hours; the actual wall-clock→UTC **conversion** first runs in `availability-read` (change 4), where the solver turns a professional's wall-clock hours into UTC instants for specific dates — 3b has no appointments to convert against yet.
- **Dev seed:** an **idempotent** dev-only seed (same spirit as the env-seeded admin) provisions a full runnable clinic — specialties, resource types, resources, appointment types, and a professional with specialties, durations, and working hours — so every change from `availability-read` on has something to demonstrate against without ~30 minutes of form-filling. Completed in `professional-configuration` (3b); never runs in production.
- **Soft-delete only** everywhere (I10); never hard-delete.
- **Session (Decision J):** OIDC + internal accounts both resolve to the app's own session. Mechanism (**Option C**): the cookie holds an **opaque session id**; a `Session` table is the single source of truth. Revoke = flag the row, effective on the very next request (revocation *by construction*, not a stale claims copy). A custom `AuthenticationHandler` does only the credential lookup — `[Authorize]`, policies, and RBAC still come from the framework. Password hashing borrows `PasswordHasher<T>` without the Identity store. Session-row cleanup is **expiry-on-read** for now; a periodic sweep is a documented revisit trigger for when Hangfire lands (change 6). Cookies are **`Secure` always** (localhost is a secure context — no env-conditional flags).
- **Authorization:** RBAC by role + ownership check on patient data. Roles are named
  policies applied at the endpoint; ownership goes through one guard over a single domain
  rule, which also decides whether the access is recorded (`AccessLog`). A role is fixed when
  a user is created and never changes — an administrator disables one account and creates
  another. Established in change 2.
- **Provisioning (change 2):** an unknown Google email becomes a **patient**; a professional
  is **pre-created by an administrator** (S11) and claimed by their first Google sign-in; a
  Google sign-in whose address belongs to an internal account is **refused**. Roles are never
  inferred from the identity provider. The `User` (identity) and the `Professional` row (clinical
  configuration) are created in **separate steps** (Fork 1 · option iii): an administrator invites
  the professional in S11 (change 2 — creates the `User`, `Role=Professional`, `Status=PendingClaim`);
  the `Professional` row is created later in S7 (change 3b) on first configuration — S7 lists
  `User where Role=Professional` and the row is born on first save. identity-session owns the
  professional's identity; clinic-configuration owns their clinical setup.
- **Secrets** outside the repo (env / Docker secrets); `.env.example` committed.
- **Logging:** Serilog structured logs, correlation id per request and per job/webhook. The header is **`X-Correlation-ID`** — read from the inbound request when present, generated when absent, always echoed in the response. Chosen over `traceparent` because W3C trace context buys distributed-tracing interoperability this project has no consumer for (single deployable, no tracing backend — `03-nfr.md` §4 deliberately keeps observability proportional). Revisit only if a real tracing stack is introduced.

## 6. Test & CI substrate (established in change 1, enforced thereafter)

- **Unit tests:** domain-core invariants.
- **Integration tests:** against a **real PostgreSQL** via **Testcontainers**; **Respawn** resets state between tests. Covers the `EXCLUDE` constraint, the professional-scoped lock (G1), and Dapper queries.
- **Acting as a user (change 2):** `ApiFixture.AsRoleAsync(role)` seeds a user and a session and returns a client already holding the cookie — one line, and the seam every later change's tests use. It deliberately skips session issuance, so the login flows carry their own end-to-end tests.
- **Google, offline (change 2):** the token exchange is stubbed and the signing keys are replaced; the validation itself (signature, `iss`, `aud`, `exp`, `nonce`, `email_verified`) runs for real against a locally minted token. CI therefore needs no Google credentials.
- **A third tier:** `pnpm smoke` asserts against the running Compose stack what the in-process host cannot — Caddy's routing, the base paths, the session cookie surviving the proxy, and the built CSS.
- **CI (GitHub Actions):** `openspec validate --strict` → i18n-key presence check → README link check → build (both ecosystems) → unit → integration (Testcontainers) → compose smoke. The cheap, hermetic gates run first so a broken spec, a missing translation, or a README pointing at a renamed file fails in seconds rather than after two Docker tiers. `.github/workflows/ci.yml` is the source of truth for the order.

## 7. Definition of Done (every change)

Tests (unit + integration) green in CI; i18n keys present (pt-BR + en) for new user-facing strings; the demonstrable behavior works end to end; `openspec validate --strict` passes; a **validation guide is produced and its checks are actually run** (see §9); **`README.md`'s status table reflects what now works** (a 1–3 line edit, made when the change reaches `main` — see §8); the change is archived into the living spec.

## 8. The README is the outward-facing surface

`README.md` explains the project to recruiters and potential clients who may not be technical. It is written once and kept current cheaply, because only three parts of it move:

- the **status table** — one cell per change;
- the **"N of 9 increments shipped"** line — only when N changes;
- the **local-run section** — only when a change adds a prerequisite or an environment variable.

Everything else derives from these docs, so it moves only when a documented decision moves. The status cell flips when the change reaches `main`, not when it is applied — otherwise `main`'s README claims a capability whose code is not on `main`. Rewriting the README per change is not the intent and is explicitly out of scope for a change's task list.

## 9. The validation guide (human verification)

Automated tests cover domain invariants, DB constraints, and API behavior — but **not** browser-level UX, both-locale rendering, visual/interaction correctness (e.g. a dialog's focus trap), or the Compose stack as a user experiences it. Every change therefore ends by producing a **validation guide** at `openspec/changes/<id>/validation.md`: a numbered list of the manual checks a human must run against a locally-running app.

- **Each item names:** the role to act as, the screen/route, the action, the expected result — and, where user-facing, that it is checked in **both pt-BR and en**.
- **When:** produced during `apply`/`verify`; the maintainer **runs the checks against the local app and confirms them before archive/merge**. The change is not done until the guide has been executed.
- **Why:** this replaces the anti-pattern of archiving with human-verification task boxes left unchecked (as happened in `identity-session` and `clinic-catalog`). The guide collects exactly the human-only surface in one place, so it stops being buried, unchecked boxes.
- **Scope:** only what tests cannot assert. If a check *can* be automated, it belongs in the test suite, not the guide.