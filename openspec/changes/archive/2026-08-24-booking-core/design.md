## Context

Change 4 built an answer to "when could this happen?" and left three things it could not finish. Its
own risk register names all three: the appointment half of the busy set ships untested, lead time and
horizon are applied on a read whose write does not exist, and a slot names a room it has not reserved —
with the binding mitigation written as a constraint on *this* change rather than as code in that one.
Change 4 also shipped internal block creation deliberately unlocked and unchecked, on the stated
grounds that there was nothing to race.

This change is where all four bets are settled, because it creates the racer. That is what makes it the
largest increment in the build order: a new aggregate, a state machine, three enforcement layers, a
second data-access technology, a retrofit into a path that already shipped, and the flagship screen.

Decision ids below are `B1…`. Three other namespaces are in play and are named explicitly wherever
they appear, because two of them collide by letter: `02-domain-model.md` has locked decisions A–G
(written as *domain-model F1*, *domain-model G1* below), `03-nfr.md` has G–J, and `04-architecture.md`
has K–V. `B` is used here precisely because nothing numbered in any document uses it.

## Goals / Non-Goals

**Goals:**

- Make SC-2 true rather than claimed: two simultaneous bookings for the same professional, the same
  room, or the same patient cannot both commit, and the proof is a constraint rather than a code
  review.
- Make the read and the write agree **by construction** — one implementation of "is this slot
  offerable", reached from two directions.
- Fill change 4's `BusyInterval` seam and find out whether it was the right shape, rather than
  assuming it.
- Close I7 in both directions and put the domain-model G1 lock under both, so the cross-table check is
  race-safe instead of merely written.
- Ship P2 as a designed surface, since it is the public, recruiter-facing expression of the whole
  thesis and the first screen where the project's argument is visible to someone who will never read
  a spec.

**Non-Goals:**

- The four terminal transitions, reschedule, cancel, and the domain-model F3 cutoff (5b). The enum is
  declared whole; nothing beyond `Scheduled` is reachable.
- Front-desk booking on behalf (S5), the professional's own schedule (S1), the day view (S4), and the
  patient's appointment list (P5/P6) — all 5b.
- Calendar sync in any form: the outbox, `externalEventId`, `ReconciliationConflict`, and the
  domain-model G2 sweep (6/7).
- Strict buffer enforcement at the database level. P-4 stays exactly where change 4 left it — see
  Risks.
- Any change to how a role is assigned or how a session is issued.

## Decisions

### B1 — One solver, two entry points: `Solve` enumerates, `Explain` diagnoses

The obvious implementation of a booking check is a list of validations in the handler: inside working
hours, not inside a block, lead time respected, a room free. Change 4 already wrote every one of those,
in the solver. Writing them again is how the read and the write drift, and drift here produces the
exact failure this product exists to prevent — a slot offered on P2 that P3 refuses.

So the solver gains a second entry point rather than a sibling:

- `Solve(inputs)` — unchanged. Enumerates every offerable slot for a window.
- `Explain(inputs, requestedStart)` — walks the *same* steps for one candidate start and returns
  either the resource it would assign or a **typed reason** it would not: outside candidate hours,
  inside lead time, beyond horizon, overlapping the professional's busy set (distinguishing a block
  from an appointment), or no free room.

Both are pure functions in `Domain` over the same `AvailabilityInputs`. The slice maps a reason to an
error code; it never decides one.

*Why the reason must be typed and produced by the solver.* The friendly message is the thing an
application check exists for (02 §"Design principle"): the constraint below makes the guarantee true,
the domain makes the refusal *legible*. A reason produced anywhere other than inside the same walk is
a guess about why the slot was not offered, and a guess is wrong exactly when the rules are
complicated — which is the only case that matters.

