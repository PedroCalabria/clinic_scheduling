# Requirements Document — Clinic Scheduling System

> **Status:** Phases 1–3 consolidated (Problem & scope · Actors & roles · Use cases).
> **Upcoming phases:** 4 — Domain modeling & business rules · 5 — Non-functional requirements · 6 — Architecture & technical decisions · 7 — OpenSpec translation & dev workflow.
> **Product languages:** pt-BR / en (i18n). **Document language:** English.

---

## Project thesis (why it belongs in the portfolio)

This project's differentiator is **not** the state machine or the background jobs — those competencies are already demonstrated in another project. The thesis here is **resilient integration with external systems**: federated identity (OIDC), delegated authorization via OAuth 2.0, inbound webhooks, and resilience at the integration boundary (rate limiting, idempotency, retry with backoff, and reconciliation when external state diverges from internal state).

Design consequence: the chosen problem is one where **external integration is the core**, not an ornament. Every capability exists because the domain requires it — not by accumulation.

---

## Phase 1 — Problem & scope

### Problem

Clinics with multiple professionals and shared resources (rooms, equipment) lose revenue and time to manual scheduling. Three concrete pain points:

1. **Scheduling conflicts** — double-booking of a professional, room, or patient.
2. **No-shows** — missed appointments due to lack of reminders.
3. **Calendar divergence** — the clinic's schedule does not reflect the professional's real personal calendar; when they block a commitment in their own calendar, the front desk doesn't know and books over it.

### Technical core — tri-constraint scheduling

An appointment can only exist if, within the same interval, there is **simultaneous** availability of:

- **Patient** — no other appointment in the interval;
- **Professional** — within working hours, holding the required specialty, free and not blocked (including against blocks coming from the external calendar);
- **Resource** — room/equipment of the required type, available.

This is a constraint-satisfaction problem. It is what justifies shared-resource management entering the project out of **necessity**, not as an ornament.

### Success criteria (demonstrable with the system running)

- **SC-1** — The patient sees only genuinely free slots, with availability computed in real time against all three constraints and against the synchronized external calendar.
- **SC-2** — The system **prevents double-booking by construction**, not by convention.
- **SC-3** — A block created by the professional in Google Calendar reflects as unavailability in the clinic **without manual intervention**.
- **SC-4** — Reminders reduce no-shows.
- **SC-5** — All content available in pt-BR and en.

### Anti-scope (explicitly out, and why)

| Out of scope | Reason |
|---|---|
| Billing / payment / insurance | An entire domain of its own; not the thesis. |
| Electronic health records and clinical notes | Ultra-sensitive, regulated data; cutting it keeps the LGPD scope contained and realistic. |
| Telemedicine / video | Outside the scheduling thesis. |
| Multiple clinics / franchise / marketplace | True multi-tenancy is a separate thesis. |
| Native mobile app | Responsive web covers the demonstration. |
| Outlook and SMS | Choice for **depth** (Google + one reminder channel) over shallow breadth. |
| Full LGPD compliance tooling (data-subject requests, DPO workflow) | Scope is LGPD **awareness**, not certification. |

---

## Phase 2 — Actors & roles

### Authorization differentiation (portfolio argument)

This project combines **two distinct types** of authorization, and being able to distinguish them is an interview point:

- **Role-based access control (RBAC)** — what each type of user is allowed to do (actions).
- **Ownership-based / row-level authorization** — the patient can only access resources that are *theirs*. Ties directly to LGPD (minimization + restricted access to sensitive data).

### Roles

| Role | Identity | Responsibilities | Data scope |
|---|---|---|---|
| **Patient** | Google (OIDC) | Search availability; book/reschedule/cancel own appointments; manage own data and consents. | Restricted to self. |
| **Professional** | Google (OIDC) — login + calendar scope in the **same consent** | Manage own schedule, working hours, specialty; connect Google Calendar (grants the bidirectional-sync consent); view own schedule; block time. | Own schedule. |
| **Front desk** | Internal account (email/password) | Book/reschedule/cancel on behalf of patients (phone and walk-in); run day-to-day; resolve flagged reconciliation conflicts. | Operational. Does **not** manage structural configuration. |
| **Administrator** | Internal account (email/password) | Register professionals, rooms/resources, specialties, working-hour templates; user management; reports. | Structural. |

**Locked decision:** front desk and administrator are **separate roles** — the permission difference is real (front desk does not touch structural configuration) and cheap to model; it reinforces the argument of RBAC + ownership-based authorization coexisting.

