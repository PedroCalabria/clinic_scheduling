## Context

The repository currently contains planning docs and a design system, and no code. This change creates the substrate that changes 2–8 build on: the Compose stack, the `Api` + `Domain` solution, two independently-built SPAs plus `packages/shared`, and the Testcontainers+Respawn test harness — verified end to end by a single health endpoint.

The binding constraint is that **two SPAs share one origin** (Decision Z1): patient-portal at `/`, staff at `/staff`. Serving two independent builds from one host under different path prefixes is the piece most likely to break, and it breaks in a delayed way — typically on a hard refresh of a deep link, or on an asset request that resolves against the wrong prefix. Everything else here (a health check, one migration, a CI pipeline) is routine. The design therefore spends its attention almost entirely on the routing and base-path contract.

Inherited and not re-litigated here: stack pins, monorepo layout, `Api`+`Domain` split, pnpm workspaces, error contract, conventions, and the Definition of Done — all from `00-context.md`.

## Goals / Non-Goals

**Goals:**

- `docker compose up` yields a working stack: Caddy public, API and DB internal only.
- Prove the base-path contract for both surfaces, including deep-link reload and asset resolution.
- Prove the write path: an EF Core migration applies cleanly to the containerized PostgreSQL.
- Establish the integration-test harness (real PostgreSQL) that every later change plugs into.
- Establish CI as the enforcer of the Definition of Done from change 1 onward.
- Prove the cross-cutting wiring that later changes assume: correlation ids, i18n in both apps, `packages/shared` actually consumed by both apps.

**Non-Goals:**

- Any clinic domain concept. No entities, no `EXCLUDE` constraints, no advisory locks (G1), no state machine.
- Authentication or sessions. `/api/health` is anonymous; auth is change 2.
- Real screens. Each app renders one health page and nothing else — the design system is not applied yet.
- Production TLS at local runtime, and production deployment itself.
- Hangfire, the outbox, Dapper, and rate limiting — introduced by the changes that need them.

## Decisions

### D1 — Path-based routing with two independent builds, each pinned to its own base

Two builds, each configured for the prefix it will be served from:

| App | Vite `base` | Router `basename` | Served at |
|---|---|---|---|
| patient-portal | `/` | `/` | `/` |
| staff | `/staff/` | `/staff` | `/staff` |

Both values derive from a single per-app constant so they cannot drift apart — the most common failure in this setup is a build whose `base` and `basename` disagree, which yields an app that mounts but cannot navigate, or navigates but cannot load assets.

Setting Vite `base` to `/staff/` makes the staff build emit absolute asset URLs under `/staff/`, so its assets never collide with the patient-portal's at the root. This is what lets a single origin host both.

*Alternatives considered:* **Subdomains** (`portal.` / `staff.`) sidestep base paths entirely, but need two DNS names and two certificates, and `04-architecture.md` §9 already commits to path routing. **One build with two entry points** would share a bundle but couples the public showcase to the internal console — exactly what Z1 separates, and it would drag the staff bundle into the recruiter-facing surface. **Runtime base injection** (a placeholder rewritten at container start) buys deploy-time flexibility this project has no use for, since both prefixes are fixed.

### D2 — Caddy: distinct roots per prefix, with per-prefix SPA fallback

```
                       ┌──────────────── Caddy (only public container) ────────────────┐
                       │                                                              │
  GET /api/health ─────┼─▶ handle /api/*      reverse_proxy api:8080                   │
                       │                                                              │
  GET /staff/… ────────┼─▶ handle_path /staff/*  root /srv/staff                       │
                       │                          try_files {path} /index.html         │
                       │                                                              │
  GET /… ──────────────┼─▶ handle /*          root /srv/patient-portal                 │
                       │                          try_files {path} /index.html         │
                       └──────────────────────────────────────────────────────────────┘
                                    │                          │
                              api:8080 (internal)        db:5432 (internal)
```

Three points carry the correctness:

1. **Ordering and specificity.** `/api/*` is matched before `/staff/*`, which is matched before the root catch-all. Caddy's `handle` blocks are mutually exclusive and ordered by specificity, so the root handler cannot swallow `/staff`.
2. **`handle_path` for the staff prefix** strips `/staff` before the file lookup, so `/staff/assets/x.js` resolves to `/srv/staff/assets/x.js`. Pairing a stripping matcher with a `base` of `/staff/` is the exact combination that makes both the asset path and the file path line up.
3. **`try_files {path} /index.html` inside each `handle` block**, not globally. A global fallback would serve the patient-portal `index.html` for an unknown `/staff/*` path — the app would load with the wrong basename and fail confusingly. Scoping the fallback per prefix is what makes the deep-link-reload scenario pass for both surfaces independently.

