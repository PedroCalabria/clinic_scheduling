## Context

3a said what the clinic offers, 3b said who can deliver it and when. Both stored rules. This change
supplies a date, which is the moment a rule becomes an interval — so it runs the system's first
wall-clock→UTC conversion, and it is where `NodaTime`, brought in by 3b and comparatively idle
there, either earns its dependency or does not.

Two structural facts shape every decision below. First, **the subtraction has no producer**: the
formula in `02-domain-model.md` §4 removes appointments and external blocks that do not exist until
changes 5 and 7. Second, **internal `TimeBlock` needs nothing from booking** and was filed under
`calendar-integration`, where nothing about it belongs. Bringing it here is what turns a solver
verified only against its own test fixtures into one verified against rows a professional created in
a browser.

Decision ids below are `F1…`. `02-domain-model.md` has its own locked decisions A–G in a separate
namespace, so its buffer decision is written as *domain-model F1* where it appears.

## Goals / Non-Goals

**Goals:**

- Compute genuinely free slots for a date window: working hours minus exceptions, converted against
  each concrete date, sliced by the professional's own duration, minus busy intervals, and paired
  with a free resource of the required type.
- Get the DST question right and prove it — the two cases NodaTime exists for, exercised in a zone
  where they can actually fail.
- Leave change 5 a busy-set seam it extends rather than rewrites.
- Ship a browser surface (S3) that produces real subtrahend rows, so the subtraction is demonstrable
  rather than merely unit-tested.
- Resolve 3b's open question 3 (the effective-date dimension) with a decision rather than leaving a
  column someone feels obliged to use.

**Non-Goals:**

- `Appointment` in any form — schema, `tstzrange` columns, `EXCLUDE`/GiST, Dapper, the
  professional-scoped lock (G1), or I7 enforcement. Every one of them protects a race that cannot
  occur until appointments exist.
- External blocks, webhooks, sync (change 7).
- Any patient-facing screen. P2 is the surface this data exists for and it belongs to change 5;
  until then availability is verified through the API and the test suite.
- Deciding which room a booking actually gets. A slot names a free one, but assignment stays
  automatic at booking (domain-model F2) — see F6.

## Decisions

### F1 — The solver is interval arithmetic in the Domain core; the database read is bounded input, not a query

`04-architecture.md` §1 and §2 used to disagree about where the solver lived — §1 put it in the
protected core, §2 described heavy SQL over `tstzrange` and GiST. §2 has been re-scoped: the solver
is C# in `Domain`, and Dapper moves to change 5's write path where those columns actually exist.

The shape is one loading step and one pure function. `Api` reads the inputs for the requested window
— matching working-hour templates, exceptions, internal blocks, and the active resources of the
required type — into a plain input record. `Domain` takes that record and returns slots.
No repository interface, no port, no adapter: `Domain` defines the input shape and the function, and
`Api` fills one and calls the other. Introducing an interface in `Domain` so `Api` can implement it
would be ports-and-adapters ceremony the project has already declined once (P-3).

*Alternatives.* SQL range algebra with Dapper — rejected because the columns and indexes it is
justified by arrive with `Appointment`, the input at clinic scale is small (one professional's week
is tens of rows), and DST resolution in SQL would mean trusting the database's zone handling instead
of the library chosen for it. A hybrid, subtracting in SQL and slicing in C#, is the worst of both:
two places to get the same interval logic wrong.

*Honest cost.* This flips if the input read stops being small. **Revisit trigger:** the input read
for a normal window exceeding a few thousand rows, or a p95 breach against `03-nfr.md`'s target.

### F2 — DST is answered by resolving endpoints leniently and slicing in the instant domain

A working-hour segment is two wall-clock times and a date. Converting them can encounter two things
an offset cannot represent: on a spring-forward day a local time that **does not exist**, and on a
fall-back day one that **happens twice**.

Both are resolved leniently — a nonexistent time shifts forward past the gap, an ambiguous one takes
the earlier occurrence — and the resulting UTC interval is simply shorter or longer than the wall
clock suggests. That is not a compromise; it is what the day is. A clinic open "09:00–17:00 local" on
a spring-forward day is open seven real hours, and pretending otherwise would offer a slot that does
not exist.

The load-bearing half is **where the slicing happens**. Slots are cut from the *instant* interval
after conversion, never from the wall-clock interval before it. Slicing wall clock first and
converting each start produces duplicate instants on a fall-back day and instants inside the gap on
a spring-forward day — a bug that passes every test written in a zone without DST, which is precisely
why `00-context.md` §6 requires a DST-observing zone.

