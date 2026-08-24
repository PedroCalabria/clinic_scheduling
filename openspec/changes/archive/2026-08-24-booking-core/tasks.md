## 1. The catalogue first, because codes precede their use

- [x] 1.1 Add `booking.slot_blocked` (409) to `07-error-codes.md` — the I7 booking direction — with the reason it is not `slot_taken`: the cause is that the professional declared themselves unavailable, and telling a patient somebody was faster sends them looking for a race that did not happen (B13)
- [x] 1.2 Add `booking.patient_busy` (409) to `07-error-codes.md` for I6, noting that it exists because the third exclusion constraint would otherwise have no answer, and that overloading `slot_taken` would tell a patient somebody else took a slot they are standing in. **Flagged for review** — the proposal names it as beyond the brief
- [x] 1.3 Annotate `auth.consent_required` in the catalogue with the fact that `booking-core` is its first use, and what it now gates (B12)
- [x] 1.4 Add the constants to `ErrorCodes`, each carrying the same argument in its doc comment, in the established voice

## 2. Dapper, and the connection it must ride

- [x] 2.1 Add the Dapper package reference to `Api` only, and confirm `DomainBoundaryTests` still passes — this is the second infrastructure dependency the boundary guard has had to refuse entry to `Domain`, and it is the one most likely to be added in the wrong project
- [x] 2.2 Write one helper that runs Dapper **on the `DbContext`'s own connection, inside the `DbContext`'s current transaction**, and document at the call site why: a transaction-scoped advisory lock taken on another connection is released immediately and protects nothing, while every functional test still passes (B5)
- [x] 2.3 Write one function deriving the advisory-lock key from a professional id, used by both mutating paths, recording that a key collision costs serialization between unrelated professionals and never costs correctness (B7)
- [x] 2.4 Integration-test the seam itself before anything depends on it: take the lock in one transaction and assert a second transaction blocks until the first ends, and that a failed handler releases it. This is the assertion the whole cross-table floor rests on, and its failure mode is invisible (see Risks)

## 3. Domain: the `Appointment` aggregate and its state machine

- [x] 3.1 Add `AppointmentStatus` with all five values — `Scheduled`, `Completed`, `NoShow`, `Cancelled`, `Rescheduled` — recording that I9 is a statement about a shape and a two-value enum would misdescribe it while making 5b a column rewrite (B10)
- [x] 3.2 Add the `Appointment` aggregate in `Domain/Scheduling`: patient, professional, resource, appointment type, a time range of instants, status, and source. **No** `rescheduledFromId`, **no** `externalEventId`, and **no** soft-delete column (B3, B10)
- [x] 3.3 Add one creation path producing `Scheduled`, taking the professional's duration as a parameter and computing the end from it, so I1's "baked at booking" is structural rather than a rule a caller applies
- [x] 3.4 Enforce I1 in the factory: the range moves forward and its length equals the supplied duration exactly
- [x] 3.5 Enforce I2: the factory takes the fact that the professional holds the type's specialty as an explicit, checked precondition rather than trusting the caller, refusing with the violation the slice maps to `booking.specialty_mismatch`
- [x] 3.6 Enforce I3: the factory takes the resource's resource type and the appointment type's required resource type, and refuses a mismatch — so no code path, including 5b's, can persist an appointment in the wrong kind of room
- [x] 3.7 Enforce I8 in the factory against the scheduling parameters and a supplied `now`, distinguishing the lead-time violation from the horizon violation so the slice can answer with the right code
- [x] 3.8 Add `source` with the two designed values and use the self-service one, mirroring `TimeBlockSource`'s argument: the second value is designed, not speculated, and the alternative is renaming a column in 5b
- [x] 3.9 Expose a live/terminal predicate and the appointment's `BusyInterval`, so "which appointments count" is a fact on the type rather than a predicate each query re-spells — the mistake `TimeBlock.BusyIntervalsOf` was shaped to avoid
- [x] 3.10 Add **no** transition methods. Confirm by inspection that `Scheduled` is the only reachable state and that nothing can mutate the range after construction

## 4. Domain: the solver's second entry point

- [x] 4.1 Add `Explain(inputs, requestedStart)` beside `Solve`, walking the **same** private steps and returning either the resource it would assign or a typed reason it would not (B1). Refactor the existing walk so both entry points share it rather than copying it
- [x] 4.2 Make the reason type name every distinguishable cause: outside candidate hours, inside lead time, beyond horizon, overlapping a block, overlapping an appointment, no free resource. The block and appointment cases must be distinguishable, since that is the whole basis for `slot_blocked` versus `slot_taken`
- [x] 4.3 Confirm `Explain` returns the resource it selected, so the caller has no reason to choose one itself and F2's "the server assigns" has exactly one implementation
- [x] 4.4 Run the build and `DomainBoundaryTests` again: the aggregate plus this refactor is the largest single addition `Domain` has taken, and a persistence type reaching it would be caught here

