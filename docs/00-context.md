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
- **Time (Decision H):** store UTC (`tstzrange`); one configured clinic timezone for display.
- **Soft-delete only** everywhere (I10); never hard-delete.
- **Session (Decision J):** OIDC + internal accounts both resolve to the app's own session (HttpOnly cookie + revocation).
- **Authorization:** RBAC by role + ownership check on patient data.
- **Secrets** outside the repo (env / Docker secrets); `.env.example` committed.
- **Logging:** Serilog structured logs, correlation id per request and per job/webhook. The header is **`X-Correlation-ID`** — read from the inbound request when present, generated when absent, always echoed in the response. Chosen over `traceparent` because W3C trace context buys distributed-tracing interoperability this project has no consumer for (single deployable, no tracing backend — `03-nfr.md` §4 deliberately keeps observability proportional). Revisit only if a real tracing stack is introduced.

## 6. Test & CI substrate (established in change 1, enforced thereafter)

- **Unit tests:** domain-core invariants.
- **Integration tests:** against a **real PostgreSQL** via **Testcontainers**; **Respawn** resets state between tests. Covers the `EXCLUDE` constraint, the professional-scoped lock (G1), and Dapper queries.
- **CI (GitHub Actions):** build → unit → integration (Testcontainers) → `openspec validate --strict` → i18n-key presence check. CI enforces the Definition of Done instead of trusting it.

## 7. Definition of Done (every change)

Tests (unit + integration) green in CI; i18n keys present (pt-BR + en) for new user-facing strings; the demonstrable behavior works end to end; `openspec validate --strict` passes; the change is archived into the living spec.
