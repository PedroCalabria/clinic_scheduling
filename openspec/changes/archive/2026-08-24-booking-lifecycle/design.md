## Context

`booking-core` built an aggregate with one reachable state, three exclusion constraints whose partial
predicate anticipates four more, a professional-scoped advisory lock, and a shared loading step feeding
both the read and the write. It then wrote, in the aggregate's own doc comment, that the transitions
"belong to 5b with their own guards and their own tests". This change is that.

So the shape of the work is unusual for this project: almost nothing here is new infrastructure. There
is no new dependency, no new data-access technology, no new lock, no new constraint. What there is
instead is a set of **ordering and authority decisions** on top of machinery that already exists — and
one of them (C2) is a correctness property that no amount of code review catches and that the obvious
test does not exercise.

Decision ids below are `C1…`. The namespace collides deliberately, as 5a's `B` did: `02-domain-model.md`
carries locked decisions A–G, `03-nfr.md` G–J, `04-architecture.md` K–V, `05-openspec-workflow.md`
W–Y. Wherever one of those is meant it is named with its document (*domain-model F3*, *domain-model
G1*), never by bare letter.

## Goals / Non-Goals

**Goals:**

- Make the state machine in `02 §3` reachable from the product for the two states a patient owns, and
  refuse every transition the machine does not draw.
- Get the reschedule statement ordering right, and — more importantly — get it *tested by something
  that would fail if it were wrong*.
- Deliver domain-model F3's patient half in a shape that does not have to be rewritten when
  `booking-desk` adds the front-desk half.
- Close the round trip the partial exclusion predicate was written for, through the API rather than
  through a hand-written row.
- Give a patient the rule *before* they hit it: P5 shows a cutoff-locked appointment as locked, not as
  a button that fails.

**Non-Goals:**

- `Completed` and `NoShow`. They are front-desk observations about an appointment that has already
  happened, and the surface that records them (S4) is `booking-desk`'s.
- The front-desk cutoff **override**. The parameter is built here; the caller that passes `false` is
  5c's. See C4 for why the parameter is nonetheless built now.
- Booking on behalf, `Professional.fullName`, `AppointmentSource.FrontDesk`, `AccessLog` on staff PII
  reads, S1/S4/S5 — `booking-desk`.
- Cross-professional reschedule. Ruled **out** (C3), not deferred.
- Calendar propagation (C9). Change 6.
- Any change to the solver, to `AvailabilityInputs`, to the block path, or to how a session or a role
  is issued.

## Decisions

### C1 — Two named transition methods, not one `TransitionTo(status)`

`Appointment` gains `Cancel(...)` and `RescheduleTo(...)`, each with its own preconditions, rather than
a single method taking a target state and a table of legal edges.

*Why.* The two transitions do not share preconditions beyond "the appointment is live". Cancel needs
the cutoff and nothing else. Reschedule needs the cutoff, plus everything `Book` already checks about
the *new* time — the professional still qualified, the room still of the right type, lead time and
horizon against the new start. A generic method would take the union of both parameter sets and ignore
half of them per call, which is the shape that lets a caller pass the wrong half and be told nothing.

Named methods also make the *unreachable* states visible by absence: there is no `Complete()` in this
change, so `Completed` is unreachable by inspection rather than by reading a transition table.

*Alternatives.* (a) A state-machine table with a generic mover — buys nothing at five states and two
edges, and costs the per-transition preconditions. (b) Setter on `Status` plus handler-side guards —
exactly the design the aggregate exists to prevent; 5c would then reintroduce the bug on a second
write path, which is the failure mode 5a's own doc comment names.

### C2 — Reschedule updates the old row to `Rescheduled` **before** inserting the new one

This is the load-bearing decision of the change, and it is recorded in `02-domain-model.md` §5 as a
domain fact rather than only here, because it survives this change's implementation.

The three exclusion constraints are `EXCLUDE USING gist ... WHERE (status = 'Scheduled')` and were
created **without `DEFERRABLE`**, so PostgreSQL evaluates them at the end of each statement rather than
at commit. Inside the reschedule transaction:

```
   CORRECT                                   WRONG
   BEGIN                                     BEGIN
   pg_advisory_xact_lock(professional)       pg_advisory_xact_lock(professional)
   UPDATE old SET status='Rescheduled'       INSERT new
     -> old leaves the partial index           -> old is still 'Scheduled'
   INSERT new                                  -> appointments_patient_no_overlap FIRES
     -> checked against live rows only       UPDATE old ...   (never reached)
   COMMIT                                    ROLLBACK
```

The wrong order fails **always** for a same-patient near move — the patient constraint sees the old
row — and additionally fires the professional constraint whenever the ranges overlap.

*Why this needs writing down rather than just doing.* The natural test moves an appointment from
Tuesday to Thursday. Under the wrong ordering that test **passes**, because the old and new ranges do
not overlap and the exclusion constraints are overlap constraints. The bug is only reachable when the
two ranges touch.

**Amended during implementation — the first mitigation did not work, and that was found by trying to
break it.** The plan was for the near-move integration test to fail if the handler ordered its
statements wrongly. It does not. Reversing the handler's two `SaveChanges` calls leaves **every test
green**, because EF Core does not emit statements in the order the code calls them: it builds a command
batch and orders it itself, and for a self-referencing insert whose foreign key points at the row being
updated it emits the `UPDATE` first regardless.

That is a worse position than it looks. The handler would have been correct *by accident* — protected
by an EF implementation detail that survives until an upgrade, a batching-configuration change, or
somebody restructuring the call. Two things follow, and both are implemented:

1. **The handler pins the order explicitly**, with two `SaveChanges` calls inside one transaction
   rather than one call that EF is trusted to order. The comment at those statements says why.
2. **The rule is asserted where it is actually true** — against raw SQL, in
   `RescheduleOrderingTests`: the wrong order raises `23P01`, the right order commits, and a *far*
   move survives the wrong order, which is the assertion that keeps the near-move fixture honest.

The near-move test in `AppointmentLifecycleTests` still earns its place — it is what proves the
busy-set filter (C7) — but its comment now says what it does and does not cover, rather than claiming
coverage it never had.

One further detail the SQL test surfaced: a near move violates **all three** constraints at once — same
patient, same professional, same room — and PostgreSQL reports whichever it checks first. So the test
asserts the set rather than one name, which is the same conclusion `booking-core` reached for its
concurrency assertions.

*Alternatives.* (a) Declare the constraints `DEFERRABLE INITIALLY DEFERRED`, making order irrelevant.
Rejected: it weakens all three constraints on **every** path, including the hot booking path, to buy
freedom on one rare path — and a deferred violation surfaces at `COMMIT` where the handler has already
lost the context to map it to a friendly code. The mapping-by-constraint-name that 5a built (B8) still
works, but it would fire from a different place. (b) `SET CONSTRAINTS ... DEFERRED` for this
transaction only. Rejected as the same weakening with a narrower blast radius and an extra statement
whose absence is silent. (c) Delete the old row and insert the new one. Rejected outright: I10 and the
audit trail (C5). (d) Mutate the old row's range in place. Same rejection, plus it destroys the
history that `rescheduledFromId` exists to keep.

### C3 — Reschedule is scoped to the same professional and appointment type

A patient rescheduling picks a new time from P2's search **restricted to the professional and the
appointment type they already have**. Choosing a different professional is a cancel followed by a new
booking, through the paths that already exist.

*Why, in order of weight.* First, it is what the thing means: an appointment is a commitment with a
particular person, and swapping the person is a different commitment, not a new time for the same one.
Rescheduling across professionals would also have to re-run I2's qualification check against someone
the patient never chose from a list.

Second, and usefully: the professional-scoped advisory lock stays **single-keyed**. A
cross-professional reschedule would need the old professional's lock and the new one's, and two
transactions acquiring `{A,B}` and `{B,A}` deadlock. The fix is trivial — take them in ascending key
order — but it is a fix for a problem this change simply does not have. `ScheduleMutation.LockKey`
stays as 5a wrote it, and its documented "collisions are harmless" property stays true without
needing a second reading.