## 5. Domain unit tests

- [x] 5.1 Aggregate invariants: I1 (range forward; length equals the supplied duration; a later duration change cannot reach an existing appointment because it holds its own range), I2, I3, and I8 in both directions
- [x] 5.2 The status enum: all five values distinct, creation yields `Scheduled`, and the live/terminal predicate answers correctly for each
- [x] 5.3 `Explain`'s reasons, one test per cause, including the block-versus-appointment distinction
- [x] 5.4 **The agreement property, in both directions** (B1): over generated inputs, every start `Solve` offers is admitted by `Explain` with a resource, and every start `Explain` admits appears in `Solve`'s output. This is the protection; the tests below are the documentation
- [x] 5.5 The specific lead-time and horizon agreement test the Definition of Done names: vary both parameters and assert the earliest and latest offerable start move identically on both entry points
- [x] 5.6 The half-open boundary, asserted on both entry points: a start at the exact instant a block or an appointment ends is offered and admitted
- [x] 5.7 The seam-fill cases at the domain level: an appointment in the professional's busy list removes its slots; the same appointment in a resource's busy list withholds slots only while no other room is free; the resource's turnaround applies and the professional's does not

## 6. Persistence: the range column and the three constraints

- [x] 6.1 Map `Appointment` in `SchedulingConfigurations`, snake_case and enum-as-string per convention, with `time_range` as `tstzrange` through a converter from the domain range to `NpgsqlRange<DateTimeOffset>`
- [x] 6.2 Confirm the range is **half-open** (`[)`) so it agrees with `BusyInterval`'s comparison exactly. A closed range refuses abutting appointments and silently disagrees with the read (B3)
- [x] 6.3 Answer change 4's deferred NodaTime-plugin question for the third and final time, now that a range column genuinely exists, and record the end condition rather than re-asking next change: the plugin becomes correct when a NodaTime type must be expressed **in SQL itself** (B6)
- [x] 6.4 Write the migration: the table, then `CREATE EXTENSION IF NOT EXISTS btree_gist`, then the three partial `EXCLUDE USING gist` constraints on `(professional_id, time_range)`, `(resource_id, time_range)` and `(patient_id, time_range)`, each `WHERE status = 'Scheduled'`, with **explicit names**
- [x] 6.5 Record in the migration why the predicate has one clause and the table has no soft-delete column, deviating from 02 §9's ERD: two sources of truth for "is this row live" is how an exclusion constraint becomes decorative (B3)
- [x] 6.6 Read the generated SQL and confirm three things rather than assuming them: the column is genuinely `tstzrange`, the extension is created before the constraints (the `uuid` equality operator class comes from it), and the constraint names match what task 8 maps from
- [x] 6.7 Confirm no fourth index is added by reflex — the professional exclusion constraint already provides the GiST index the window read wants
- [x] 6.8 Confirm the migration is additive on top of change 4 and that dropping the table is a clean rollback; leave `time_blocks` as two `timestamptz` columns, since nothing here issues a range query against them (B4)

## 7. The busy-interval producer: one loading step, two callers

- [x] 7.1 Extract change 4's bounded input read into one place both the availability endpoint and the booking handler call, so the read and the write cannot see different busy sets (B11)
- [x] 7.2 Add the appointment overlap read as Dapper SQL over `time_range && tstzrange(@from, @to, '[)')`, filtered to live appointments and the professionals in scope — the query Decision L was justified by
- [x] 7.3 Feed appointments into **both** lists: the professional's `BusyIntervals` and the assigned resource's `ResourceCandidate.BusyIntervals`. Record that a block contributes to one list and an appointment to two, which is the asymmetry change 4's F6 reversal had already made room for (B11)
- [x] 7.4 Confirm the availability endpoint's behaviour is unchanged except that the two lists now arrive full, and that no requirement of change 4's read had to be rewritten to accommodate them — and if one did, say so in the design rather than absorbing it

## 8. API: the booking slice