*Alternatives.* (a) `Solve` for the window and a set-membership test on the result — correct, and it
is what the agreement test asserts, but it can only ever say "not offered", so every refusal collapses
into one unhelpful code. (b) Independent write-side validations, with a test asserting they agree —
the shape the Definition of Done asks for, and the shape change 4's risk register predicted would
rot: a test can only assert the cases someone thought of, and the drift will be in the case nobody
did. (c) Re-run `Solve` for a one-day window and then diagnose from the inputs — two walks over the
same data with two chances to disagree.

*The property that replaces the promise.* A test asserts both directions over generated inputs:
everything `Solve` offers, `Explain` admits with a resource; everything `Explain` admits,
`Solve` offers. The lead-time/horizon agreement test the Definition of Done requires stays as a
readable, specific regression guard on top of it — the property is the protection, the test is the
documentation.

### B2 — Three layers, and each one is for a different job

The layering is the project's central claim, so it is worth stating what each layer is *for*, because
they are easy to mistake for redundancy:

| Layer | Job | If it were missing |
|---|---|---|
| `Appointment` aggregate (I1–I3, I8) | An appointment that violates an invariant cannot be *constructed* — from a handler, a test, or 5b's front-desk path | A future path could persist a nonsense appointment by skipping a handler check |
| `Explain` (B1) | Names the cause, so the refusal is a sentence a patient understands | Every refusal would be "that slot is not available" |
| Partial `EXCLUDE` constraints (I4–I6) | The guarantee holds under concurrency, whatever the code does | Two racers both read "free" and both commit |

The aggregate's checks are therefore not defensive duplication of `Explain`; they are the reason a
second write path in 5b cannot reintroduce a bug this change fixes. They are also what the domain unit
tests exercise, since they are reachable without a database.

### B3 — Three partial `EXCLUDE` constraints, and the predicate is the status alone

One `tstzrange` column, `btree_gist`, and three constraints:

```
EXCLUDE USING gist (professional_id WITH =, time_range WITH &&) WHERE (status = 'Scheduled')
EXCLUDE USING gist (resource_id     WITH =, time_range WITH &&) WHERE (status = 'Scheduled')
EXCLUDE USING gist (patient_id      WITH =, time_range WITH &&) WHERE (status = 'Scheduled')
```

The range is half-open (`[)`), matching `BusyInterval`'s half-open overlap exactly, so an appointment
ending at 10:00 and one starting at 10:00 are accepted by both the constraint and the solver. A closed
range here would refuse the most ordinary schedule there is and would disagree with the read.

**The predicate is `status = 'Scheduled'` and nothing else, because `appointments` has no soft-delete
column.** This deviates from the ERD in 02 §9, which shows `deleted_at`, so the argument is on the
record:

- I10 exists so that history is reconstructible. An appointment's history is reconstructible from its
  status — and `Cancelled`, `Rescheduled` and `No-show` are *richer* facts than a deleted flag, which
  is exactly why the state machine has them.
- A `deleted_at` on top would create a second, weaker way for an appointment to stop counting, and the
  predicate would have to honour both. **Two sources of truth for "is this row live" is how an
  exclusion constraint becomes decorative** — the day they disagree, the constraint stops protecting
  the case the application thinks it protects.
- Nothing in the MVP would ever set it. A column no path writes is the same mistake as a table for a
  producer that does not exist, which this project has declined twice already.

*Alternative.* Follow the ERD, add the column, and write the predicate as
`status = 'Scheduled' AND deleted_at IS NULL`. Rejected for the reason above; recorded in Open
Questions because it is a documented shape being deliberately departed from, and that is a reviewer's
call to overrule.

Why three constraints rather than one: they are three different invariants (I4, I5, I6) with three
different remedies, and PostgreSQL names the violated constraint — which is what lets B8 answer with
the right code instead of a generic one.

### B4 — `tstzrange` for appointments, two `timestamptz` for blocks: change 4's open question 3, answered

Change 4 stored a block as two `timestamptz` columns and recorded the revisit trigger: a range column
would be ceremony while all overlap arithmetic happens in C#. That reasoning still holds for blocks and
does **not** hold for appointments, and the difference is not stylistic:

- An appointment's overlap check has to run **inside the database**, because that is the only place a
  race can be adjudicated. `EXCLUDE USING gist` requires a range type and a GiST-indexable operator.
- A block's overlap check has no race to adjudicate — I7 is cross-table and is enforced by the domain
  check plus the lock plus (later) the sweep, never by an `EXCLUDE`.

So the schema is asymmetric because the enforcement is asymmetric, which is a better reason than
consistency. Blocks are **not** migrated to `tstzrange` here: nothing in this change issues a range
query against them (the busy read filters by two instants, which the existing
`(professional_id, starts_at)` index serves), and converting them would be a migration bought by
symmetry alone. Change 4's revisit trigger — change 7's sweep wanting SQL overlap queries — is still
the trigger, unfired.

### B5 — Where Dapper actually lands, and it must ride EF's connection

Decision L says Dapper earns its place on the booking write path. Landing it *precisely* matters more
than landing it symmetrically:

- **EF Core performs the insert.** The aggregate is the write model (Decision L's own first clause),
  and the exclusion constraint protects the row regardless of which client issues the `INSERT`. Adding
  a second insert path so that Dapper "owns the write" would mean two ways to create an appointment,
  which is the opposite of what an aggregate is for.
- **Dapper owns what EF cannot express**: `pg_advisory_xact_lock`, and the `time_range &&` overlap
  reads that feed the busy set on both the read and the write path. That is genuinely hand-written SQL
  over `tstzrange` and GiST — the thing Decision L was justified by.

**The load-bearing implementation detail: Dapper must execute on the `DbContext`'s own connection,
inside the `DbContext`'s transaction.** A transaction-scoped advisory lock taken on a different
connection is taken in a different transaction, released immediately, and protects nothing — while
every test still passes, because tests that do not race never notice. The same is true of the overlap
read: on another connection it sees another snapshot. So the booking handler opens one EF transaction,
hands `Database.GetDbConnection()` and `CurrentTransaction` to Dapper, and everything happens on that
one connection. This is asserted rather than assumed — see the Migration Plan.

*Alternative rejected.* `NpgsqlDataSource` for the Dapper calls and EF for the insert, coordinated by
`TransactionScope`. It would work and it would be a distributed-transaction mechanism introduced to
avoid passing a connection.

### B6 — The NodaTime Npgsql plugin, deferred a third time, and this time for a reason that ends

3b deferred it because it stored no instants; change 4 deferred it again on the grounds that
`Instant` → `DateTimeOffset` is exact and Npgsql maps that to `timestamptz` — and named change 5's
range work as the case that could change the answer. That case is now here, and the answer is
unchanged: `NpgsqlRange<DateTimeOffset>` maps `tstzrange` natively, so a value converter from the
domain's own range type is four lines, and Dapper's SQL constructs ranges with
`tstzrange(@start, @end, '[)')` from ordinary parameters.

What changes is that the deferral now has an end condition rather than being re-asked every change:
the plugin becomes correct when a query needs to express a NodaTime type *in SQL itself* —
`Interval`, `LocalDate` as a parameter to a range function, or a NodaTime type in a `WHERE`. Nothing
in the build order requires that. If it ever does, the cost of adopting late is that the plugin also
takes over the `time`/`date` mappings 3b hand-wrote, so it is a change that must be read carefully
rather than a package reference.

### B7 — One advisory lock per professional, taken first on both paths

Both schedule-mutating paths open a transaction and immediately take
`pg_advisory_xact_lock(key)` where `key` is a `bigint` derived deterministically from the
professional's id, in **one** function used by both call sites. The lock is transaction-scoped, so it
is released by commit or rollback and cannot be leaked by a failing handler.

What the lock is for, stated narrowly: it closes the **cross-table** read-then-write race between an
appointment and an internal block. It is *not* what protects appointment↔appointment overlap — the
constraints do that, and they do it for the resource and the patient too, which no professional-scoped
lock could.

Consequences worth stating because they are easy to get wrong:

- **Key derivation may collide** across professionals (a 128-bit id into 64 bits). A collision costs
  serialization between two unrelated professionals and never costs correctness, because the lock is
  not the thing that makes any check true.
- **The lock must be taken before the reads it protects**, not around the write. Taking it after
  reading the busy set is a lock that serializes nothing.
- **Booking takes exactly one lock**, on the professional. A patient booking two professionals
  concurrently is adjudicated by the patient constraint (I6), not by a lock, so there is no ordering
  and therefore no deadlock to design around. This is a property worth keeping: the moment a path
  needs two of these locks, a lock-ordering rule has to be invented.

*Alternative.* `SERIALIZABLE` for both paths — declarative, and domain-model G1 records it as the
alternative. Rejected on the grounds already recorded there: booking is frequent and block creation is
rare, so serialization-failure retries would be imposed on the hot path to protect a collision that
happens between two paths touching one professional, where contention is approximately zero.

### B8 — Constraint violation to error code, and the resource race refuses rather than retries

`Explain` runs before the insert, so on the ordinary path every refusal has already happened with a
specific code. A violation at commit therefore means a genuine race, and the violated constraint's
name says which:

| Violated | Code | Status |
|---|---|---|
| professional exclusion (I4) | `booking.slot_taken` | 409 |
| resource exclusion (I5) | `booking.resource_unavailable` | 409 |
| patient exclusion (I6) | `booking.patient_busy` | 409 |

Mapping by constraint name means the names are part of the contract, so they are asserted in an
integration test — a renamed constraint would otherwise silently degrade three specific answers into
`server.unexpected`.

**The resource race refuses; it does not retry.** The server chose a free room a moment earlier
(domain-model F2), so a resource violation means another professional's booking took it in between.
Retrying with the next candidate would be friendlier, and it needs a `SAVEPOINT` per attempt because a
failed statement aborts the transaction. That machinery is not bought here: rooms contended at the
same instant across professionals in a single clinic is a rare event, and `resource_unavailable` is an
honest answer that P3 can act on. **Revisit trigger:** the refusal appearing in logs at a rate a
human notices — the fix is a savepoint loop over the remaining candidates, confined to one handler.

Professional and patient violations never retry, because retrying cannot help: the time is taken.

**A deadlock is not a refusal, and it happens — found while writing the concurrency tests.** Two
bookings for the same patient with two different professionals in the same room conflict on *two*
exclusion constraints at once. Each transaction inserts its heap tuple before either finishes
checking indexes, so each waits on the other's tuple and PostgreSQL breaks the cycle with `40P01`.
The professional lock cannot prevent it: the two transactions hold **different** professionals'
locks, which is precisely the concurrency that lock exists to allow.

So the transactional part is attempted up to three times, and `40P01` (with `40001` for
completeness) triggers a retry rather than a code:

- The victim's transaction was rolled back **entirely**, so the retry re-reads committed state and
  produces the *correct specific* answer — the winner's row is now visible, so it refuses with
  `slot_taken` / `resource_unavailable` / `patient_busy`, or succeeds because the conflict was never
  real.
- Mapping the deadlock itself to a code would sometimes lie, because it does not say which
  constraint would have refused.
- Three attempts is one more than a two-transaction cycle needs; persisting past that is reported as
  an unexpected failure rather than dressed up as a business outcome.

**A race violates more than one constraint at once, so the code it reports is not deterministic —
found by a flaky test, and the flake was the test's fault.** Two racing transactions cannot see each
other's uncommitted rows, so each independently assigns the *first free* room: the same one. A race
for one professional's slot therefore collides on the room as well, and a race to double-book one
patient collides on the room too. PostgreSQL reports whichever constraint it checks first, which
follows index order rather than anything this project decides.

The answer is to assert the guarantee rather than the database's checking order:

- **The concurrency tests assert that exactly one commits and the loser is told something true** — a
  409 naming a collision it genuinely had. Each permitted code is correct and each has the same
  remedy, which is precisely the catalogue's own test for whether two failures are one.
- **The specific code is asserted sequentially**, where only one invariant is in play: book, then
  book again over it, and the answer is deterministic. That is also the shape the spec's scenarios
  describe, and writing the sequential `patient_busy` test is what revealed the concurrent one had
  been asserting something it could not promise.

Pinning the code harder would have meant asserting an implementation detail of PostgreSQL and
calling it a requirement.

Two implementation details the retry forced, both of which would have been silent bugs:

- **The exception chain must be walked, not unwrapped once.** EF classifies the deadlock as
  transient and wraps it again, so it arrives as
  `InvalidOperationException → DbUpdateException → PostgresException`. A one-level unwrap misses it,
  and the symptom is a `500` where a specific `409` belonged. That is how this was found.
- **The change tracker is cleared before each attempt**, so a retry does not re-submit the entity the
  failed attempt added and insert twice on success.

### B9 — The request names an instant and nothing else that matters

```
POST /api/appointments   { appointmentTypeId, professionalId, startsAt }
```

- **`startsAt` is a UTC instant** (ISO-8601), never a wall-clock label (Q4). A fall-back day
  legitimately produces two slots reading the same local time an hour apart in real time; an instant
  distinguishes them and a label cannot. The end is not sent — it is derived from the professional's
  duration for the type, which is what "duration is baked at booking" (I1) means on the wire.
- **No `resourceId`.** Not "ignored if present" — absent from the contract, so domain-model F2 is
  structural rather than a rule someone must remember, exactly as change 4's block request carries no
  professional. Change 4's risk register named this as the one hazard a future change could still get
  wrong; this is where it is not got wrong.
- **No `patientId`.** The appointment belongs to the caller's own patient record, read from the
  session. 5b widens this path for the front desk booking on behalf, and it will do so by adding an
  explicit, role-gated field — never by starting to trust a body value this change ignores.

A pleasant consequence of I6 worth naming: a double-submitted confirm button is self-defending. The
second request overlaps the appointment the first one created for the same patient, so it is refused
by the patient constraint rather than by an idempotency mechanism this change does not have.

### B10 — The status enum is declared whole; only one transition is wired

`AppointmentStatus` gets all five values now. I9 ("state transitions only per the state machine") is a
statement about a shape, and a two-value enum would misdescribe the shape while making 5b's migration
a column rewrite instead of new code.

What keeps this from being half-built:

- There is exactly one way to create an appointment and it produces `Scheduled`. No setter, no public
  transition method — 5b adds those with their own guards and their own tests.
- The `EXCLUDE` predicate already reads `status = 'Scheduled'`, so the day 5b writes `Cancelled`, the
  slot frees itself with no migration and no constraint change. A unit test asserts the terminal
  values exist and are distinct; an integration test asserts that a row written directly in a terminal
  state does not block a booking, which is the behaviour 5b depends on, provable now.

### B11 — The seam fill, and what appointments needed that blocks did not

Change 4 bet that one untyped busy list would absorb appointments without change. The bet is settled
here, and the honest answer is **mostly yes — with one asymmetry the seam already had room for, and
one addition it genuinely needed**.

**What it needed (recorded during implementation, not predicted).** `BusyInterval` gained a
`BusyCause` discriminator — `InternalBlock`, `Appointment`, `ExternalBlock`. The subtraction *does*
absorb appointments unchanged, exactly as F5 promised, but the refusal cannot: `booking.slot_blocked`
and `booking.slot_taken` are separate codes precisely because the causes differ, and something has to
tell them apart. Two things make this an honest fill of the seam rather than a repair of it:

- **F5 predicted this specific caller.** It said the busy list deliberately does not record why, and
  that "the place that genuinely needs to know *why* a professional is busy is the write path, where
  an I7 refusal has to name the cause". This is that place, arriving on schedule.
- **F5's actual promise still holds.** The subtraction ignores the value entirely — one list, one
  comparison, one code path, and a unit test asserts that two configurations differing only in cause
  produce identical slots. Only the refusal reads it. The rejected alternative in F5 was *three
  separately-typed lists*, and that is still rejected; a discriminator on one value is not three code
  paths to the same union.

Had the cause been split into three lists instead, `Judge` would have needed three overlap loops and
change 7 a fourth. So the seam took a field, not a rewrite — which is what a good seam does.

The asymmetry it already accommodated:

- A block contributes to **one** list: its professional's `BusyIntervals`. That is all a block can
  affect — a block occupies nobody's room.
- An appointment contributes to **two**: its professional's `BusyIntervals` *and* its resource's, in
  `ResourceCandidate.BusyIntervals`.

The second list existed only because change 4 reversed its own first cut on F6 and turned the resource
half from a boolean into a candidate set. Had it not, this change would be adding the buffer, the
choosing, the fall-through and the no-free-room rule at the same time as the producer. So the seam paid
off, and specifically the *reversal* paid off — worth recording, because the reversal was the more
expensive-looking decision at the time.

The producer is one loading step serving both paths. That is the point: the availability read and the
booking check cannot see different busy sets, because there is one query. The appointment overlap read
is the Dapper `time_range &&` query from B5, bounded by the requested window, and the same rows feed
both the professional lists and the resource lists.

### B12 — The consent gate, and a correction to `06 §P3`

`06-ui-surfaces.md` describes P3 as capturing data-processing consent for a first-time patient. Change
2 already grants that consent at Google just-in-time provisioning, so as written the screen would
capture something that has always already happened.

What P3 genuinely owns is the **gate**, and the gate is not redundant — it closes a loop change 2
opened. P7 lets a patient revoke `DataProcessing`, and until now nothing checked it, so a patient
could withdraw consent and keep transacting. So:

- Booking requires an **active** `DataProcessing` consent at the configured current version. Absent,
  revoked, or superseded → `auth.consent_required` (422), a code catalogued since the seed and never
  yet used.
- P3 shows the consent state and offers a re-grant in place when it is missing, so the refusal is
  recoverable on the screen where it happened rather than by a trip to P7.

The version comparison is deliberately included: it is the mechanism a versioned consent exists for,
and a gate that ignored the version would make `Consent.Version` decoration. **Revisit trigger:** the
first time the version actually changes, every patient is gated at once — which is correct, and which
is a product decision someone should make deliberately rather than discover.

### B13 — Two new codes, and why neither overloads an existing one

Both are added to `07-error-codes.md` before use, per Decision I.

- **`booking.slot_blocked` (409)** — the professional has an internal block over the requested time
  (the I7 booking direction). The catalogue's rule is one code per distinct user-meaningful failure,
  and the distinction here is the remedy: `slot_taken` means somebody was faster and another time will
  do, while this means the professional is unavailable and the read that offered it is stale for a
  different reason. It is also the mirror of `booking.block_overlaps_appointment`, which already exists
  for the other direction — having one direction named and the other overloaded would be the
  asymmetry.
- **`booking.patient_busy` (409)** — the patient already holds an appointment over that time (I6).
  Beyond the brief, and flagged in the proposal for review: without it the third exclusion constraint
  has no answer, and `slot_taken` would tell a patient that somebody else took a slot they are
  themselves standing in.

Both get pt-BR and en keys as part of this change's Definition of Done, along with every previously
catalogued `booking.*` code that becomes reachable here for the first time.

### B14 — P2's shape: the search state lives in the URL, and the query is never fresh

P2 is the flagship, so its architecture is stated rather than left to the screen:

- **The search is the URL.** Specialty, appointment type, professional-or-any, and the date window are
  query parameters. A patient can reload, bookmark, or be sent a link; the back button works; and the
  P3 → back → P2 path returns to the results rather than to an empty form. This costs nothing and is
  the difference between a demo and a product.
- **TanStack Query with no staleness.** Availability is uncached by decision (Decision S) and the API
  says `no-store`. The client honours that: `staleTime: 0`, refetch on window focus and on
  reconnection. This is the first place TanStack Query is doing the job it was chosen for rather than
  wrapping a fetch — volatile server state that must not be shown stale, because a stale slot is a
  slot that is already gone.
- **Five states, all designed, all reachable in validation.** Results, loading, empty (a genuine
  success — "nothing free in this window, try another"), error (`availability.unavailable` /
  `auth.rate_limited`), and **taken** — the P3 refusal handed back to P2 with the offending slot
  removed and the reason shown, which is the state the whole optimistic design exists to make
  graceful.
- **Slots are grouped by day and rendered in clinic wall clock**, converted from the instants in the
  response using the `timezone` the response carries — never from the browser's zone. This is also
  where change 4's open question 4 is answered: on a fall-back day two slots can read the same local
  time, and the UI disambiguates them by rendering the pair with an explicit offset rather than by
  hiding one. Rare, correct, and cheap.

Design input comes from the existing Claude Design canvas (`design/`), extended with P2/P3/P4
artboards; the implementation is the real stack (React 19, shadcn primitives from
`packages/shared`, TanStack Query, react-i18next). The canvas is input, not output — no generated
markup is shipped.

### B15 — What P3 collects, and what it deliberately does not

Just-in-time provisioning gives a patient a name and an email from Google and leaves
`ContactPhone` empty (`Patient.Register`). So "first-time patient captures minimal data" resolves to
exactly one field the clinic genuinely needs and does not have: a contact phone, requested on P3 when
it is missing, with the name shown for correction.

Nothing else is collected. LGPD minimization is a stated principle (02 §8), and the temptation on a
confirm screen is to ask for a birth date or a document number because a real clinic form would. This
one asks for what the appointment needs.

P4 confirms with the appointment summary, the professional, the room-free-of-charge detail omitted (a
patient does not need to know which room), and a link onward — to P5 once 5b exists, and until then to
the profile. The dangling link is a real seam and is named rather than hidden.

## Risks / Trade-offs

- **The advisory lock could be silently ineffective** if Dapper runs on a different connection than
  EF's transaction (B5). Every functional test would still pass. → Asserted directly: an integration
  test takes the lock in one transaction and proves a second transaction blocks, and the booking-vs-block
  serialization test would fail without it. This is the single most important assertion in the change,
  because the failure mode is invisible.

- **`Explain` and `Solve` could diverge** despite sharing a file (B1). → A property test in both
  directions over generated inputs, plus the specific lead-time/horizon agreement test. Residual risk:
  a rule added later to only one of them. Mitigation is structural — they walk the same private steps,
  so adding a rule to one requires editing the shared walk.

- **The buffer is still not enforced at the database level** (P-4, inherited). The constraint operates
  on the raw range while the solver applies the resource's turnaround, so two exactly-abutting bookings
  in one room remain theoretically race-possible: the loser of a race can land in the cleaning window.
  → Accepted, unchanged from change 4's framing. The fix is an expression index over the buffered
  range, which is a real option and not a rewrite. **Revisit trigger:** a clinic reporting a room
  double-used with a buffer configured.

- **`appointments` has no soft-delete column**, deviating from the ERD in 02 §9 (B3). → Argued rather
  than assumed, and listed in Open Questions. The cost of being wrong is one additive migration and a
  predicate change; the cost of the alternative is a second source of truth inside a constraint
  predicate.

- **The resource race refuses rather than retrying** (B8), so a patient can be told no room is free
  when one was a moment later. → Bounded by rarity and by an honest code; revisit trigger recorded.

- **The consent version gate will one day refuse everybody at once** (B12). → Correct behaviour, but
  it makes bumping `Auth__ConsentVersion` a product event rather than a config edit. Recorded here so
  the person who bumps it is not surprised.

- **P2 is the first screen where a stale read is user-visible.** A slot can be taken between the
  search and the confirm, and no amount of refetching closes that window. → This is not a bug to
  mitigate; it is the design (Decision D, optimistic booking). What is mitigated is the *experience*:
  the taken state is a designed state, the offending slot is removed, and the search stays where it
  was.

- **`booking.patient_busy` is a scope addition beyond the brief** (B13). → Flagged in the proposal, not
  buried here. If the reviewer prefers to overload `slot_taken`, the change is one mapping line and one
  i18n pair; the constraint and the invariant are unaffected.

- **P4's onward link has no destination until 5b** (B15). → Points at the profile for now and is named
  in the validation guide, so it is a known gap rather than a broken link somebody finds in a demo.

- **This change is large**, and Decision W says a change should be reviewable in one sitting. → It is
  the one increment where the build order accepted that, because the aggregate, the constraint, the
  lock and the screen have no seam between them that leaves a demonstrable increment on either side:
  the constraint without the aggregate is unreachable, the aggregate without the screen is untestable
  by a human, and the retrofit without the racer is what change 4 already declined to build. Stated so
  that "it is big" is a recorded judgement rather than a drift.

## Migration Plan

One additive EF migration creating `appointments`: patient, professional, resource and appointment-type
references, `time_range` as `tstzrange`, `status` as text (the string-enum convention from change 2),
`source` as text, and the audit column. Then, in the same migration and written by hand because EF does
not model them:

1. `CREATE EXTENSION IF NOT EXISTS btree_gist;`
2. The three partial `EXCLUDE USING gist` constraints from B3, with explicit, asserted names.
3. A supporting index for the window read on `(professional_id, time_range)` using GiST — the same
   index the professional exclusion constraint creates, so this is a check that no fourth index is
   added by reflex rather than a new object.

Nothing existing is altered; no data is backfilled; rollback is dropping the table (the extension is
left, being harmless and possibly shared). `time_blocks` is **not** migrated to `tstzrange` (B4).

Three things to read in the generated SQL rather than assume, in the spirit of the assertions 3b and
change 4 both added:

- `time_range` is genuinely `tstzrange` and the range literals are **half-open** — a `[]` default here
  would refuse abutting appointments and disagree with the solver.
- The three constraint predicates say `status = 'Scheduled'`, and the constraint names match the ones
  B8's mapping depends on.
- `btree_gist` is created before the constraints, since the `=` operator class for `uuid` in a GiST
  index comes from it.

No new environment variable and no new prerequisite: the scheduling parameters that govern I8 already
exist from change 4 with defaults, and `Auth__ConsentVersion` already exists from change 2. The
README's local-run section therefore needs no edit — the README change is the status cell and the
"N of 9 increments shipped" line.

## Open Questions

1. **Should `appointments` carry a soft-delete column after all?** B3 says no and argues it from I10's
   purpose and from the hazard of a two-clause predicate. It deviates from 02 §9's ERD, so it is a
   reviewer's call to overrule; the cost of reversing is one additive migration.
2. **Is `booking.patient_busy` wanted?** B13 says yes and the proposal flags it as beyond the brief.
   The alternative is overloading `booking.slot_taken`, at the price of a message that misdescribes the
   cause.
3. **Should the resource race retry across remaining candidates?** B8 says no for now, with a revisit
   trigger and a bounded fix (a savepoint loop in one handler).
4. **Does `Explain` belong in the solver's file or beside it?** Same class here, because the whole
   argument is that they share one walk. Worth revisiting only if the file becomes hard to read, and
   splitting it would need the shared steps to stay shared.
5. **Should a professional have a name before a patient is shown one?** **Resolved during
   implementation: no, provided the real field is scheduled.** P2 needs a patient-facing label and
   `Professional` carries none, so the server derives one from the account's local part behind a
   `displayName` field. Accepted on the explicit condition that a change owns the replacement —
   **`booking-lifecycle` (5b) now does, as P-5**, because it needs a professional's name for S1, S4
   and S5 anyway. Recorded in `02-domain-model.md` §10 and the build order rather than only in a code
   comment, so the seam is a scheduled debt instead of a discovered one.
6. **How does 5b widen `POST /api/appointments` for the front desk?** B9 says by an explicit,
   role-gated patient field, never by starting to trust an ignored one. Recorded so 5b inherits a
   decision rather than a temptation.