`/staff` without a trailing slash redirects to `/staff/` so relative resolution behaves.

### D3 — Frontends call the API by relative path

Both apps call `/api/...` relative to their origin; no API base URL is configured or built in. Same-origin by construction, so no CORS in any environment, and no build-time environment coupling. In dev, each Vite dev server proxies `/api` to the API so the same relative code path works without Caddy.

The typed API client and its `fetch` wrapper live in `packages/shared` and are consumed by both apps — which is how this change proves the workspace linking rather than asserting it.

### D4 — Static assets are baked into the Caddy image at build time

A multi-stage build compiles both SPAs (pnpm workspace install, then build each app) and copies the resulting `dist/` directories into the Caddy image at `/srv/patient-portal` and `/srv/staff`.

*Alternative considered:* building on the host and bind-mounting `dist/` into Caddy is faster to iterate but makes the image non-reproducible and depends on host toolchain state — the opposite of what a walking skeleton should prove. Named volumes populated by a build container add a lifecycle (stale volumes surviving a rebuild) with no benefit here. Local iteration uses the Vite dev servers anyway; the Compose stack represents the production shape.

### D5 — Migrations run as an explicit startup step, with the production caveat recorded

The API applies pending EF Core migrations on startup, gated so it is the container's deliberate first action rather than a side effect. For a single-instance VPS deployment (`04-architecture.md` §9) this is correct and keeps `docker compose up` a single command.

**Refined during implementation.** This was first written as an inline `await` after `builder.Build()` in `Program.cs` — "explicit, not a hosted-service side effect". That does not survive contact with the test harness: `WebApplicationFactory` captures the built host and stops the entry point there, so statements between `Build()` and `Run()` do not reliably execute. The migration would have been invisible to the integration tier — the very tier whose job is to prove migrations apply cleanly. It is therefore a first-registered `IHostedService` (`DatabaseMigrationStartupService`), because host startup is a hook the real container and the test host genuinely share. Still explicit and easy to find; the logic and the caveat stay in `DatabaseMigrator`.

**Recorded caveat:** migrate-on-startup is unsafe with concurrent instances (two replicas racing the same migration). Horizontal scale is out of scope and is already the documented trigger for revisiting Redis (`04-architecture.md` §7); it is the same trigger for promoting migrations to a separate step. Noting it here means the later change does not have to rediscover why.

The initial migration creates one minimal marker table. Its only job is to prove the migration path end to end; the real schema arrives with change 3.

### D6 — Two test tiers, and a third that verifies what the others structurally cannot

This is the decision most worth attention, because the change's central risk falls in a gap between the obvious two tiers.

| Tier | Mechanism | Proves | Blind to |
|---|---|---|---|
| Unit | plain test project against `Domain` | nothing yet — establishes the project and the CI step | everything |
| Integration | `WebApplicationFactory` + Testcontainers PostgreSQL + Respawn | the API, EF migration, and DB connectivity are real | **Caddy — the API is invoked in-process; no proxy, no static files, no base paths** |
| Compose smoke | `docker compose up` + `curl` assertions in CI | Caddy routing, both base paths, deep-link fallback, asset resolution | fine-grained behavior |

The integration tier cannot see the thing this change exists to de-risk. `WebApplicationFactory` bypasses Caddy entirely, so a green integration suite is fully compatible with a Caddyfile that 404s every deep link. Verifying the base-path contract by hand would mean the project's riskiest infrastructure is the only part not regression-guarded — and it would silently rot the first time someone edits the Caddyfile.

So CI adds a **third tier**: bring the stack up, then assert with `curl` that `/` and `/staff/` each return their own `index.html`, that each app's primary asset returns `200` under its own prefix, that a deep link like `/staff/anything` returns `index.html` rather than `404`, and that `/api/health` reports healthy through the proxy. A handful of shell assertions, and they map one-to-one onto the scenarios in the spec.

Respawn has no data to reset in this change. It is wired now because retrofitting state isolation after tests exist is worse than establishing it while the suite has one test.

### D7 — CI ordering: cheap and deterministic gates before Docker-dependent ones

`openspec validate --strict` → i18n-key check → build → unit → integration (Testcontainers) → compose smoke. The fast, hermetic checks fail first; the two Docker-dependent tiers run last. GitHub Actions' Ubuntu runners provide a Docker daemon, so both work without extra setup.

