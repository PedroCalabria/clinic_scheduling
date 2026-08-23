## Why

`professional-configuration` (3b) finished every term of the availability formula except the busy
intervals, and deliberately converted nothing: it stores "every Monday 09:00–12:00" as wall clock
because that is a rule, not an event. This change is the one that supplies a date — so it runs the
first real wall-clock→UTC conversion in the system, and turns configuration into answers.

The complication is that the subtraction has no producer. `Appointment` arrives in change 5 and
external blocks in change 7, so a solver built strictly to the 05 build order would ship a
subtraction step that never subtracts anything, verified only by tests that construct their own
inputs. Internal `TimeBlock` is the one busy-interval source that needs nothing from booking, and
it was filed under `calendar-integration` — a categorization error, because a professional blocking
their own lunch has no Google in it. Moving internal blocks and **S3** here gives the subtraction
real, producer-backed rows, gives the change a browser surface to validate, and leaves change 7 with
the external half it actually owns.

## What Changes

**The availability engine** — new `availability` capability, computed as interval arithmetic in the
Domain core:

- Candidate hours from `WorkingHoursTemplate`, matched **two-dimensionally**: the weekday *and* the
  concrete date falling inside the template's effective range. This closes 3b's open question 3
  (does the first cut use the effective-date dimension?) as **yes** — the column exists because the
  overlap rule needs it, and a solver that ignores it would answer today's question with next
  year's schedule.
- `WorkingHoursException` overrides that day: unavailable entirely, or different hours.
- Wall-clock → UTC per concrete date via NodaTime, with DST-ambiguous and DST-nonexistent local
  times handled explicitly rather than by an offset.
- Sliced into candidate slots by `ProfessionalAppointmentType.durationMinutes` — per professional,
  which is the whole reason that duration lives on the junction.
- Busy intervals subtracted. Internal `TimeBlock`s are real input from day one; **appointments and
  external blocks are a named seam with a genuinely empty input** until changes 5 and 7 supply them.
  The seam is wired and typed; no table is created for a producer that does not exist yet.
- Resource-type feasibility: a slot requires a `Resource` of the `AppointmentType`'s required
  `ResourceType` to exist and be usable. Existence is real here; "not occupied by an appointment" is
  vacuous until change 5, and the spec says so rather than implying a check that cannot fail.
- Two query modes: a specific professional, and **any professional of specialty X** — a union where
  every slot carries the `(professional, resource)` pair that satisfies it, resolved server-side.

**Internal block time (S3)** — the producer:

- `TimeBlock` entity with `source = Internal`; a professional creates, edits and retires their own.
- One rule: `start < end`, otherwise `block.invalid_range` (already catalogued in `07`). Overlapping
  blocks for the same professional are **allowed** — they union for availability, and refusing them
  would be arbitrary.
- No appointment-collision check and no professional-scoped lock. Both exist to protect a race
  against appointments (I7, G1); with no appointments there is nothing to race, and shipping a lock
  now would be a mechanism nobody can test.

**Consequences recorded elsewhere, not re-argued here:** Decision L is re-scoped so Dapper lands on
change 5's write path where `tstzrange` and GiST actually exist (`04-architecture.md` §2); S3 moves
from `calendar-integration` to `availability` (`06-ui-surfaces.md`); `block.invalid_range` is in the
catalogue (`07-error-codes.md`). Nothing is **BREAKING** — the migration is additive.

## Capabilities

### New Capabilities
- `availability`: the tri-constraint solver — working hours minus exceptions, converted against a
  concrete date, sliced by per-professional duration, minus busy intervals, gated on resource-type
  feasibility, in both specific- and any-professional modes; **and** internal `TimeBlock`s, the
  professional-declared unavailability the solver subtracts. Both live in one capability because a
  block is only meaningful as something availability removes. Change 7 extends the same entity with
  `source = External` under `calendar-integration`, where the sync machinery genuinely belongs.

### Modified Capabilities

None. The solver reads `clinic-configuration`'s data and `identity-session`'s roles without changing
what either promises.

## Impact

| Area | Change |
|---|---|
| `apps/api/src/Domain` | New availability solver (pure interval arithmetic, no infrastructure — the boundary guard now genuinely enforces this) and the `TimeBlock` entity |
| `apps/api/src/Api` | Two vertical slices: `Availability` (query) and the internal-block endpoints; one EF migration adding `time_blocks` |
| `apps/staff` | S3 at `/staff/blocks`, professional-role navigation |
| `packages/shared` | i18n keys (pt-BR + en) for S3 and `block.invalid_range` |
| Dependencies | None added. NodaTime, brought in by 3b and comparatively idle there, does its real work here |
| Tests | Domain unit tests for the solver, including DST gap and overlap against a **DST-observing zone** (`00-context.md` §6 — a Sao_Paulo-only test cannot fail); integration tests for the endpoints and the block↔availability round trip |
| Not touched | `Appointment` schema, `EXCLUDE`/GiST/`tstzrange` columns, Dapper, booking, the professional-scoped lock (G1), I7 enforcement, external blocks and Google sync, and the patient-facing P2/P3 and S1/S5 screens — availability output is API- and test-verified until P2 lands in change 5 |