*Alternatives.* A fixed offset — the documented trap; it works until the law changes and no test in
`America/Sao_Paulo` would notice. NodaTime's strict resolver, throwing on both cases — rejected
because a legal clock change would then take availability down for a day; a refusal is the wrong
answer to a question that has a good one.

*Trade-off.* On a fall-back day two distinct slots carry the same local time. Correct — they are
different real instants — but a display concern with no owner until P2 lands. Recorded in Open
Questions rather than solved by a read that has no reader.

### F3 — The effective-date dimension is used in the first cut (3b open question 3: yes)

A template matches a date when the weekday matches **and** the date falls inside the template's
effective range. Filtering by "currently effective" instead — evaluating the period against today
rather than against the queried date — is the tempting shortcut and it is wrong for the exact case
the period exists for: a schedule change taking effect mid-window would answer next month's dates
with this month's hours.

Consequence worth stating because it is easy to miss: 3b allows several non-overlapping templates on
one weekday (a morning and an afternoon block), so a date's candidate hours are the **union** of all
matching templates, not the first match.

### F4 — An exception replaces the day; it does not subtract from it

An active exception for a date wins outright. Unavailable-all-day yields no candidate hours;
different-hours yields *those hours instead of* the templates. 3b guarantees at most one active
exception per professional per date, so there is no precedence puzzle to invent.

*Alternative.* Treating an exception as another busy interval — rejected: "works 14:00–18:00 instead"
cannot be expressed as a subtraction without the administrator having entered a block, and they
entered hours. Conflating the two would make the screen lie about what it stored.

### F5 — One busy-interval list, three future producers, no fake tables

The solver takes a single list of instant intervals and does not care why a professional is busy.
This change fills it from internal `TimeBlock`s. Change 5 appends appointments; change 7 appends
external blocks. The subtraction code is written once and never changes.

Three separately-typed sources were considered and rejected: they would be three code paths to the
same union, and the place that genuinely needs to know *why* a professional is busy is the write
path, where an I7 refusal has to name the cause — not the solver.

*Honest exposure.* The appointment case is untested until change 5. The mitigation is structural
rather than aspirational: it is the same code path as the internal-block case, which is tested here
against rows created through S3.

*Alternative rejected.* Creating the `Appointment` schema now so the subtraction has all three
inputs — an `EXCLUDE` constraint guarding a race that cannot happen, exercised by nothing.

### F6 — A slot names the professional and the resource that satisfy it

**Reversed during implementation.** The first cut had a slot carry the professional and the
appointment type but no concrete `Resource`, on the grounds that naming a room in a read would be a
reservation the system does not hold. Open question 1 asked for that to be reviewed rather than
absorbed, and the review answered: the resource appears in the slot, completing the
`(professional, resource)` pair `02-domain-model.md` §4 always described.

The argument that was rejected is kept rather than deleted, because it identifies a real hazard that
the decision does not remove: **a slot is not a reservation.** By the time a patient confirms, the
named room may be taken. What the reversal changes is where that hazard is handled — it moves from
"do not say it" to "say it, and do not trust it coming back", which is recorded as a risk below and
as a constraint on change 5.

What the decision bought, beyond matching the domain model, is that the resource half of the
tri-constraint stopped being a boolean and became a real seam:

| | First cut | After the reversal |
|---|---|---|
| Input | `bool RequiredResourceIsAvailable` | `ResourceCandidate[]` — id, turnaround buffer, busy intervals |
| Where evaluated | once per answer | once per slot |
| What change 5 adds | occupancy, the choosing, the buffer, and the rule | occupancy only |

That last row is the part worth having. Choosing among free rooms, falling through when one is
taken, withholding a slot when all of them are, and keeping the turnaround buffer
(*domain-model F1*) out of the bookable window are all written and unit-tested now, against a
busy-set that is empty for the same reason the professional's appointment intervals are empty
(F5). Change 5 fills a list rather than implementing a rule — and the buffer, which the first cut
deferred entirely, is live and tested rather than waiting.

Two details that follow, both asserted:

- **The choice is deterministic**, not arbitrary. The loading step orders candidates by name and the
  solver takes the first free one, so the caller's ordering *is* the assignment policy and a test
  can assert an id.
- **The buffer is trailing and applies to rooms only.** A professional free at 10:00 is free at
  10:00; a room occupied until 10:00 with a fifteen-minute turnaround is not. Turnaround belongs to
  the room, not to the person walking out of it, and there is a test for the asymmetry because it
  would otherwise be a plausible thing to "fix".

*Still not decided here:* which room a booking actually gets. Domain-model F2 keeps assignment
automatic at booking, and the id on a slot explains the answer rather than fixing it.

### F7 — Two query modes, one code path, and 3b's gate collapses eligibility to one join

