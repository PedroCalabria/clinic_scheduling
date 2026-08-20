## 1. Repository skeleton & pins

- [x] 1.1 Pin exact versions for .NET LTS, PostgreSQL, and Node LTS; write the chosen values back into `docs/00-context.md` §1 (replacing the "e.g." ranges) — reproducible integration tests are the stated reason for pinning
- [x] 1.2 Create the directory layout from `00-context.md` §2: `apps/`, `packages/shared/`, `infra/`, `.github/workflows/`
- [x] 1.3 Add `pnpm-workspace.yaml` covering `apps/patient-portal`, `apps/staff`, `packages/shared` (the .NET solution is deliberately outside the JS workspace)
- [x] 1.4 Add root `package.json` with scripts to build/lint/test the JS side, plus `.gitignore`, `.editorconfig`, and `.dockerignore`
- [x] 1.5 Commit `.env.example` listing every variable the stack reads (DB credentials, Caddy site address, connection string); no real secrets in the repo
- [x] 1.6 Wire `docs/00-context.md` into `openspec/config.yaml` `context:` so later `/opsx:propose` runs inherit the substrate (resolves a design Open Question)
- [x] 1.7 Decide the correlation header name (`X-Correlation-ID` vs `traceparent`) and record it in `00-context.md` §5 (resolves a design Open Question)

## 2. Backend solution (`Api` + `Domain`)

- [x] 2.1 Create the solution in `apps/api/` with two projects: `Api` (ASP.NET) and `Domain` (class library)
- [x] 2.2 Add the `Api` → `Domain` project reference, and verify `Domain` references no infrastructure packages (no EF, no Dapper, no ASP.NET) — the compiler is the boundary per `00-context.md` §3
- [x] 2.3 Create the vertical-slice folder structure inside `Api` (feature folders; endpoints call handlers directly, no MediatR) and place the health slice in it as the pattern later features follow
- [x] 2.4 Add Serilog with structured (JSON) output configured for console
- [x] 2.5 Add correlation-id middleware: read the inbound header or generate one, push it into Serilog's `LogContext` for the request scope, and echo it in the response header (design D8)
- [x] 2.6 Add a global exception handler returning the `{ code, params? }` envelope from `07-error-codes.md`, mapping unhandled errors to `server.unexpected` without leaking internals

## 3. Persistence & migration

- [x] 3.1 Add EF Core + the PostgreSQL provider to `Api`, with the connection string sourced from configuration/environment
- [x] 3.2 Add the `DbContext` under the `Api` infrastructure folder with one minimal marker entity
- [x] 3.3 Generate the initial EF Core migration for the marker table
- [x] 3.4 Apply pending migrations as an explicit startup step (design D5), and add a code comment recording the concurrent-instance caveat and its revisit trigger

## 4. Health endpoint

- [x] 4.1 Register an ASP.NET health check that executes a trivial query against PostgreSQL
- [x] 4.2 Expose `GET /api/health` anonymously, returning `200` + `Healthy` when the DB is reachable and `503` + `Unhealthy` when it is not
- [x] 4.3 Verify the response body discloses no connection string, credentials, host name, or stack trace in either state
- [x] 4.4 Confirm the `Api` container publishes no host port (only Caddy is public)

## 5. Shared package

- [x] 5.1 Scaffold `packages/shared` as a TypeScript package consumable by both apps
- [x] 5.2 Add the typed `fetch` wrapper and API client calling `/api/health` by relative path (design D3), with the response type and the `{ code, params? }` error shape
- [x] 5.3 Add the pt-BR and en i18n resource files containing the one shared user-facing string
- [x] 5.4 Verify both apps resolve the workspace dependency and typecheck against it

## 6. Patient portal (`/`)

- [x] 6.1 Scaffold the React + TypeScript + Vite app with `base: '/'` and router `basename: '/'`, both derived from one per-app constant (design D1)
- [x] 6.2 Add Tailwind and react-i18next (pt-BR + en) wired to the shared resource files
- [x] 6.3 Add TanStack Query and one health page that calls `/api/health` via the shared client and renders the status including DB connectivity
- [x] 6.4 Render the shared i18n string and add a language switch, verifying both languages with no missing-key fallback
- [x] 6.5 Configure the Vite dev-server proxy for `/api` so the relative path works without Caddy

## 7. Staff app (`/staff`)

