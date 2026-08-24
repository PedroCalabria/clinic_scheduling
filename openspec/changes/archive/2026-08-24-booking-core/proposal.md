## Why

Four changes have built the terms of the promise and none of them has kept it. `availability-read`
answers "when could this happen?" against a subtraction with one real producer and two named-but-empty
seams, and its own risk register says so: a slot names a room it has not reserved, lead time and
horizon are applied on a read whose write does not exist yet, and the appointment half of the busy set
ships untested. SC-2 — "no double-booking, by construction rather than by convention" — is at this
point a claim in a README and a partial index nobody has written.

This change creates the racer. A patient searches real availability and books (P2 → P3 → P4), and the
guarantee becomes true in the only way that counts: two simultaneous bookings for one slot cannot both
commit, whatever the application code does.

## What Changes

**The `Appointment` aggregate and its state machine** (02 §3, §6):

- The aggregate, and the full status enum — `Scheduled`, `Completed`, `NoShow`, `Cancelled`,
  `Rescheduled`. **Only the create path is wired**: booking enters `Scheduled`. The four terminal
  transitions belong to `booking-lifecycle` (5b); the enum is declared whole because I9 is a statement
  about a shape, and a two-value enum would misdescribe it.
- Duration is **baked** into the appointment's own range at booking (I1). A later edit to
  `ProfessionalAppointmentType.durationMinutes` moves future searches and never an existing
  appointment.
- `rescheduledFromId` and `externalEventId` are **not** created here. They belong to 5b and to change
  6, and this project does not add a column for a producer that does not exist.

**The three enforcement floors** — the part that makes this the interview-grade change:

1. **Database (I4/I5/I6).** Three `EXCLUDE USING gist` constraints over a `tstzrange` column with
   `btree_gist` — `(professional_id =, time_range &&)`, `(resource_id =, time_range &&)`,
   `(patient_id =, time_range &&)` — each **partial** on the live state, so a cancelled or rescheduled
   row frees the slot it held. The application check exists to produce a friendly message; the
   constraint is what makes the guarantee true under a race. This is where `tstzrange` and GiST first
   exist in the schema, which is where Decision L always said Dapper would land.
2. **Domain (I1–I3, I8).** Duration baked (I1); the professional holds the appointment type's
   specialty (I2, `booking.specialty_mismatch`); the assigned resource is of the required type (I3);
   minimum lead time and scheduling horizon (I8, `booking.lead_time_violation` /
   `booking.horizon_exceeded`).
3. **I7 + the G1 lock, retrofitted into change 4's block path.** `availability-read` shipped internal
   block creation with no appointment-collision check and no lock, on the stated grounds that there
   was nothing to race. There is now, so both directions close in this change and both take the
   professional-scoped transaction advisory lock (G1). This is a change to what `availability`
   promises, not only to code behind it — see Modified Capabilities.

**Read/write agreement, by construction rather than by hope.** The booking path does not re-implement
the read's rules; it **runs the same solver** over the single requested professional, type and date and
asks whether the requested instant is one of the slots it offers. Working hours, exceptions, blocks,
step, lead time and horizon therefore cannot drift, because there is one implementation. The agreement
test the Definition of Done asks for stays, downgraded from sole protection to regression guard.

**The `BusyInterval` seam, filled.** A `Scheduled` appointment becomes a busy interval for its
professional and populates `ResourceCandidate.BusyIntervals` for its room — the two lists change 4 left
typed and empty. One loading step now serves both the availability read and the booking write, so the
appointment producer is exercised by every test on either path.

**Server-side resource assignment (F2).** The server picks a free resource of the required type from
the candidate set and **never** trusts a caller-supplied id; the booking request carries no resource
field at all, which makes it structural rather than a rule someone must remember. If every room is
taken at commit, `booking.resource_unavailable` (409).

**Slot identity and optimistic booking (Q4).** The request names the slot by **UTC instant** plus
professional plus appointment type — never a wall-clock label, so a fall-back day that legitimately
yields two slots an hour apart in UTC stays unambiguous. On an exclusion-constraint rejection the API
answers `booking.slot_taken` (409), which is P3's "slot just taken" state.

**Screens P2, P3, P4** (patient portal, Google sign-in). P2 is the flagship — the visible form of the
project's thesis, with every state designed (results, loading, empty, error, taken). P3 confirms the
slot, collects the minimal data a patient record is still missing, and holds the data-processing
consent gate. P4 confirms.