The i18n check is a small script comparing key sets across the pt-BR and en resource files and failing on any asymmetry. Written now, while there is exactly one string to check, it is trivially verifiable — and it is the mechanism that keeps the i18n clause of the Definition of Done honest for every later change.

### D8 — Correlation id: middleware plus Serilog's `LogContext`

Middleware reads the inbound correlation header, generates one when absent, pushes it into Serilog's `LogContext` for the request scope, and writes it to the response. Every log entry emitted during the request carries it without any call site passing it along. The same mechanism extends to Hangfire jobs and webhook handlers in changes 6–8, which is why it is worth establishing on a request path that has nothing to correlate yet.

### D9 — Local TLS: HTTP locally, automatic HTTPS in production, one Caddyfile

Caddy cannot obtain a Let's Encrypt certificate for `localhost`. The Caddyfile's site address comes from an environment variable, so local runtime binds plain HTTP while production binds a real hostname and Caddy's automatic HTTPS engages — a config difference, not a code or file difference, consistent with the 12-factor SMTP handling in `03-nfr.md` §8.

Production TLS stays load-bearing for OAuth redirect URIs and Google webhooks (changes 6–7). Those changes are the first to actually require a public hostname, and they are the right place to exercise it.

## Risks / Trade-offs

- **A wrong base-path contract is expensive to unwind later** → the whole reason this change is first and isolated; the D6 compose-smoke tier turns the contract into a CI-enforced regression guard rather than a one-time manual check.
- **Compose smoke tests are slower and flakier than in-process tests** (container startup, readiness timing) → keep the assertion set tiny, depend on Compose healthchecks rather than sleeps, and let this tier assert only routing, never behavior.
- **Testcontainers requires a Docker daemon** → true on GitHub Actions' Ubuntu runners and on local Docker Desktop; the project already requires Docker for its runtime, so this adds no new prerequisite.
- **Migrate-on-startup breaks under concurrent instances** → documented in D5, with horizontal scale as the explicit revisit trigger; harmless at the single-instance scope this project commits to.
- **A skeleton with no domain can drift from what the domain needs** → mitigated by choosing a genuinely end-to-end probe (HTTP → API → EF → real PostgreSQL) instead of a static endpoint that would prove nothing about the write path.
- **Baking assets into the Caddy image means a frontend change requires an image rebuild** → accepted; local iteration uses the Vite dev servers, and reproducibility matters more than rebuild speed for the artifact that represents production.
- **Two ecosystems in one repo (pnpm + .NET)** → the CI pipeline exercises both on every run, so a break in either is caught immediately rather than at deploy.

## Migration Plan

No migration — this is the first change and there is nothing deployed. Rollback is deleting the branch.

Sequence within the change: repository skeleton and pnpm workspace → `Api` + `Domain` projects with the reference discipline in place → health endpoint and EF marker migration → integration harness → both SPA health pages sharing `packages/shared` → Caddyfile and Compose → CI pipeline including the compose-smoke tier.

Verification is the Definition of Done in the proposal, and the CI pipeline is what makes it repeatable.

## Open Questions

All three resolved during implementation:

- **Exact pins** — resolved: .NET 10 LTS (`global.json`, SDK 10.0.400, roll-forward `latestFeature`), PostgreSQL 17 (`postgres:17-alpine`, the same tag in Compose and Testcontainers), Node 22, pnpm 9.15.2. Written back into `00-context.md` §1 with a note that changing any pin means updating the table, `global.json`, both Postgres tags, and CI together.
- **`openspec/config.yaml` `context:`** — resolved: populated with the doc map, the non-negotiables, and the Definition of Done, plus per-artifact rules. Later proposals inherit the substrate instead of restating it.
- **Correlation header** — resolved: `X-Correlation-ID`. W3C `traceparent` buys distributed-tracing interoperability that has no consumer here (single deployable, no tracing backend), and `03-nfr.md` §4 keeps observability proportional on purpose. Recorded in `00-context.md` §5. Revisit only if a real tracing stack arrives.

Discovered while implementing, and worth carrying forward:

- **`redir` needs all three arguments.** `redir /staff/ 308` reads the first token as a *matcher* — any token starting with `/` is valid there — so it matched nothing and answered an empty `200`. The correct form is `redir /staff /staff/ 308`. A Caddyfile that is syntactically valid and passes `caddy validate` can still be silently wrong; only the compose-smoke tier catches this class of error.
- **The solution file is `ClinicScheduling.slnx`** — .NET 10's `dotnet new sln` emits the newer XML format. Both the Dockerfile and CI reference it by that name.