### Hybrid identity model

Patients and professionals sign in via **Google (OIDC)**; the professional combines, in a single consent, login + calendar scope. Front desk and admin use **internal accounts** (email/password), since they are clinic staff — it makes no sense to depend on their personal Google account. Demonstrates conscious coexistence of federated identity and internal identity in the same system.

### Auth vs. calendar distinction (do not conflate)

- **"Sign in with Google" = authentication** (OpenID Connect): federated identity, ID-token validation, mapping external account → internal user.
- **"Sync my Google Calendar" = authorization** (OAuth 2.0 with Calendar API scopes): refresh token, renewal, and revocation. This is where resilience lives: rate limiting, expired tokens, backoff, idempotency when creating events.

For the professional, the two are combined into a single consent — but **modeled as separate responsibilities**.

### Cost rationale (free tools only)

- **Authentication (Google Identity / OIDC):** free, no billing quota.
- **Google Calendar API:** free for the project's scope. All standard use is available at no cost; billing is planned only for use **above** quota (policy change expected later in 2026), with a limit on the order of ~1M requests/day (new projects: ~10,000/min per project, 600/min per user) — far above what a portfolio consumes.
- **Derived design decision:** use **incremental sync (`syncToken`) + webhooks** instead of aggressive polling, to keep call volume low by construction. Locked in Phase 6. Beyond being economical, it is the correct practice and becomes an interview argument ("I chose incremental sync to respect the API's quota and rate limit").
- **Cost watch point — reminder channel:** SMS has no genuinely free option; it will be cut. The reminder channel (email via SMTP / mail catcher in dev, or in-app) is a **pending item to resolve in Phase 5**.

---

## Phase 3 — Critical use cases

### Core flows (the thesis)

**UC-1 — Patient searches availability and books an appointment** *(product star)*
Exposes the tri-constraint computation to the outside. The patient picks a specialty and a date window; the system returns **only** genuinely free slots, computed in real time against all three constraints and against the synchronized external calendar. Booking consolidates the reservation **atomically** — no double-booking by construction.

**UC-2 — Professional connects Google Calendar; schedule syncs both ways** *(integration star)*
- *Inbound:* professional blocks "lunch" in Google → webhook → clinic now sees it as unavailable.
- *Outbound:* appointment booked in the clinic → event created in their Google Calendar.
- Where token refresh, rate limiting, idempotency, and reconciliation live.

**UC-3 — Reschedule / cancel an appointment**
By the patient (self-service, scope restricted to own data) and by the front desk (on behalf of others). Releases the slot and the resource and propagates the change to the external calendar.

### Supporting flows (necessary, not the thesis)

**UC-4 — Admin configures the clinic**
Professionals, rooms/resources, specialties, working-hour templates. CRUD; the foundation for everything to work.

**UC-5 — Appointment reminder**
Scheduled job that fires a reminder X hours before. Satisfies SC-4. Enters the MVP in its simplest possible form.

### Locked domain decisions

**Decision 1 — Booking model: support both.**
- *Specific professional* — primary path; the search fixes one variable.
- *"Any professional of specialty X"* — variation; makes availability computation richer (the solver matches patient × any-eligible-professional × room). This is the technical differentiator, and a real clinic offers both.

**Decision 2 — Conflict reconciliation (UC-2 star case): flag for a human to resolve (human-in-the-loop).**
When the professional externally blocks a slot that already had an appointment, external state diverges from internal state. The system **detects the conflict and flags it for the front desk to resolve** — because canceling a patient's appointment is a business decision, not an automatic one. Demonstrates maturity: not every conflict is solved with code; some require a human decision with the system providing support.

### MVP cut

**Inside the MVP:** UC-1 + UC-2 + UC-3 + UC-4 + UC-5 (simple version) + hybrid authentication. A cohesive, end-to-end defensible product.

**Outside the MVP (fast-follow, if time permits):** waitlist; rescheduling with automatic new-slot suggestions; occupancy analytics reports. None is the thesis — cuttable fat with no loss.

---

## Tracked open items

| # | Item | Resolve in |
|---|---|---|
| P-1 | Reminder channel (email via SMTP / mail catcher vs. in-app) | Phase 5 (NFR) |
| P-2 | Incremental-sync design (`syncToken`) + webhooks to contain call volume | Phase 6 (architecture) |
| P-3 | Calibrate architecture layering (avoid full Clean Architecture by reflex; find the minimum that demonstrates the skill without over-engineering) | Phase 6 |