- [x] 8.1 Add `Features/Booking` with `POST /api/appointments` taking `{ appointmentTypeId, professionalId, startsAt }` and nothing else. **No `resourceId` and no `patientId` in the contract** — F2 and ownership are structural, not validated (B9)
- [x] 8.2 Restrict the endpoint to the patient role, refusing other roles `auth.forbidden`, and record that 5b widens it for the front desk with an explicit, role-gated patient field rather than by trusting a body value this change ignores
- [x] 8.3 Parse `startsAt` as a UTC instant and refuse anything else, never accepting a wall-clock label — Q4's whole point, and what keeps a fall-back day unambiguous
- [x] 8.4 Resolve the caller's own patient record from the session, and refuse an unknown or inactive appointment type or professional with `config.not_found`
- [x] 8.5 Gate on an active `DataProcessing` consent at the configured current version, refusing `auth.consent_required` (422) — closing the loop change 2 opened by making revocation possible with nothing checking it (B12)
- [x] 8.6 Open one transaction, take the professional advisory lock **first**, then load the inputs through task 7's step, then call `Explain`
- [x] 8.7 Map each `Explain` reason to its code: `booking.outside_working_hours`, `booking.lead_time_violation`, `booking.horizon_exceeded`, `booking.slot_blocked`, `booking.slot_taken`, `booking.patient_busy`, `booking.resource_unavailable`
- [x] 8.8 Check the patient's own overlap on the same loaded window, so I6 refuses with a message rather than only at the constraint
- [x] 8.9 Construct the aggregate through its factory — passing the specialty, resource-type and duration facts it checks — and insert through EF Core inside the same transaction
- [x] 8.10 Catch the exclusion violation and map **by constraint name** to `slot_taken`, `resource_unavailable` or `patient_busy`. Do not retry the resource case; record the savepoint-loop revisit trigger at the call site (B8)
- [x] 8.11 Return the created appointment: professional, appointment type, start and end instants, and status. No room — a patient does not need to know which one
- [x] 8.12 Add the API client function and response type to `packages/shared`, documenting that no resource is sent and why

## 9. The I7 and G1 retrofit into change 4's block path

- [x] 9.1 On block creation and block edit, take the same advisory lock first, then check the professional's live appointments for overlap, refusing `booking.block_overlaps_appointment` (409) with nothing stored (B7)
- [x] 9.2 Confirm the edit path leaves the stored range untouched on refusal, the same property the `block.invalid_range` path already has, since the screen relies on it to keep showing the truth
- [x] 9.3 Confirm the check is scoped to the block's own professional and uses the same half-open comparison, so a block abutting an appointment is accepted
- [x] 9.4 Confirm a terminally-stated appointment does not block a block, which is the same predicate the `EXCLUDE` uses and the same one 5b will rely on
- [x] 9.5 Note in the slice that change 4 deliberately shipped this path unlocked and unchecked because there was nothing to race, and that this change created the racer — so the retrofit is a fulfilled plan rather than a repaired oversight

## 10. Design input for the flagship

- [ ] 10.1 Extend the existing Claude Design canvas with P2, P3 and P4 artboards, including P2's five states (results, loading, empty, error, taken), against the Consult Rio tokens already in `design/_ds`
- [x] 10.2 Treat the canvas as **input**: no generated markup is shipped. The implementation uses React 19, the shared shadcn primitives, TanStack Query and react-i18next (B14)
- [x] 10.3 Confirm what the design needs that `packages/shared` does not yet have, and add only genuinely required primitives from the shadcn CLI — the bar change 2 and 3a both applied, and the reason the dialog is the only floating element in the system

## 11. P2 — the booking search

- [x] 11.1 Add `/book` to the patient portal, behind the patient session, with specialty → appointment type → professional-or-any → date window as the search
- [x] 11.2 Carry the whole search in the query string, so a reload, a bookmark, and the return from P3 all restore the results without re-entry (B14)
- [x] 11.3 Wire TanStack Query with `staleTime: 0` and refetch on focus and reconnect, honouring the API's `no-store` — the first place the library does the job it was chosen for rather than wrapping a fetch
- [x] 11.4 Group slots by day and render times in **clinic wall clock**, converted from the response's instants using the `timezone` the response carries, never the browser's zone
- [x] 11.5 Disambiguate two slots reading the same local time on a fall-back date by showing the offset on both, answering change 4's open question 4 rather than hiding one
- [x] 11.6 Build all five states as designed states: results, loading, empty as a success, error from `availability.unavailable` and `auth.rate_limited`, and **taken** — the slot removed, the reason shown, the search still on screen
- [x] 11.7 Choose and document a default window the empty state is unlikely to hit, and check the response size for a realistic window in the browser — this is the check that closes change 4's F8 revisit trigger, and the answer belongs in the validation guide's outcome

## 12. P3 and P4

- [x] 12.1 Add `/book/confirm` showing the chosen slot: professional, appointment type, start and end in clinic wall clock
- [x] 12.2 Send the booking by **instant**, professional and appointment type only, and confirm by inspecting the request that no resource id is transmitted even though the search response carried one (B9)
- [x] 12.3 Collect the contact phone when the patient's record lacks it, with the name shown for correction, and nothing else — LGPD minimization means this screen asks for what the appointment needs, not what a clinic form would (B15)
- [x] 12.4 Show the data-processing consent state and offer a grant in place when it is not active, so `auth.consent_required` is recoverable where it happened rather than by a trip to P7
- [x] 12.5 Render every booking refusal as its translated message, and route the taken case back to P2's taken state
- [x] 12.6 Add `/book/success` with the appointment summary and an onward link — to the profile for now, since P5 is 5b. Name the temporary destination in the validation guide rather than leaving a reader to find it
- [x] 12.7 Add pt-BR and en keys for every new string and for every `booking.*` code reachable here, including the two new ones, and confirm `pnpm check:i18n` passes both the consistency and the usage scan