- [x] 7.1 Scaffold the app with `base: '/staff/'` and router `basename: '/staff'`, both derived from one per-app constant (design D1)
- [x] 7.2 Add Tailwind, react-i18next, and TanStack Query mirroring the portal setup, consuming the same shared package
- [x] 7.3 Add its own health page calling `/api/health`, plus the shared i18n string and language switch
- [x] 7.4 Configure the Vite dev-server proxy for `/api`
- [x] 7.5 Build and confirm emitted asset URLs are absolute under `/staff/` — the check that `base` and `basename` agree

## 8. Caddy & Compose

- [x] 8.1 Write `infra/Caddyfile` with three ordered handlers: `/api/*` → `reverse_proxy api`, `handle_path /staff/*` → root `/srv/staff`, root catch-all → `/srv/patient-portal` (design D2)
- [x] 8.2 Add `try_files {path} /index.html` **inside each** static handler, not globally, so an unknown `/staff/*` path never falls back to the portal's `index.html`
- [x] 8.3 Redirect `/staff` (no trailing slash) to `/staff/`
- [x] 8.4 Take the site address from an environment variable so local runs bind plain HTTP while production engages automatic HTTPS from the same file (design D9)
- [x] 8.5 Write the multi-stage Dockerfile that pnpm-installs the workspace, builds both SPAs, and copies each `dist/` into the Caddy image at `/srv/patient-portal` and `/srv/staff` (design D4)
- [x] 8.6 Write the `Api` Dockerfile (multi-stage build/publish)
- [x] 8.7 Write `infra/docker-compose.yml` with `caddy`, `api`, `db`: only `caddy` publishes ports; `api` and `db` on an internal network; named volume for PostgreSQL data
- [x] 8.8 Add Compose healthchecks for `db` and `api`, and make `api` depend on `db` being healthy so startup migration does not race the database
- [x] 8.9 Run `docker compose up` and manually confirm `/`, `/staff/`, a deep link, and `/api/health` all behave before automating the checks

## 9. Test harness

- [x] 9.1 Create the unit test project targeting `Domain` (establishes the project and CI step; no domain logic to test yet)
- [x] 9.2 Create the integration test project with `WebApplicationFactory` + Testcontainers PostgreSQL, pinned to the same version as Compose
- [x] 9.3 Wire Respawn for state reset between tests, even though there is no data yet (design D6)
- [x] 9.4 Add the integration test asserting migrations apply cleanly and `/api/health` reports healthy against the real containerized PostgreSQL
- [x] 9.5 Add the i18n-key check script comparing key sets across the pt-BR and en resource files and failing on any asymmetry
- [x] 9.6 Write the compose-smoke script asserting, via `curl` against the running stack: `/` serves the portal, `/staff/` serves the staff app, each app's primary asset returns `200` under its own prefix, `/staff/anything` returns `index.html` not `404`, and `/api/health` reports healthy through the proxy (design D6 — the tier that covers what `WebApplicationFactory` structurally cannot)
- [x] 9.7 Verify the smoke script waits on Compose healthchecks rather than fixed sleeps

## 10. CI

- [x] 10.1 Add the GitHub Actions workflow in the D7 order: `openspec validate --strict` → i18n-key check → build (both ecosystems) → unit → integration → compose smoke
- [x] 10.2 Set up Node/pnpm and .NET with the pinned versions from task 1.1, with dependency caching
- [x] 10.3 Confirm the Testcontainers and compose-smoke steps run on the Ubuntu runner's Docker daemon
- [x] 10.4 Verify the pipeline fails as intended on a deliberately broken i18n key, then revert the break
- [x] 10.5 Verify the pipeline fails as intended on a deliberately broken Caddy `try_files` line, then revert the break — proves the smoke tier actually guards the change's central risk
- [ ] 10.6 Confirm the full pipeline is green on the branch

## 11. Definition of Done

- [x] 11.1 `docker compose up` brings up `caddy` + `api` + `db` with only Caddy exposed
- [x] 11.2 Patient portal loads at `/` and staff at `/staff`, each through Caddy, each rendering `/api/health` including DB connectivity
- [x] 11.3 Deep-link reload resolves correctly under both base paths
- [x] 11.4 Initial EF migration applies cleanly; integration test green in CI
- [x] 11.5 Correlation id present in logs and echoed in the response header
- [x] 11.6 The shared i18n string renders in pt-BR and en in both apps
- [x] 11.7 `openspec validate walking-skeleton --strict` passes
- [x] 11.8 Update `docs/00-context.md` if any pin or convention changed during implementation, so the substrate doc stays the source of truth
