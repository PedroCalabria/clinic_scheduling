## Why

`booking-core` made a booking atomic and made it **permanent**. A patient can create an appointment
and can do nothing else with it: there is no code path in the system that produces a terminal
appointment row. The three exclusion constraints are partial on `WHERE status = 'Scheduled'` — a
predicate written deliberately in anticipation, whose freeing behaviour 5a could only prove by writing
a terminal row *directly, bypassing the handler*. The state machine in `02 §3` has five states and one
of them is reachable.

That gap is also a product gap. UC-3 is one of the three core flows, and F3 — the patient's power over
their own appointment, bounded by a 24-hour cutoff — is what `02 §5` calls the concrete demonstration
of RBAC and ownership-based authorization coexisting. Today a patient who books at the wrong time has
to telephone.

This change makes an appointment changeable by the person it belongs to, and closes the round trip the
partial index was built for: cancel a slot and it is offered again.

## What Changes

**Two transitions, wired onto the aggregate 5a shaped for them** (02 §3, §6 I9):

- `Scheduled → Cancelled` and `Scheduled → Rescheduled`, as guarded methods on `Appointment`. The
  enum already holds all five values; this change reaches two of them. `Completed` and `NoShow` are
  front-desk facts and stay unreachable until `booking-desk` (5c).
- Both are refused from a terminal state, so a double cancel is an answer rather than a second write.
- `rescheduledFromId` is added now, because its producer now exists — the column 5a explicitly
  declined to create ahead of one. `Rescheduled` is terminal and **spawns a new linked `Scheduled`
  appointment**, preserving history for audit and LGPD rather than mutating a range in place.

**The reschedule statement ordering — load-bearing, and invisible when wrong** (02 §5):

The exclusion indexes are partial *and* non-deferrable, so they are evaluated per statement. Within
one transaction under the professional-scoped advisory lock, a reschedule must **UPDATE the old row to
`Rescheduled` first** — at which point it leaves the partial index — and **then INSERT the new row**.
The reverse order fires the patient constraint against the still-live old row and always fails a
same-patient reschedule.

The failure is order-dependent in a way that defeats a natural test: moving an appointment to next week
passes under either ordering, and only a **near** move collides. So the integration tier moves an
appointment by a few minutes, and that choice is the test's whole point rather than an arbitrary
fixture value.

**Reschedule is scoped to the same professional and appointment type.** Moving to a different
professional is a cancel plus a new booking, not a reschedule. This is a domain decision first — the
new appointment is a different commitment with a different person — and it has the useful consequence
that the professional lock stays single-keyed, so two locks are never held and the lock-ordering
deadlock that a cross-professional reschedule would introduce does not exist to be solved.

**The cutoff (F3), parameterized for an authority it does not yet have:**

- The cutoff does **not** join `SchedulingParameters`. Those three numbers decide what availability may
  *offer*; the cutoff decides who may *undo*, the solver has no use for it, and putting it there would
  hand the read a rule it must then be trusted to ignore.
- The transition methods take a `cutoffApplies` **fact**, not a role — the same bargain I2's
  qualification fact struck in 5a, keeping the core role-agnostic and unit-testable without a session.
  The patient path in this change always passes "applies"; the front-desk override that passes
  otherwise is `booking-desk`'s to exercise. The parameter is built here because building the rule
  without it would mean rewriting the method signature in 5c.
- A patient cancelling or rescheduling inside the cutoff is refused `booking.cutoff_passed` (422).

**Ownership, on a second entity.** A patient may cancel or reschedule only their own appointment,
through the ownership guard change 2 delivered and change 4 already reuses for time blocks. This is
also the change that has to decide what a patient learns from asking about an appointment that is not
theirs — the catalogue already set that precedent for patient records, and the same reasoning is
applied here rather than re-derived (see design).

**The availability round trip — the demonstrable behaviour.** Because `IsLive` and the exclusion
predicate both name the single value `Scheduled`, a cancelled appointment stops occupying its
professional, its room and its patient in the same instant on both floors, with no migration and no
second rule. Book, cancel, search again: the slot is back. 5a proved the mechanism against a
hand-written row; this change proves it through the product.

**Screens P5 and P6** (patient portal, both locales). P5 lists a patient's own upcoming and past
appointments, with reschedule and cancel **disabled inside the cutoff** and a message to call
reception — the rule visible before it is hit, not only as a refusal afterwards. P6 reuses P2's search,
scoped to the same professional and appointment type. P4's onward link, which 5a shipped pointing at
the profile as a named temporary destination, now points where it always meant to.

