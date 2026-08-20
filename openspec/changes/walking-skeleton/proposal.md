## Why

Every subsequent change in the build order (`05-openspec-workflow.md` §3) assumes a working substrate: a Compose stack, a two-project .NET solution, two independently-built SPAs served at different base paths, and a test harness that runs against a real PostgreSQL. None of that exists yet, and all of it can fail in ways that have nothing to do with the clinic domain.

The riskiest part is not the API — it is **Caddy serving two separate SPA builds at `/` and `/staff` from one origin**. Base-path handling (router basename, asset URLs, SPA fallback per prefix) is the classic source of "works in dev, 404s on refresh in prod", and it is load-bearing for the entire frontend plan (Decision Z1). Discovering it during `identity-session` — while also debugging OIDC redirect URIs — would conflate two hard problems. This change isolates it and proves it with nothing else in the way.

## What Changes

Structural only. **No domain logic, no authentication, no real screens** — those belong to changes 2+.

- **Compose stack** (`infra/`): `caddy`, `api`, `db`. Internal network; only `caddy` publishes ports. `db` reachable solely by `api`.
- **Caddy routing**: patient-portal build at `/`, staff build at `/staff`, `/api/*` proxied to `api`. Per-prefix SPA fallback so a deep-link refresh resolves to the right app.
- **Base-path strategy** for the two builds — Vite `base` + router `basename` per app, wired so the same source works under both dev server and Caddy-served production build. *The reason this change exists.*
- **`GET /api/health`** — one end-to-end vertical slice through the real stack, reporting DB connectivity. Modeled as an ASP.NET health check, exposed through the slice structure that later features will follow.
- **Both SPAs render one health page** that calls `/api/health` and displays the result. This is the proof that both base paths serve their own build and reach the API through Caddy — not a feature, a probe.
- **EF Core initial migration** creating a minimal marker table, applied against the containerized PostgreSQL. Proves the write and migration path before any real schema.
- **Test harness**: Testcontainers (real PostgreSQL) + Respawn, plus one integration test asserting `/api/health` reports healthy. This harness is the substrate every later change's integration tests plug into.
- **CI** (GitHub Actions): build → unit → integration → `openspec validate --strict` → i18n-key presence check. CI enforces the Definition of Done from change 1 forward rather than trusting it.
- **Serilog** with a correlation id per request; **one i18n string** rendered in pt-BR and en in *both* apps (proves the react-i18next wiring and gives the CI i18n check something real to assert); **`.env.example`** committed.

**Local TLS:** Caddy cannot obtain Let's Encrypt certificates for `localhost`. Local runtime is plain HTTP (or Caddy's internal CA); production TLS is an environment/profile difference, deliberately out of this change's local runtime. Nothing here weakens the production posture from `04-architecture.md` §9 — it defers exercising it.

## Capabilities

### New Capabilities

- `platform-health`: the deployed system reports its own operational readiness — an HTTP health endpoint covering database connectivity, reachable through the public reverse proxy, and observable via correlated structured logs. Deliberately a small spec; the substantive content of this change is structural and lives in `design.md`.

### Modified Capabilities

None — this is the first change; `openspec/specs/` is empty.

## Impact

**Created:** repository skeleton per `00-context.md` §2 — `apps/api` (`Api` + `Domain` projects), `apps/patient-portal`, `apps/staff`, `packages/shared`, `infra/`, pnpm workspace root, `.github/workflows/`, `.env.example`.

**Runtime dependencies introduced:** PostgreSQL (pinned), Caddy, Docker Compose, Serilog, EF Core, Testcontainers, Respawn, Vite, React, react-i18next, TanStack Query.

**Not touched:** no clinic domain entities, no `Appointment` aggregate, no `EXCLUDE` constraints, no advisory locking (G1), no OAuth/OIDC, no Hangfire, no outbox. The `Domain` project is created with its no-infrastructure reference discipline in place but stays essentially empty.

**Downstream:** every later change inherits this layout, the Caddy base-path contract, the Testcontainers+Respawn harness, and the CI gate. Getting the base-path strategy wrong here is expensive to unwind later, which is precisely the argument for isolating it now.