"Specific professional" is the any-professional path over a one-element set. There is no second
solver.

Eligibility for any-professional mode is simpler than expected because of what 3b built: an
appointment type belongs to exactly one specialty, and `ProfessionalAppointmentType` may only exist
for a type whose specialty the professional holds (the I2 qualification gate). So "any professional
of specialty X, for this kind of visit" is exactly "professionals with an active
`ProfessionalAppointmentType` for this appointment type" — one join, and the specialty check comes
along for free rather than being re-derived. The request therefore takes an appointment type and no
separate specialty parameter.

### F8 — Slot starts step by configuration, not by duration, and the read refuses to lie

`02-domain-model.md` §4 lists three scheduling parameters as config rather than code: slot start
step (15 min), minimum lead time (1 h), scheduling horizon (60 days). All three are honoured here,
with defaults, because all three change what the read may offer:

- **Start step** means candidate slots overlap: a 40-minute visit in 09:00–12:00 offers 09:00, 09:15,
  09:30 … not a back-to-back partition. Correct for a read whose slots are not reservations — the
  patient picks a convenient time, and booking one removes its neighbours. Cost: the response grows
  with window × professionals ÷ step, bounded by a maximum window that refuses with
  `availability.window_invalid`.
- **Lead time and horizon** are booking invariants (I8) enforced at write time in change 5. They are
  applied to the read as well, because a read that offers a slot the write will refuse is a lying
  read, and "only genuinely free slots" (SC-1) is the product's whole claim. One config source feeds
  both; change 5's tests assert the pair agree.

These parameters get **defaults**, deliberately unlike `Clinic__Timezone`, which 3b gave none. A
timezone default is wrong for every clinic but one; a 15-minute step is right until a clinic says
otherwise. Same mechanism, opposite call, for a stated reason.

### F9 — `TimeBlock` stores instants, in two `timestamptz` columns

A block is an event at a real time, so — unlike working hours — it converts nothing and stores
instants. This is the other half of `00-context.md` §5's wall-clock rule, and stating it keeps the
rule from being read as "3b uses `time` columns, so schedule things use `time` columns".

Two `timestamptz` columns rather than the `tstzrange` in `02-domain-model.md`'s ERD. The range type
buys range operators and a GiST index; this change performs its overlap arithmetic in C# (F1) and
issues no range query, so a range column would be ceremony. It stays ceremony afterwards too: I7 is
cross-table and is enforced by domain check plus lock plus sweep, not by an `EXCLUDE` — so nothing
downstream forces the conversion. **Revisit trigger:** change 7's reconcile sweep wanting an overlap
query in SQL; the migration is one `ALTER`.

`source` is present from the start with a single value, `Internal`. Change 7 adds `External` plus the
columns only it needs. A discriminator with one value is honest here because the second value is
already designed.

### F10 — Overlapping internal blocks are allowed

They union for availability, so both mean the same thing: busy. Refusing them would be arbitrary.

The contrast with 3b is the point: overlapping *working hours* are refused because two rules covering
one moment leave genuine ambiguity about which applies. Two blocks covering one moment have no
ambiguity to resolve. Same-looking rule, different reason, different answer.

### F11 — A professional owns their blocks; availability is readable by any authenticated caller

Blocks are the second use of ownership authorization, and the first on a record that is not patient
data: a professional creates, edits and retires **their own** blocks and nobody else's. An
administrator cannot block on someone's behalf — that is a clinical decision about their time, the
mirror of 3b refusing to let a professional configure their own qualifications. Because the record is
not patient data, the ownership guard runs **without** writing an `AccessLog`; that log exists for
staff reading patients, and widening it here would dilute an audit trail whose value is its
narrowness.

Availability is readable by any authenticated caller — it exposes free/busy shape, never patient
data. Anonymous is refused, per the project-wide `[Authorize]` default, and the endpoint is
rate-limited per `03-nfr.md` §2, which names the availability search as the abusable surface. It
sends `Cache-Control: no-store`, consistent with Decision S's refusal to cache this path.

### F12 — S3 is a list plus the existing dialog, with times in clinic wall clock

The professional's blocks in a table, add/edit through the dialog primitive 3a introduced. No new
primitive: the input is a native `datetime-local`, styled — which is a good fit rather than a
concession, because it collects wall clock with no zone attached, and the clinic timezone is exactly
the interpretation the server applies.

This is the **first professional-role screen in the staff console**, so the role's navigation branch
is exercised for the first time (see Risks).

### F13 — `GET /api/availability`, one endpoint, professional optional

