## 0. Carried debt, closed before new consumers make it harder to attribute

- [ ] 0.1 **STILL OPEN — carried to `booking-desk`.** Capture change 4's **F8 response-size number** against P2 as it exists today — payload size and duration for a realistic window with "any professional" — and record it in `2026-08-23-availability-read/design.md`'s F8 note or in this change's validation Outcome. P2 is the one consumer right now; P6 makes it two, and after that no figure can be attributed to a single screen
- [ ] 0.2 **STILL OPEN — carried to `booking-desk`.** Confirm whether `availability-read`'s validation guide was run against the now-configured Google client, and record the answer in **its own** Outcome section. `booking-core`'s guide asked for it and its Outcome recorded that the result is unknown; leaving it unknown a second time turns a deferral into a habit

## 1. The catalogue, and the one row that is now wrong

- [x] 1.1 Confirm by inspection that **no new error code is needed**: `booking.cutoff_passed` (422), `booking.appointment_not_found` (404) and `auth.ownership_denied` (403) are all already catalogued. Record this in the change as a contrast with 5a, which added two — the catalogue was written ahead and is being consumed
- [x] 1.2 Annotate `booking.appointment_not_found` in `07-error-codes.md`: it says "unknown / **soft-deleted** appointment", and 5a deliberately gave `appointments` no soft-delete column (its design B3 — status *is* the history). Correct it to describe an unknown appointment on a path whose caller is entitled to distinguish absence from denial, and note that **no patient path uses it** (C6)
- [x] 1.3 Annotate `booking.cutoff_passed` with the fact that `booking-lifecycle` is its first use and that the front-desk override which makes it conditional is `booking-desk`'s, so a reader does not expect the override here
- [x] 1.4 Add `ErrorCodes.CutoffPassed` and `ErrorCodes.AppointmentNotFound` constants, each carrying its argument in the doc comment in the established voice. Note at `AppointmentNotFound` that it has no caller yet and why (C6), so its absence from the handler is deliberate rather than forgotten
- [x] 1.5 **Flagged for review — the brief said no new codes and it needed one.** Add `booking.appointment_not_changeable` (409) to `07-error-codes.md` and to `ErrorCodes`: a patient cancelling an already-terminal appointment (two tabs, or the back button) had no honest answer. `appointment_not_found` denies a row visible on P5; `auth.ownership_denied` is about who and the patient owns it; `cutoff_passed` gives a time reason for a state refusal — the confusion `slot_blocked` exists to avoid. Same terms as 5a's `patient_busy`: overloading instead is one mapping line and one i18n pair
- [x] 1.6 Correct `proposal.md` and `design.md` to say so, rather than leaving an approved artifact claiming something the code contradicts

## 2. Configuration: the cutoff, and where it deliberately does not go

- [x] 2.1 Add the cancellation cutoff as configuration (`Scheduling__CancellationCutoffHours`, default 24 per domain-model F3), bound and validated at startup like the other scheduling numbers — a non-positive value fails startup rather than silently disabling the rule
- [x] 2.2 **Do not add it to `SchedulingParameters`**, and record why at the point a reader would expect to find it: those three numbers are handed to the solver on both the read and the write so the two cannot diverge, and the cutoff is not about what may be offered (C4). A comment on `SchedulingParameters` naming the cutoff's absence is cheaper than the bug where somebody applies it in the solver
- [x] 2.3 Add the optional line to `.env.example` with its default, and confirm the README local-run section needs **no** edit — the default means this change adds no prerequisite, the same property 5a confirmed for itself

## 3. Domain: the two transitions