## 13. Dev seed

- [x] 13.1 Extend the seed with a patient and one or two appointments for Dra. Helena, constructed through the aggregate factory as everything else in it is, so a fresh stack demonstrates a booked slot disappearing from availability without hand-entry — and confirm a restart still does not duplicate
- [x] 13.2 Confirm the seeded appointments do not collide with the seeded blocks, since one of them would now be refused rather than stored

## 14. Integration tests

- [x] 14.1 The happy path end to end: a patient books an offered slot, the appointment exists with the server-assigned room, and the response describes it
- [x] 14.2 **The concurrent double-book, three times over**: two simultaneous requests for one professional's slot, for the last free room across two professionals, and for one patient across two professionals. Exactly one appointment each time, and the loser gets `slot_taken`, `resource_unavailable`, `patient_busy` respectively
- [x] 14.3 The constraint without the application: write an overlapping appointment directly, bypassing the handler, and assert the database refuses it — this is the assertion that says the guarantee does not depend on the code
- [x] 14.4 The constraint names are asserted, so a rename cannot silently degrade three specific answers into `server.unexpected` (B8)
- [x] 14.5 A terminal-state appointment frees its time: a row in a cancelled state does not prevent a booking, proving the predicate 5b depends on
- [x] 14.6 **The G1 serialization test**: a booking and a colliding block creation run concurrently for one professional; exactly one succeeds and the other is refused as colliding. Confirm it fails when the lock is removed, so the test is testing the lock
- [x] 14.7 I7 in both directions: booking over a block gives `booking.slot_blocked`; creating a block over an appointment gives `booking.block_overlaps_appointment`; both abutting cases are accepted
- [x] 14.8 F2: the created appointment's room is chosen by the server; a request that attempts to carry a room is unaffected by it; and the room falls through when the first candidate is occupied
- [x] 14.9 The read/write agreement at the API level: every slot a real availability response offers is bookable in isolation, and a start the read withholds for lead time or horizon is refused by the write with the matching code
- [x] 14.10 **The seam**: book a slot, request availability again, and assert exactly the overlapping slots for that professional disappeared, that the room's occupancy withheld slots for a second qualified professional, and that the abutting slot did not disappear
- [x] 14.11 The turnaround buffer after a real appointment: slots inside the buffer are withheld for that room while the professional is offerable at the instant their appointment ends
- [x] 14.12 The authorization and consent matrix: a patient may book; professional, front-desk and administrator get `auth.forbidden`; anonymous gets `auth.session_expired`; a patient with a revoked consent gets `auth.consent_required` and succeeds after granting it
- [x] 14.13 Duration baked: book, change the professional's duration for the type, and assert the existing appointment's range is unchanged while new searches use the new duration
- [x] 14.14 The repeated confirm: the same request twice yields one appointment and a `patient_busy` refusal

## 15. Documentation

- [x] 15.1 Flip **this change's own** README status cell for increment 5 and the "N of 9" line, describing what a person can now do (`00-context.md` §8) — its own cell in its own feature commit
- [x] 15.2 Confirm the README local-run section needs **no** edit: the scheduling parameters and the consent version already exist with defaults, so this change adds no prerequisite and no new variable
- [x] 15.3 Update the two README decision rows that carry a "*(increment 5)*" promise — double-booking prevented by the database, and two data-access tools — so they read as delivered rather than planned
- [x] 15.4 Record in `04-architecture.md` §2 where Dapper actually landed and why EF still performs the insert, so Decision L reads as fulfilled precisely rather than approximately (B5)

## 16. Definition of Done

- [x] 16.1 Unit and integration tests green in CI, integration against a real PostgreSQL via Testcontainers with Respawn between tests
- [x] 16.2 pt-BR and en keys present for every new user-facing string including both new codes, `pnpm check:i18n` green
- [x] 16.3 `openspec validate booking-core --strict` passes
- [x] 16.4 The build and `DomainBoundaryTests` confirm `Domain` gained an aggregate and a solver entry point, and that Dapper did not arrive with them (2.1, 4.4)
- [x] 16.5 The demonstrable behaviour works end to end: a patient searches on P2, books on P3, sees P4, and that slot is no longer offered
- [x] 16.6 `validation.md` **run** against the local Compose stack with the real Google client, both locales, and its Outcome section recorded — including a plain statement of what was not examined, per the standard 3b set. The change is not done until the guide has been executed (`00-context.md` §9)
- [x] 16.7 Change archived into the living spec, creating the `booking` capability and folding the `availability` modifications into it