**Almost no new error codes — one, and it is flagged.** `booking.cutoff_passed` (422) and
`booking.appointment_not_found` (404) have been in the catalogue since the seed and are used here for
the first time; `auth.ownership_denied` already exists and is already used.

**`booking.appointment_not_changeable` (409) is added, and this paragraph originally claimed nothing
would be.** Implementation found no honest answer for a patient cancelling an appointment that is
already terminal — the two-tab case, and the back-button case. `appointment_not_found` would deny a row
the patient is looking at on P5; `auth.ownership_denied` is about *who* is asking and the patient does
own it; `cutoff_passed` would give a time-based reason for a state-based refusal, which is exactly the
confusion `slot_blocked` was split away from `slot_taken` to prevent. **Flagged for review** on the same
terms 5a flagged `booking.patient_busy`: if the reviewer prefers to overload an existing code, the
change is one mapping line and one i18n pair, and neither the invariant nor the constraint is affected.

One catalogue **annotation** is also needed: `booking.appointment_not_found` is
described as "unknown / soft-deleted appointment", and 5a deliberately gave `appointments` no
soft-delete column (design B3 — status *is* the history). The row is corrected to say what the code
actually means, in the same spirit 5a annotated `auth.consent_required` at its first use.

**The calendar seam, recorded rather than silently deferred.** `06 §P6` promises that cancelling
"releases slot + resource and propagates to the external calendar", and `02 §5` says the Google event
is removed on cancel. This change delivers the internal release and **not** the propagation, which is
`calendar-outbound` (change 6) — there is no outbox, no `externalEventId` and no connected calendar to
propagate to. Half of a documented sentence is delivered here, and design.md says which half and why,
so a reader of the code is not left to discover it.

## Capabilities

### New Capabilities

None. `booking` already exists as a capability; this change extends it.

### Modified Capabilities

- `booking`: the capability currently promises that an appointment can be created and that a
  terminally-stated appointment stops being busy — the second of those being, today, a property no API
  caller can bring about. This change **adds** requirements for cancelling and rescheduling an
  appointment, for the cutoff rule and its authority parameter, for ownership authorization on an
  appointment, and for the patient's own appointment list and change surface (P5/P6). It **modifies**
  the existing "a booked appointment is unavailable time" requirement so that the terminal-state
  scenario is reachable through the product rather than only by a direct write.

`availability` is **not** modified: the solver, the block path and the busy-interval contract are
untouched, and the round trip works because the existing `IsLive` predicate already says so.
`identity-session` is **not** modified: the ownership primitive is reused exactly as change 4 reuses
it.

## Impact

| Area | Change |
|---|---|
| `apps/api/src/Domain` | `Appointment` gains two guarded transitions and the `rescheduledFromId` link; a cutoff rule taking an authority fact. No new domain type beyond what the transitions need — the aggregate was shaped for this in 5a |
| `apps/api/src/Api` | The `Booking` slice gains the patient's appointment list, cancel, and reschedule endpoints. One EF migration adding `rescheduled_from_id` (nullable, self-referencing) — additive, and the three exclusion constraints are untouched because their predicate already covers the new states |
| Persistence | **The reschedule is the first handler whose statement *order* is a correctness property rather than a style choice.** It runs under the existing `ScheduleMutation` lock and the existing `ScheduleReader` loading step; no new infrastructure and no new dependency |
| `apps/patient-portal` | P5 `/appointments`, P6 `/appointments/:id/reschedule`. P6 reuses P2's search components rather than copying them, scoped to one professional and type; P4's onward link is repointed |
| `packages/shared` | API client functions and types for the list, the cancel and the reschedule; i18n keys (pt-BR + en) for P5/P6 including the cutoff-disabled state and `booking.cutoff_passed` |
| `docs/07-error-codes.md` | No new codes. One annotation: `booking.appointment_not_found` no longer says "soft-deleted", because appointments carry no such column |
| Dependencies | None added |
| Not touched | `Professional.fullName` (P-5), book-on-behalf, the front-desk cutoff override, `AppointmentSource.FrontDesk`, `AccessLog` on staff PII reads, S1/S4/S5 — **all `booking-desk` (5c)**; `Completed` and `NoShow` (5c); calendar sync, the outbox and `externalEventId` (6/7); reminders (8); **cross-professional reschedule**, ruled out rather than deferred; P-4's buffer trade-off, unchanged |