- [x] 3.1 Add `rescheduledFromId` to `Appointment` as a nullable reference, set only on an appointment created by a reschedule. Record that 5a declined to create it and why that was right then and is not now: the producer exists (C5)
- [x] 3.2 Add the cutoff rule as a domain concern taking the appointment's start, a supplied `now`, the configured cutoff, and a `cutoffApplies` **fact** — not a role, not a session, not an enum (C4). `Domain` has no notion of `Role` and the compiler will keep it that way
- [x] 3.3 Add `Cancel(...)`: refuses a terminal appointment, applies the cutoff rule, moves to `Cancelled`. **No** other precondition — cancelling needs nothing the appointment does not already hold
- [x] 3.4 Add `RescheduleTo(...)`: refuses a terminal appointment, applies the cutoff rule, moves the original to `Rescheduled` and returns the **new** `Scheduled` appointment carrying the link. The new appointment goes through the same construction path `Book` uses, so I1, I2, I3 and I8 are enforced against the new time by the code that already enforces them and not by a copy (C1)
- [x] 3.5 Confirm `RescheduleTo` bakes the duration **in force now**, not the original's, so a duration edited between the booking and the reschedule reaches the new appointment and cannot reach the old one — I1 in the one situation that distinguishes the two readings
- [x] 3.6 Add **no** `Complete()` and **no** `MarkNoShow()`. Confirm by inspection that `Completed` and `NoShow` remain unreachable, so the unreachable half of the state machine is visible by absence rather than by reading a table (C1)
- [x] 3.7 Confirm the original appointment's `Range` is unreachable for mutation from either transition — a reschedule creates, it never moves (C5)
- [x] 3.8 Run the build and `DomainBoundaryTests`: this change adds a rule that is *about* authority, and the boundary guard is what stops the temptation to reach for a session type to express it

## 4. Domain unit tests

- [x] 4.1 Both transitions from `Scheduled`: the resulting status, and for reschedule the link, the new range, and the original's range unchanged
- [x] 4.2 Both transitions refused from each of the four terminal states, so a double cancel and a cancel-after-reschedule are answers rather than writes
- [x] 4.3 The cutoff rule in **both** directions of `cutoffApplies` (C4): with the fact true, inside the cutoff refuses and outside succeeds; with it false, inside the cutoff succeeds. The `false` direction has no caller until 5c and is tested now, because it is the specification of what 5c wires up
- [x] 4.4 The cutoff boundary: an appointment starting exactly the cutoff away is changeable — a minimum notice, not an exclusive bound
- [x] 4.5 The new appointment's invariants against the **new** time: lead time and horizon (I8) measured from the reschedule's `now`, qualification (I2), resource type (I3)
- [x] 4.6 Duration re-baked: reschedule with a different duration in force and assert the new range uses it while the original's is untouched (3.5)
- [x] 4.7 A rescheduled appointment rescheduled again: the chain links to the appointment directly replaced, and nothing collapses it (C5)

## 5. Persistence

- [x] 5.1 Map `rescheduledFromId` in `SchedulingConfigurations` as a nullable self-reference, snake_case per convention
- [x] 5.2 Write the migration: add `rescheduled_from_id uuid NULL` with its FK and index. State in the migration that the three exclusion constraints are **deliberately untouched** — their predicate is already `status = 'Scheduled'` and both new states fall outside it, which is the payoff of 5a's B10 and the kind of non-change a reader will otherwise stop and check
- [x] 5.3 Confirm the FK's index is not redundant with an exclusion constraint's GiST index — unlike 5a's task 6.7 this is an equality lookup on a different column, so the fourth index earns its place
- [x] 5.4 Confirm the migration is additive on top of `BookingCore` and that dropping the column is a clean rollback with no existing non-null value
- [x] 5.5 Read the generated SQL rather than assuming it: the column is nullable, and no constraint was silently recreated

## 6. API: cancel