**Two error codes are missing from the catalogue, not one.** Both go into `07-error-codes.md` before
use:

- `booking.slot_blocked` (409) — the booking direction of I7: the professional has an internal block
  over the requested time. Distinct from `slot_taken` deliberately, because the cause and the remedy
  differ: nobody took this slot, the professional declared themselves unavailable, and a patient told
  "someone just booked it" would go looking for a race that did not happen.
- `booking.patient_busy` (409) — I6, the patient's own overlap. **This one is beyond the brief and is
  flagged for review**: the catalogue has a code for the professional's collision (`slot_taken`) and
  for the room's (`resource_unavailable`), and none for the patient's, so the third exclusion
  constraint has no way to answer. Overloading `slot_taken` would tell a patient somebody else took a
  slot they themselves are standing in.

`auth.consent_required` (422), catalogued since the seed and never yet used, is used here — see Impact
for why that is a correction rather than an addition.

## Capabilities

### New Capabilities

- `booking`: the `Appointment` aggregate and status enum; the create path to `Scheduled`; the three
  enforcement floors (database exclusion constraints for I4–I6, domain invariants I1–I3 and I8, and the
  I7 cross-table refusal under the G1 lock); automatic server-side resource assignment; slot identity
  by UTC instant with optimistic commit; the data-processing consent gate; and the patient booking
  surface P2/P3/P4. The four terminal transitions, reschedule and cancel are `booking-lifecycle`'s.

### Modified Capabilities

- `availability`: **internal block creation is no longer unconditional.** Change 4's requirement that a
  professional may record any forward range now carries an exception — a range overlapping one of their
  own active appointments is refused with `booking.block_overlaps_appointment` (409) — and both the
  block-creation path and the booking path serialize on the same professional-scoped transaction lock,
  which is what makes the cross-table check race-safe rather than merely written. The busy-interval
  requirement also changes what it promises: the set that was "taken from the professional's active
  internal time blocks" now genuinely includes appointments, and a resource's occupied periods stop
  being vacuous. Nothing change 4 promised is withdrawn; two of its requirements become stricter and
  one becomes true.

## Impact

| Area | Change |
|---|---|
| `apps/api/src/Domain` | The `Appointment` aggregate, `AppointmentStatus`, and the booking rules (I1–I3, I8) as pure domain code. The solver is **reused, not extended** — its busy lists finally arrive full |
| `apps/api/src/Api` | New `Booking` slice (`POST /api/appointments`); change 4's `Availability` slice gains the appointment producer, the shared loading step, and the retrofit on the block-creation path; one EF migration adding `appointments` with a `tstzrange` column, `btree_gist`, and three partial `EXCLUDE` constraints |
| Persistence | **Decision L lands, narrowly and on purpose.** EF Core still performs the aggregate insert (that is what the aggregate is for, and the constraint protects the row regardless of which client issues it). Dapper owns exactly what EF cannot express: `pg_advisory_xact_lock` and the `time_range &&` overlap reads that feed the busy set. The NodaTime Npgsql plugin, deferred twice, is **still** not needed — `NpgsqlRange<DateTimeOffset>` maps `tstzrange` natively and `Instant` converts exactly |
| `apps/patient-portal` | P2 `/book`, P3 `/book/confirm`, P4 `/book/success` — the first designed, stateful patient surface, and the first consumer of TanStack Query's real justification (volatile server state) |
| `packages/shared` | Booking API client functions and types; i18n keys (pt-BR + en) for P2/P3/P4 and for every `booking.*` code including the two new ones |
| `docs/07-error-codes.md` | `booking.slot_blocked` and `booking.patient_busy` added before use |
| Dependencies | Dapper added. Nothing else — no cache, no queue, no mediator |
| Consent (correction) | Change 2 already grants `DataProcessing` at Google just-in-time provisioning, so "capture consent at first booking" as written in `06 §P3` describes a capture that has already happened. What P3 genuinely owns is the **gate**: booking requires an active consent at the current version, refused with `auth.consent_required` (422) when P7 has revoked it — closing a loop change 2 opened by making revocation possible with nothing checking it |
| Not touched | Reschedule and cancel, the four terminal transitions, P5, P6, S1, S4, S5 (all 5b); calendar sync, the outbox, `externalEventId` (6/7); the reconcile sweep and G2 (7); reminders (8); `ReconciliationConflict`; role changes; the buffer's DB-level enforcement (P-4 stays a recorded trade-off — the constraint operates on the raw range, so two exactly-abutting bookings in one room remain theoretically race-possible) |