*Alternatives.* (a) Allow it, order the locks. Correct and cheap, and rejected only because the
capability is not wanted: it would be lock machinery serving a feature nobody asked for. (b) Allow it
by internally doing cancel-then-book in one transaction — the same two-lock problem wearing a
different name.

### C4 — The cutoff is an authority fact passed to the domain, and does not join `SchedulingParameters`

Two separate choices, both about where a rule lives.

**It is not a scheduling parameter.** `SchedulingParameters` holds slot step, minimum lead time and
horizon, and it is handed to the solver on *both* the read and the write precisely so the two cannot
apply different rules. The cutoff is not that kind of number: it does not decide what may be offered,
only who may undo. Putting it in that record would hand the solver a field it must be trusted to
ignore — and the moment somebody "helpfully" applies it, availability starts hiding slots for a reason
that has nothing to do with whether they are free. It is configured alongside them
(`Scheduling__CancellationCutoffHours`, default 24 per domain-model F3) and carried as its own value.

**The domain is told a fact, not a role.** `Cancel` and `RescheduleTo` take `cutoffApplies` — the
established bargain from 5a's `AppointmentBooking.ProfessionalHoldsDurationForType`, where the
aggregate is handed conclusions the caller has already established rather than reaching for a lookup.
`Domain` has no notion of `Role`, no session, and no way to acquire one without breaking the boundary
the compiler enforces.

*Why build the parameter now when only one value is ever passed.* Because the alternative is a method
signature change in 5c, and a signature change is where a caller quietly keeps the old behaviour. It
also makes the rule unit-testable in **both** directions today: a test that passes `false` and asserts
the transition succeeds inside the cutoff is the specification of what 5c will wire up, written while
the reasoning is fresh. The parameter is not speculation — the override is named in `02 §5`, has a
change that owns it, and has a screen (S5) in `06 §3`.

*Alternatives.* (a) A `CancellationAuthority` enum (`Patient` / `FrontDesk`). Rejected: it puts a role
vocabulary inside `Domain` for a distinction that is genuinely boolean at the point of use, and it
would need a third value the day a professional cancels their own. (b) Enforce the cutoff only in the
handler. Rejected — it is a domain rule with a documented default, and the aggregate is the layer 5c's
second write path will also go through.

### C5 — `Rescheduled` spawns a linked row; it does not move a range

`rescheduledFromId` is added as a nullable self-reference on `appointments`. The old row keeps its
original range and becomes terminal; the new row carries the link.

*Why.* `02 §3` says so, and the reason is audit and LGPD: "this appointment was moved from 09:00 to
14:00 on the 3rd" is reconstructible only if the 09:00 row still exists with its range intact. It also
keeps I1 honest — the new appointment's duration is baked from the duration *in force now*, which may
differ from the original's, and a moved range would silently keep the old one.

**The column belongs here and not in 5a** for the reason 5a gave: no column for a producer that does
not exist. It exists now.

*The chain is allowed to grow.* A → B → C is a legitimate history and nothing collapses it. The link
is not walked by any query in this change; it is written so that it is there when something needs it.

*Alternatives.* (a) A separate `appointment_history` table. Rejected as a second source of truth for a
fact the status column already carries — the same argument 5a used to refuse a soft-delete column
(B3). (b) Move the range in place and record nothing. Rejected: violates the spirit of I10 and
destroys the audit trail.

### C6 — A patient asking about an appointment that is not theirs is told `auth.ownership_denied`, never `booking.appointment_not_found`

The catalogue already settled this shape for a different entity, and the reasoning is quoted rather
than re-derived: `07-error-codes.md` says of `patient.not_found` that a patient asking for a record
that is not theirs "never sees this — they get `auth.ownership_denied`, so the response cannot be used
to discover which records exist."