- [x] 6.1 Add the cancel endpoint to the `Booking` slice, restricted to the patient role, refusing other roles `auth.forbidden` and an anonymous caller `auth.session_expired`
- [x] 6.2 Resolve the caller's own patient record from the session and apply the ownership rule: another patient's appointment **and** an unknown id both answer `auth.ownership_denied` (403), so the endpoint cannot be used to enumerate appointment ids. Record the precedent being followed — the catalogue's own note on `patient.not_found` (C6)
- [x] 6.3 Open a transaction and load the appointment **`FOR UPDATE`** (or transition by conditional `UPDATE ... WHERE status = 'Scheduled'` and check the row count), so two concurrent changes to one appointment cannot both pass the aggregate's guard against the same snapshot (C8)
- [x] 6.4 Take **no** advisory lock, and say why at the call site: the domain-model G1 lock serializes a cross-table read-then-write, and cancel reads nothing and can create no overlap — it only removes a row from three partial indexes (C8). A lock here would be cargo-cult
- [x] 6.5 Do **not** gate on data-processing consent, and record the reason where a reader would expect the gate 5a built: refusing to let somebody leave because they withdrew consent to processing traps them as a consequence of exercising a right (C11)
- [x] 6.6 Apply the cutoff, passing `cutoffApplies: true` unconditionally on this path, and map the refusal to `booking.cutoff_passed` (422). Note that the parameter exists for 5c and that this path will not be the one that changes
- [x] 6.7 Map the already-terminal refusal to its code and confirm nothing is written on any refusal

## 7. API: reschedule

