# Non-Functional Requirements — Clinic Scheduling System

> **Status:** Phase 5 consolidated (Non-functional requirements).
> **Depends on:** `01-requirements.md`, `02-domain-model.md`.
> **Upcoming phases:** 6 — Architecture & technical decisions · 7 — OpenSpec translation & dev workflow.
> **Document language:** English.

---

## Guiding principle

NFRs are sized to the thesis. Each requirement must justify itself against the project's goal (resilient external integration + a real, defensible clinic domain). Anything heavier than that is over-engineering — the same bar applied to capabilities.

---

## Locked decisions (this phase)

| ID | Decision | Choice |
|---|---|---|
| G | Reminder channel (resolves P-1) | Email over **SMTP**; single reminder, default **24 h** lead time |
| H | Time model | Store in **UTC**; single configured **clinic timezone** for display |
| I | i18n strategy | API returns **error codes + params**; frontend translates |
| J | Session model | OIDC and internal accounts both converge to the app's **own session** (HttpOnly cookie + revocation) |

---

## 1. Internationalization (pt-BR / en)

- API is language-agnostic: returns stable error codes + parameters, never translated prose. Frontend owns translation (react-i18next, versioned resource files).
- Exception: content generated for outside the app (reminder email body) is rendered in the recipient's preferred language by the sending service.
- Locale resolution: explicit user preference > `Accept-Language` header > default pt-BR.

## 2. Security & authorization

- **Unified session model (J):** regardless of login path (Google OIDC or internal email/password), the app issues its **own session** — token in an HttpOnly, `SameSite`, `Secure` cookie, backed by a revocation table. Federated and internal identity are normalized to one uniform session; the rest of the system never needs to know how the user authenticated.
- **Google ID token validation:** signature via JWKS, plus `iss`, `aud`, `exp` checks.
- **OAuth calendar tokens encrypted at rest** — the refresh token is sensitive material.
- **CSRF protection** (cookie-based auth requires it).
- **Two-layer authorization:** RBAC by role + ownership check on patient-data access. Never trust a client-supplied ID for ownership.
- **Rate limiting on public endpoints** — the availability search is exposed and abusable.
- **Secrets outside code** (Google client secret, token-encryption key, SMTP creds). The *where* is decided in Phase 6.

## 3. Resilience & reliability (the thesis)

- **Idempotency** on external event creation — store `externalEventId`, never create twice.
- **Retry with exponential backoff** on transient failures and rate limits (HTTP 429).
- **Webhook dedupe** by event ID + `syncToken`.
- **Outbox pattern (candidate)** for outbound sync, so "appointment persisted" and "event created in Google" never diverge if the external call fails mid-way. This is the mature answer to "what if the DB committed but Google's API was down?". Mechanism and MVP-vs-fast-follow decided in Phase 6 (tracked P-5).

## 4. Observability

Proportional, free-tooling only:
- **Structured logging** (Serilog) with a correlation ID per request and per webhook/job execution.
- **Health checks** (`/health` including DB connectivity and `CalendarConnection` status).
- **Domain metrics that matter to the thesis:** sync failure rate, open reconciliation conflicts, no-show rate.
- No heavy stack (no full ELK/Prometheus); console/file output in dev.

## 5. Temporal correctness (critical for scheduling)

- Store everything in **UTC** (already using `tstzrange`).
- The clinic has a **single configured timezone** = the canonical display timezone; all rendering converts UTC → clinic timezone.
- Since multi-clinic is anti-scope, a single canonical timezone is sufficient and simple. No per-user timezones.

## 6. Accessibility

- Target **WCAG 2.1 AA** for the public patient portal (real audience includes elderly users): keyboard navigation, contrast, form labels, ARIA where needed.
- shadcn/ui (Radix underneath) provides accessible primitives by default.

## 7. Performance

- The tri-constraint availability query is the hot path: GiST indexes help, and computation is sliced by date window (never scans the full horizon).
- Pagination on list endpoints.
- **No cache on the critical path** in the MVP — availability changes in real time; caching would risk showing an already-taken slot. A conscious decision *not* to cache.

---

## 8. Runtime & tooling constraint

**Free tools only. External tools run via Docker — with one deliberate exception (outbound mail delivery in production).**

- **Orchestration:** Docker Compose for dev and prod (single VPS). This is the right simplicity tier — Kubernetes would be over-engineering against the "simple and concise" goal. (Podman is a drop-in alternative if ever needed.)
- **SMTP is environment-split behind plain SMTP config (12-factor):** the app always speaks plain SMTP; host/port/credentials come from env vars, so there is **no code change between environments**.
  - **Dev:** Mailpit container (maintained successor to MailHog) — catches all mail, web UI to inspect. Free, no external dependency.
  - **Prod:** point SMTP at an **external transactional relay with a free tier** (provider-agnostic; e.g. Brevo — verify current limits). No self-hosted mail server.
  - **Flag — do NOT self-host an SMTP delivery server (Postfix) in Docker for production delivery.** VPS providers commonly block outbound port 25; VPS IPs carry poor sending reputation; real deliverability demands SPF, DKIM, DMARC, and reverse DNS (PTR). A relay sidesteps all of it and stays free. Self-hosting SMTP is the one place where "Docker for everything" is the wrong instinct.
- **PostgreSQL:** Docker with a persistent named volume + scheduled backup job. Trade-off: a managed DB would be more resilient but isn't free; Docker Postgres is the pragmatic free/simple pick.
- **Reverse proxy + TLS (load-bearing for integrations):** OAuth redirect URIs and Google Calendar push webhooks both require public HTTPS with a valid CA-signed cert. Caddy provides automatic HTTPS (Let's Encrypt) and runs in Compose. Topology detail → Phase 6 (P-6).
- **Dev webhook reachability:** Google push notifications cannot reach `localhost`; dev needs a tunnel (cloudflared/ngrok) or a polling fallback. → Phase 6 (P-7).

---

## 9. Open items

| # | Item | Status / Resolve in |
|---|---|---|
| P-1 | Reminder channel | **Resolved (G):** email via SMTP, single reminder @ 24 h |
| P-2 | Incremental-sync design (`syncToken`) + webhooks | Phase 6 |
| P-3 | Calibrate architecture layering | Phase 6 |
| P-4 | Strict buffer enforcement at DB level | Phase 6 (if needed) |
| P-5 | Outbox pattern for outbound sync — adopt in MVP or fast-follow? | Phase 6 |
| P-6 | Deployment topology: Compose services + Caddy TLS | Phase 6 |
| P-7 | Dev webhook reachability (tunnel vs polling fallback) | Phase 6 |
| — | Confirm production SMTP relay approach (external relay vs. reconsider) | Confirm in Phase 6 kickoff |