Applied here: on a patient path, a **non-existent id** and **someone else's appointment** produce the
same 403 `auth.ownership_denied`. A patient cannot enumerate appointment ids and learn which ones are
real. `booking.appointment_not_found` (404) therefore has **no caller on a patient path in this
change** — it is catalogued for the staff paths in 5c, where the actor is entitled to know whether a
record exists.

*This makes one catalogue row inaccurate*, and it is corrected rather than tolerated:
`booking.appointment_not_found` is described as "unknown / **soft-deleted** appointment", and 5a's
design B3 deliberately gave `appointments` no soft-delete column. The row is annotated to say
"unknown appointment, on a path whose caller is entitled to distinguish absence from denial".

*Alternatives.* (a) 404 for unknown, 403 for not-yours. The conventional REST answer, and an
enumeration oracle. (b) 404 for both. Hides the denial from a patient who legitimately owns the
appointment but hit the cutoff, and contradicts the established precedent for no gain.

### C7 — Reschedule validates the new time through `Explain`, exactly as booking does

The reschedule handler does not re-implement "is this slot offerable". It takes the lock, loads inputs
through `ScheduleReader`, calls `AvailabilitySolver.Explain` for the requested instant, and maps the
same reasons to the same codes 5a mapped them to. Everything 5a's B1 argued about read/write agreement
applies unchanged to a third caller.

**One subtlety the shared path handles for free, and one it does not.** The busy set loaded for the
new time still contains the *old* appointment, because at load time it is still `Scheduled`. For a near
move that is exactly wrong — the patient would be told their own appointment blocks them. So the old
appointment is excluded from the busy set by id before the solver sees it.

**Amended during implementation.** This decision originally said the exclusion would be a filter on the
loading step's *output*, leaving `ScheduleReader` untouched. That is not possible: a `BusyInterval`
carries a start, an end and a cause and deliberately **no identity** — which is precisely what lets
blocks and appointments share one list (change 4's F5). There is nothing in the output to filter *by*,
and matching on the range would be guessing. So the exclusion is an optional `excludingAppointmentId`
parameter threaded into the two queries where the row can still be named, defaulting to null. The
booking path passes nothing and is behaviourally untouched, which was the real intent of the original
note; what changed is where the filter sits, not what it does.

That exclusion is also *why* the near-move test is the right test twice over: it is the only case that
exercises both the statement ordering (C2) and this filter, and both failures look identical from the
outside — a refusal that should have been a success.

*Alternative.* Load the busy set after the UPDATE, so the old row is already terminal and no filter is
needed. Genuinely tempting and rejected: it means validating the new time only after having destroyed
the old appointment, so a refusal has to roll back a transaction that already mutated state. Correct
but harder to reason about, and it puts the diagnostic step after the destructive one.

### C8 — Cancel takes no advisory lock, but every transition takes a row lock

**No advisory lock on cancel.** The domain-model G1 lock exists to serialize a read-then-write across
two tables — booking reads blocks, block creation reads appointments. Cancel reads nothing and can
create no overlap: it only *removes* a row from three partial indexes. Taking the lock would be
cargo-cult serialization on a path that cannot race the thing the lock protects.

**A row lock on both.** There is a real race, and it is not the one the advisory lock covers: two
concurrent requests against the *same appointment* — a cancel and a reschedule, or two cancels. Both
read `Scheduled`, both pass the guard, and the reschedule commits a new appointment while the cancel
commits the cancellation. The patient cancelled and ended up with an appointment. Nothing in the
schema prevents this: the constraints police overlap between rows, not the lifecycle of one row.

So the appointment is loaded `FOR UPDATE` (or transitioned with a conditional `UPDATE ... WHERE
status = 'Scheduled'` whose row count is checked) inside the transaction. The loser gets the
already-terminal refusal, which is the same answer a double cancel gets. **Reschedule keeps the
advisory lock as well**, because it inserts and therefore does race the block path exactly as booking
does.

*Alternative.* Rely on the aggregate's in-memory guard. That is the bug: the guard is correct and
evaluated against a snapshot two transactions both hold.

### C9 — Cancel releases the slot internally and propagates nowhere, and that is written down

`06 §P6` says cancel "releases slot + resource and propagates to the external calendar"; `02 §5` says
the Google event is removed on cancel and replaced on reschedule. This change delivers the release and
**not** the propagation: there is no outbox, no `externalEventId`, and no professional with a connected
calendar, because `calendar-outbound` is change 6.

Recorded as a decision rather than left as an omission, per this project's habit of naming seams (the
same treatment change 4 gave the empty appointment producer). The seam is clean: propagation reacts to
a transition that will already have happened, so change 6 adds a producer to an event that exists
rather than reopening this handler's logic.