- [x] 7.1 Add the reschedule endpoint taking the appointment id and the **new start instant only** — no professional, no appointment type, no resource id. The scope restriction is then structural rather than validated, the same shape that made F2 structural in 5a (C3)
- [x] 7.2 Apply the same role and ownership rules as cancel (6.1, 6.2)
- [x] 7.3 Gate on an active `DataProcessing` consent at the configured version, refusing `auth.consent_required` (422) — a reschedule creates an appointment, and this is the difference from cancel (C11)
- [x] 7.4 Open one transaction, take the professional advisory lock **first** (reschedule inserts, so it races the block path exactly as booking does), then load the appointment `FOR UPDATE`, then load inputs through the existing `ScheduleReader`
- [x] 7.5 **Filter the appointment being moved out of the loaded busy set by id**, before the solver sees it — at load time it is still `Scheduled`, so a near move would otherwise be told the patient's own outgoing appointment blocks them (C7). Filter the loading step's output; do **not** change `ScheduleReader`, so the booking path is untouched
- [x] 7.6 Call `AvailabilitySolver.Explain` for the requested instant and map each reason to the same code 5a mapped it to. Write no second validation: read/write agreement is structural because there is one solver (C7, 5a's B1)
- [x] 7.7 Apply the transitions in the **load-bearing order** (C2): `UPDATE` the original to `Rescheduled` **first**, so it leaves the partial exclusion indexes, **then** insert the new appointment. Put a comment at the two statements stating that the order is a correctness property, that the constraints are non-deferrable, and that reversing it always fails a same-patient near move — the comment is the only thing at the call site that says so
- [x] 7.8 Catch the exclusion violation and map by constraint name as 5a does, so a reschedule losing a race gets `slot_taken`, `resource_unavailable` or `patient_busy` and the original stays scheduled
- [x] 7.9 Return the new appointment in the same shape a booking returns — professional, appointment type, start, end, status, and no room
- [x] 7.10 Confirm the whole unit is one transaction: a refusal at any point leaves the original scheduled and creates nothing (spec: "A reschedule leaves either both appointments or neither")

## 8. API: the patient's appointment list

- [x] 8.1 Add the list endpoint returning the caller's own appointments only, separated into upcoming and past by **time**, with terminal ones carried in the past list annotated with their status — the working answer to design Open Question 1, to be confirmed by the validation guide
- [x] 8.2 Include per appointment: professional (the derived display label 5a introduced, unchanged here — the real field is 5c's P-5), appointment type, start and end instants, status, and **`canChange`** computed by the server
- [x] 8.3 Compute `canChange` server-side from the cutoff and the status, and record why it is not the cutoff duration for the client to apply: the browser's clock is not the clinic's and is user-settable, which is the bug 5a's validation check 10 exists to catch (C10)
- [x] 8.4 Confirm the response carries the clinic `timezone` the availability response already carries, so the screen has one source for wall-clock conversion
- [x] 8.5 Add the API client functions and types to `packages/shared` for the list, the cancel and the reschedule, documenting that the reschedule carries only an instant and why

## 9. P5 — My appointments

- [x] 9.1 Add `/appointments` to the patient portal behind the patient session, with upcoming and past lists, times in **clinic wall clock** converted from the response's instants using the response's `timezone`
- [x] 9.2 Render an unchangeable appointment with its reschedule and cancel actions **disabled and explained** — a translated message naming reception — driven by the server's `canChange` and never by a local clock comparison (C10)
- [x] 9.3 Wire TanStack Query so the list refetches on focus and reconnect, honouring the API's `no-store`: an appointment cancelled in another tab, or a cutoff that has since passed, should not persist on screen
- [x] 9.4 Require an explicit confirmation before cancelling, and show the resulting state without a full reload
- [x] 9.5 Handle `booking.cutoff_passed` arriving on a screen that had shown the action as available — the window closed while the patient read it — as a translated message plus a refreshed list, not as a dead end
- [x] 9.6 Repoint P4's onward link from the profile to `/appointments`, closing the temporary destination 5a's task 12.6 named in its validation guide

## 10. P6 — Reschedule

- [x] 10.1 Add `/appointments/:id/reschedule`, **reusing P2's search components** rather than copying them, scoped to the appointment's own professional and appointment type with no control to change either (C3)
- [x] 10.2 Show what is being moved — the current time in clinic wall clock — beside the new time being chosen, so the patient can see both ends of the change before committing
- [x] 10.3 Send the reschedule by **instant only** and confirm by inspecting the request that no professional, appointment type or resource id is transmitted (7.1)
- [x] 10.4 Render every refusal as its translated message, routing the taken case back to the search's taken state as P2/P3 already do
- [x] 10.5 On success, land on the appointment list showing the new time — not on a separate success screen, since P4's role is to reassure a first-time booker and a reschedule already has a list to return to. Record the deviation from a strict P4-symmetry reading of `06 §4`
- [x] 10.6 Add pt-BR and en keys for every new string and every code reachable here including `booking.cutoff_passed`, and confirm `pnpm check:i18n` passes both the consistency and the usage scan

## 11. Dev seed

- [x] 11.1 Extend the seed so a fresh stack demonstrates the lifecycle without hand-entry: the seeded patient gets an appointment **outside** the cutoff and one **inside** it, so P5's changeable and locked states are both visible on first load. Confirm a restart still does not duplicate
- [x] 11.2 Confirm the seeded appointments still avoid the seeded blocks (5a's task 13.2) and that the inside-cutoff one is placed relative to `now` rather than at a fixed date, or it stops being inside the cutoff the day after it is written

## 12. Integration tests

- [x] 12.1 Cancel end to end: a patient cancels, the row is terminal with its range intact, and nothing is deleted
- [x] 12.2 **The availability round trip**: book, cancel, request availability again, and assert the slot and its overlapping neighbours are offered again — and that the room is released for a second qualified professional. This is the assertion the partial exclusion predicate was written for in 5a and could only be proved there by a hand-written row
- [x] 12.3 **The reschedule near move** — the test this change exists to get right (C2): move an appointment by a few minutes with the same professional and assert it succeeds. Add a comment stating that the small delta **is** the assertion: a far move passes under the wrong statement ordering and under a missing busy-set filter, so tidying this fixture into "next week" silently deletes the coverage
- [x] 12.4 A far reschedule as well, so the near test's failure can be localized: if the far one passes and the near one fails, the fault is the ordering or the filter and not the transition
- [x] 12.5 The reschedule's atomicity: a refused new time leaves the original scheduled with its original range and creates nothing
- [x] 12.6 **The same-appointment race** (C8): two simultaneous cancels yield exactly one cancellation; a simultaneous cancel and reschedule yield exactly one outcome, and if the cancel wins no replacement exists. Confirm the test **fails when the row lock is removed**, so the test is testing the lock
- [x] 12.7 The G1 serialization for the reschedule path: a reschedule and a colliding block creation run concurrently for one professional, exactly one succeeds
- [x] 12.8 The cutoff at the API: inside the cutoff a cancel and a reschedule both give `booking.cutoff_passed` (422) and write nothing; at the boundary both succeed
- [x] 12.9 The ownership matrix: a patient may change their own; another patient's appointment gives `auth.ownership_denied`; an unknown id gives **the same** `auth.ownership_denied`, asserted explicitly so the non-enumerability is a test and not a comment; professional, front-desk and administrator get `auth.forbidden`; anonymous gets `auth.session_expired`
- [x] 12.10 The consent asymmetry: a revoked consent refuses a reschedule with `auth.consent_required` and **permits** a cancel (C11)
- [x] 12.11 Terminal-state guards: cancelling a cancelled appointment, rescheduling a rescheduled one, and cancelling a rescheduled one are all refused with nothing written
- [x] 12.12 The reschedule chain: reschedule twice and assert each new appointment links to the one it directly replaced and that all three rows exist
- [x] 12.13 Duration re-baked at reschedule: change the professional's duration for the type between the booking and the reschedule, and assert the new appointment uses the new duration while the original's range is untouched
- [x] 12.14 The list endpoint: only the caller's own appointments; terminal ones present with their status; `canChange` true outside the cutoff and false inside it and false for a terminal appointment
- [x] 12.15 A terminal appointment no longer blocks an internal block, closing the I7 direction against a state that is now reachable through the product rather than only by a direct write (5a's task 14.5 proved the predicate; this proves the path)

## 13. Documentation

- [x] 13.1 Flip **this change's own** README status cell for increment 5b and the "N of 10" line, describing what a person can now do (`00-context.md` §8) — its own cell in its own feature commit, 1–3 lines and no new section
- [x] 13.2 Confirm the README local-run section needs **no** edit (2.3): the cutoff has a default, so this change adds no prerequisite and no required variable
- [x] 13.3 Record in `04-architecture.md` that the reschedule is the first handler whose statement **order** is a correctness property, and that it is documented in `02 §5` rather than only in code — so the fact outlives this implementation
- [x] 13.4 Confirm `02-domain-model.md` §5 and `06 §P6` already carry the same-professional scope, the statement ordering and the calendar seam, and that nothing implemented here contradicts them. If something does, correct the document rather than absorbing the divergence

## 14. Definition of Done

- [x] 14.1 Unit and integration tests green in CI, integration against a real PostgreSQL via Testcontainers with Respawn between tests
- [x] 14.2 pt-BR and en keys present for every new user-facing string including `booking.cutoff_passed`, `pnpm check:i18n` green
- [x] 14.3 `openspec validate booking-lifecycle --strict` passes
- [x] 14.4 The build and `DomainBoundaryTests` confirm the aggregate gained two transitions and that no session or role type reached `Domain`
- [x] 14.5 The demonstrable behaviour works end to end: a patient cancels an appointment on P5 and the slot is offered again on P2; a patient reschedules on P6 by a few minutes and it succeeds
- [x] 14.6 The cutoff is visible before it is hit: an appointment inside the cutoff shows disabled actions with a translated explanation, in both locales
- [x] 14.7 `validation.md` **run** against the local Compose stack with the real Google client, both locales, and its Outcome section recorded — including a plain statement of what was not examined, per the standard 3b set and 5a's. The change is not done until the guide has been executed (`00-context.md` §9). Design Open Question 1 (where terminal appointments belong in the list) is answered there by a human
- [x] 14.8 The two carried debts in group 0 are closed, or their remaining state is written down explicitly rather than left unmentioned a second time
- [ ] 14.9 Change archived into the living spec, folding the `booking` additions and the one modified requirement into it