`GET /api/availability?appointmentTypeId=…&from=…&to=…[&professionalId=…]`. Omitting
`professionalId` is any-professional mode; supplying it is the specific mode (F7). A `GET` because it
is a read, which keeps rate limiting, logging and the correlation id ordinary; a POST body would buy
nothing and make an idempotent read look like a command.

## Risks / Trade-offs

- **The response grows with window × professionals ÷ step** (F8), and nothing consumes it yet, so
  nobody will feel it in this change. → Bounded by a maximum window refused with
  `availability.window_invalid`; **revisit** when P2 lands and a real window is exercised in a
  browser.

- **Solver-in-Domain is a bet on small inputs** (F1). → Revisit trigger recorded in F1. The bet is
  cheap to lose: the input read is already the only thing that would change.

- **The appointment half of the subtraction ships untested** (F5). → Mitigated structurally by one
  list and one code path, not by intention. The residual risk is that change 5 discovers the seam's
  shape is wrong for appointments; the cost of that is confined to the loading step.

- **Lead time and horizon are applied in two places** (F8) — the read here, the write in change 5. →
  One config source, and change 5 owns a test asserting the read and the write agree. The failure
  mode if they drift is the one this change exists to prevent: an offered slot that cannot be booked.

- **A slot names a room it has not reserved** (F6). By the time a patient confirms, that room may be
  taken, and a client could echo the id back as though it were a booking. → The id is documented as
  an explanation rather than authority, in the domain type, the wire contract, and the spec's own
  requirement text. The binding mitigation is a constraint on change 5: **the booking path must
  assign the resource itself** (domain-model F2) and must not accept a caller's. This is the reason
  the first cut withheld the id at all, so it is the one risk here that a future change can still
  get wrong — and the place it would go wrong is not in this change's code.

- **A fall-back day yields two slots with the same local time** (F2). → Correct and deliberate; no
  display owner until P2. In Open Questions.

- **S3 is the first professional-role screen ever rendered**, and professionals sign in with Google,
  so the one role whose staff-console experience has never been exercised is the one this change
  depends on for its browser validation. → The `AsRoleAsync` seam covers the API tests without
  Google; the validation guide has to be walked as a real professional, and a deployment with no
  Google client configured cannot do that. Called out in the guide rather than discovered during it.

- **A block entered by a professional in another timezone reads as clinic wall clock**, not theirs
  (F12). → Deliberate: one configured clinic timezone is Decision H, and multi-timezone is inside
  the anti-scope that cut multi-clinic. No revisit trigger; it would take a scope change.

- **`source` ships with one value** (F9). → A reviewer may reasonably call that premature. The answer
  is that the second value is designed, not speculated, and the alternative is a table rename in
  change 7.

## Migration Plan

One additive EF migration creating `time_blocks`: professional reference, `starts_at_utc` /
`ends_at_utc` as `timestamptz`, `source`, the audit column, and the soft-delete marker (I10). Nothing
existing is altered and no data is backfilled, so rollback is dropping the table.

Two things to read in the generated SQL rather than assume: that `starts_at_utc` and `ends_at_utc`
are genuinely `timestamptz` (the inverse of the assertion 3b added against `information_schema` for
its `time`/`date` columns — both directions of `00-context.md` §5 now have a test), and the index
supporting the window read on `(professional_id, starts_at_utc)`.

The scheduling parameters are added to `.env.example` **with defaults** (F8), and to the Compose
environment. Because they have defaults, a deployment that restarts without them still starts — the
deliberate contrast with `Clinic__Timezone`. That also means the README's local-run section needs no
edit: no new prerequisite, no variable a person must set. The README change is the status cell alone.

## Open Questions

1. **Should a slot carry a concrete `Resource`?** **Resolved: yes**, by review during
   implementation, reversing F6's first cut. The slot names the professional and the resource,
   matching `02-domain-model.md` §4. The objection that prompted the question — a read naming a room
   it has not reserved — was not dismissed: it is recorded as a risk, and it converts into a
   requirement on change 5 not to trust a caller-supplied resource id. The reversal also turned the
   resource half from a boolean into a real candidate set, which brought the turnaround buffer
   forward from change 5 into tested code here.
2. **Do lead time and horizon belong in this change?** F8 includes them so the read cannot offer what
   the write will refuse. They are the most cuttable thing here: dropping them leaves the solver
   correct and the read slightly optimistic, and change 5 would add them to both sides at once.
3. **`tstzrange` for `time_blocks` from the start?** F9 says two `timestamptz` columns, with the
   revisit trigger named. Worth a second look only if change 7's sweep is already known to want SQL
   overlap queries.
4. **How does P2 disambiguate two slots at the same local time on a fall-back day?** Not this
   change's problem, but it is this change's decision (F2) that creates the case. Recorded so change
   5 inherits a known question rather than a surprise.