### C10 — P5 receives the cutoff decision from the server; it does not compute it

Each appointment in the list response carries a boolean saying whether the patient may still change it
(and the appointment's start, so the screen can say *when* the window closed). The browser does not
compare `startsAt` to a local clock.

*Why.* The browser's clock is not the clinic's, is user-settable, and is exactly the class of bug
`booking-core`'s validation check 10 exists to catch. A screen that computes the cutoff locally can
show an enabled button the server will refuse, or a locked one it would have accepted — and the whole
point of P5 showing the rule is that the shown rule is the enforced rule. The server is the authority
and says so in the payload.

The refusal still exists and is still handled: a patient can sit on P5 while the window closes.
`booking.cutoff_passed` renders as a translated message and the list refreshes.

*Alternative.* Send the cutoff duration and let the client compute. Rejected — it moves the clinic's
policy into a browser with a wrong clock, for one fewer field.

### C11 — Rescheduling re-checks the data-processing consent; cancelling does not

Reschedule creates an appointment, so it passes through the same `auth.consent_required` gate 5a built:
a patient whose consent is revoked cannot acquire new processed data.

Cancel does not. Refusing to let someone *withdraw* from a service because they withdrew consent to
data processing is the wrong way round — it would trap a patient in an appointment as a consequence of
exercising a right, which is precisely what LGPD's minimization and revocability posture (02 §8) is
about. Cancel reduces what the clinic holds.

### C12 — `booking.appointment_not_changeable` (409), added against the brief and flagged

*Found during implementation, recorded here rather than discovered by a reviewer.* The proposal
claimed this change would add no error code. It needed one: a patient cancelling an appointment that
is already terminal — P5 open in two tabs, or a cancel followed by the back button — had no honest
answer in the catalogue.

*Why each existing candidate is wrong rather than merely imperfect.*
`booking.appointment_not_found` (404) would deny the existence of a row the patient is looking at,
and C6 has already spent that code's meaning on a different job. `auth.ownership_denied` (403) is
about *who* is asking, and the patient owns this appointment — using it would also collide with C6's
enumeration defence, making "yours but cancelled" indistinguishable from "not yours".
`booking.cutoff_passed` (422) would give a **time-based reason for a state-based refusal**, which is
exactly the confusion `booking.slot_blocked` was split away from `booking.slot_taken` to prevent, and
it would be actively misleading for an appointment cancelled a month before its start.

409 rather than 422, on the catalogue's own axis: this is a conflict with state that exists, not a
rule about the shape of the request.

**Flagged for review on 5a's terms.** 5a added `booking.patient_busy` beyond its brief, put the flag
in the proposal rather than in the risks, and said what reverting would cost. The same applies: if the
reviewer prefers to overload an existing code, it is one line in `BookingRefusals` and one i18n pair.
Neither the aggregate's guard, the state machine, nor any constraint is affected — only what the
refusal is called.

## Risks / Trade-offs

- **The reschedule ordering could be written the wrong way round and pass CI** (C2). The obvious test —
  move to another day — passes under both orderings. → The integration test moves an appointment a few
  minutes, and a comment at the fixture says the value is the assertion. Residual risk: a later reader
  "tidies" the fixture. Mitigation is the comment plus the same fact recorded in `02 §5`, which
  survives the test file.

- **The same-appointment concurrency race is invisible in single-threaded tests** (C8). Cancel and
  reschedule racing on one appointment produces a cancelled row *and* a live one, and every functional
  test passes. → A concurrent integration test in the shape 5a used for double-booking: two
  simultaneous requests, exactly one succeeds. This risk is the sibling of 5a's advisory-lock risk and
  gets the same treatment — assert the mechanism, not just the behaviour.

- **The old-appointment filter (C7) and the statement ordering (C2) fail identically from outside** —
  both produce a refusal where a success belonged. → The near-move test covers both, which means one
  passing test is doing two jobs and a failure will not say which. Accepted, and named: the unit tier
  covers the transitions and the domain rule separately, so a failure that is *not* the filter narrows
  quickly.

- **`cutoffApplies = false` ships unexercised by any caller** (C4). Dead-ish code until 5c. → Covered
  in both directions at the unit tier, and the change that consumes it is named in the build order with
  a screen. If 5c were cancelled, this is one boolean to delete. Recorded as a deliberate, bounded bet
  rather than speculative generality.

- **`06 §P6` and `02 §5` describe calendar propagation this change does not deliver** (C9). → The
  documents are correct about the destination and wrong about the date; the seam is written here and
  the validation guide says the cancelled event does not leave the building. **Revisit trigger:**
  change 6.

- **The reschedule chain can grow without bound** (C5). Nothing collapses A → B → C, and no query walks
  it. → Accepted; the row count is one per change and a patient who reschedules ten times has ten rows,
  which is the audit trail working. **Revisit trigger:** a screen that needs to display the chain.

- **P5's list has no pagination.** A patient's history grows forever and the past list grows with it. →
  Accepted for the MVP; the realistic bound is small. **Revisit trigger:** the same one change 4's F8
  response-size trigger is waiting on — the first payload somebody is unwilling to serve. Worth
  noting that F8 is **still open** with no recorded number, and this change adds P6 as a second
  consumer of the availability response.

- **P-4's buffer trade-off is unchanged** and inherited. Cancelling frees the raw range; the solver
  still applies turnaround on top. Nothing here makes it better or worse.

## Migration Plan

One additive EF migration on `appointments`:

1. Add `rescheduled_from_id uuid NULL`, self-referencing `appointments(id)`, `ON DELETE` unspecified —
   nothing deletes appointments (I10), so the clause would describe an impossible event.
2. Add the index EF creates for the FK. This is the fourth index on the table; unlike 5a's task 6.7,
   it is not redundant with an exclusion constraint's GiST index, because it is an equality lookup on a
   different column.

The three exclusion constraints are **not touched**: their predicate is `status = 'Scheduled'` and the
two states this change makes reachable are already outside it. That is the payoff of 5a's B10, and the
migration should say so rather than leaving a reader to wonder why a change that adds two states
touches no constraint.

**Rollback** is dropping the column. No existing row has a non-null value, and no query in a prior
change reads it.

**No data migration.** No configuration is required to run: `Scheduling__CancellationCutoffHours`
defaults to 24 (domain-model F3), so `.env.example` gains a documented optional line and the README's
local-run section needs no new prerequisite — the same property 5a's task 15.2 confirmed for itself.

## Open Questions

1. **Should P5's past list include terminal appointments, or only elapsed ones?** A cancelled future
   appointment is neither upcoming nor past. The working answer is a two-list split by *time* with
   terminal ones shown in the past list annotated with their status, because "what happened to my
   3pm?" is the question a patient actually asks. Flagged because the alternative — a third
   "cancelled" section — is a real product opinion and the validation guide should collect one.

2. **Does a reschedule reset anything a reminder would have sent?** Reminders are change 8 and no
   reminder rows exist, so the answer is currently vacuous. Named so that change 8 finds the question
   already asked rather than discovering it: a rescheduled appointment's reminder must key off the new
   row, and the old row being terminal is what makes that fall out naturally.

3. **Is the cutoff measured from the appointment's start, or from the start minus the lead time?** Read
   plainly, `02 §5` says "before start", and that is what is implemented. Recorded because a clinic
   could reasonably mean something else and the answer is one expression.
