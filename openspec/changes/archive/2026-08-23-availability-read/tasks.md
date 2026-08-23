## 1. Scheduling parameters: the configuration the read is bounded by

- [x] 1.1 Add `Scheduling__*` options bound at startup — slot start step, minimum lead time, scheduling horizon, maximum window — **with defaults** (15 min, 60 min, 60 days, and a window cap), validated as positive, failing startup on a nonsensical value (F8)
- [x] 1.2 Record in the options type why these carry defaults while `Clinic__Timezone` deliberately carries none: 15 minutes is right until a clinic says otherwise, a timezone default is wrong for every clinic but one. Same mechanism, opposite call, stated rather than inferred
- [x] 1.3 Add the keys to `.env.example` and `infra/docker-compose.yml`, commented as optional, and confirm the API container still starts with them absent
- [x] 1.4 Confirm `HealthEndpointUnhealthyTests` and every other host that builds its own `WebApplicationFactory` still start — 3b's second discovery was that required configuration breaks exactly those, and defaults are what keeps this change from repeating it
- [x] 1.5 Integration-test both halves: absent configuration starts on the documented defaults, and an out-of-range value fails startup with the setting named

## 2. Domain: the internal time block

- [x] 2.1 Add `TimeBlock` in a new `Scheduling` area of `Domain`, holding a start and end **instant** — the deliberate inverse of 3b's wall-clock types, because a block is an event rather than a rule (F9)
- [x] 2.2 Decide and record whether the range uses NodaTime's `Instant` or `DateTimeOffset`: `Instant` makes "this is not wall clock" a compile-time fact and is consistent with E3's reasoning, at the cost of a converter. Audit columns stay `DateTimeOffset` to match every existing entity
- [x] 2.3 Add `source` with its single value `Internal`, so change 7 adds a value rather than renaming a table (F9)
- [x] 2.4 Add the validity rule: the end must follow the start, raising the refusal the slice maps to `block.invalid_range`. One predicate covers both the reversed and the zero-length case
- [x] 2.5 Add **no** overlap rule, and write the unit test that asserts overlapping blocks are accepted — so the absence is a decision on record rather than an omission nobody notices (F10)
- [x] 2.6 Add retirement as deactivation, never deletion (I10), and confirm a retired block is distinguishable from an active one
- [x] 2.7 Unit-test the validity rule: forward range accepted, reversed refused, zero-length refused

## 3. Domain: the solver

- [x] 3.1 Define the solver's input record in `Domain` — matching working-hour segments, exceptions, busy intervals, per-professional durations, resource-type feasibility, and the scheduling parameters. **No repository interface**: `Domain` declares the shape and the function, `Api` fills one and calls the other (F1)
- [x] 3.2 Implement segment-to-date matching as the two-dimensional rule: the weekday matches **and** the date falls inside the effective period; several matching segments union rather than first-wins (F3)
- [x] 3.3 Implement the exception rule as **replacement**, not subtraction: unavailable-all-day yields nothing, different-hours yields those hours in place of every matching segment (F4)
- [x] 3.4 Implement wall-clock→instant conversion per date with lenient resolution — a nonexistent local time resolves forward past the gap, an ambiguous one takes the earlier occurrence, and neither raises (F2)
- [x] 3.5 Slice **the converted instant interval**, never the wall-clock interval. This is the single line the DST tests in 4.7 and 4.8 exist to protect; write it knowing that the wrong version passes every test in a zone without daylight saving (F2)
- [x] 3.6 Implement slot starts at the configured step, offering a slot only where it fits entirely within the candidate hours, using each professional's own duration for the appointment type (F8)
- [x] 3.7 Subtract the busy intervals as **one list** with half-open comparison, so touching endpoints do not overlap and the appointment and external-block cases later join the same code path unchanged (F5)
- [x] 3.8 Apply the minimum lead time and the scheduling horizon to the offered slots, from the same configuration change 5's write path will enforce (F8)
- [x] 3.9 Gate on resource-type feasibility — at least one active resource of the required type must exist — and give the slot a professional and an appointment type but **no concrete resource** (F6)
- [x] 3.10 Implement any-professional as the general path and specific-professional as the one-element case, so there is one solver and not two (F7)
- [x] 3.11 Run the build and `DomainBoundaryTests` and confirm the guard still passes: the solver is the largest thing ever added to `Domain`, and this is the change that would notice if infrastructure arrived with it

## 4. Domain unit tests: the solver, and the daylight-saving cases with teeth

- [x] 4.1 Effective dates: a date before the period, a date after it, a mid-window schedule change honoured on both sides, a split day contributing both segments, and a retired segment contributing nothing
- [x] 4.2 Exceptions: a day off, different hours replacing the recurring ones, neighbouring dates unaffected, another professional unaffected, a retired exception restoring the recurring hours
- [x] 4.3 Slicing: starts at the configured step, a slot that would overrun the hours withheld, and two professionals with different durations for one type each getting their own
- [x] 4.4 Subtraction: covered slots absent, an abutting slot still offered, overlapping blocks equalling their union, a retired block restoring slots, and a block outside working hours changing nothing
- [x] 4.5 Resource feasibility: zero active resources of the required type yields nothing, one is enough, and a returned slot names no resource
- [x] 4.6 Any-professional union: two qualified professionals both present, an unqualified one absent, and the same time from two professionals offered twice and distinguishable
- [x] 4.7 Daylight saving in a **DST-observing zone** (`00-context.md` §6): the spring-forward day's interval is an hour shorter, a nonexistent start resolves forward, the fall-back day's interval is an hour longer, and an ambiguous start takes the earlier occurrence
- [x] 4.8 The test that catches the wall-clock-slicing bug: on both transition dates, assert no two slot starts share an instant and none falls inside the skipped interval. This is the assertion that fails if 3.5 was written the natural, wrong way round
- [x] 4.9 Run the same fixtures under `America/Sao_Paulo` and assert they pass trivially, with a comment saying why — the point of §6 is that this zone cannot fail, and the test suite should say so rather than leaving a future reader to assume coverage it does not have

## 5. Persistence: schema and mapping

- [x] 5.1 Add `Configurations/SchedulingConfigurations.cs` mapping `TimeBlock`, snake_case and enum-as-string per the established convention
- [x] 5.2 Map the start and end to `timestamptz` explicitly, and add the integration assertion against `information_schema` that they **are** timestamp-with-timezone — the inverse of 3b's assertion, so both directions of `00-context.md` §5 now have a test rather than only the wall-clock half
- [x] 5.3 Index `(professional_id, starts_at_utc)` for the window read, and map the foreign key `Restrict`
- [x] 5.4 Resolve 3b's deferred question now that an instant is actually being stored: whether NodaTime's Npgsql plugin is needed or a value converter suffices. A dependency that is not required should not arrive by reflex
- [x] 5.5 Generate the migration, read the SQL to confirm the column types and the index, and confirm it is additive on top of 3b with dropping the table as a clean rollback

## 6. API: the availability slice

- [x] 6.1 Add `Features/Availability` with `GET /api/availability?appointmentTypeId=&from=&to=[&professionalId=]`, `[Authorize]` with no role policy — availability exposes free time, not patient data (F11, F13)
- [x] 6.2 Implement the bounded input read as **one** place that loads the window's segments, exceptions, active internal blocks, and resource-type feasibility. Do not spread it across the handler
- [x] 6.3 Resolve eligible professionals through the single join `ProfessionalAppointmentType` permits — 3b's qualification gate means the specialty check comes along for free rather than being re-derived (F7)
- [x] 6.4 Validate the window **before** any computation, refusing a reversed or oversized window with `availability.window_invalid` (400)
- [x] 6.5 Refuse an unknown or inactive appointment type or professional with `config.not_found` (404)
- [x] 6.6 Send `Cache-Control: no-store`, consistent with Decision S's refusal to cache this path
- [x] 6.7 Apply rate limiting to the endpoint per `03-nfr.md` §2, which names the availability search as the abusable surface, reusing `auth.rate_limited` rather than minting a near-duplicate code

## 7. API: internal blocks

- [x] 7.1 Add the blocks slice with a caller-scoped collection (`POST` and list) and id-addressed item operations, so creating a block **cannot** name another owner while editing one can be attempted and refused (F11)
- [x] 7.2 Implement create, edit, retire and list, mapping the validity refusal to `block.invalid_range` (422)
- [x] 7.3 Refuse acting on another professional's existing block with `auth.ownership_denied` (403), and refuse administrators and front-desk users entirely with `auth.forbidden` (403)
- [x] 7.4 Confirm the ownership check writes **no** `AccessLog` row: that trail exists for staff reading patients, and widening it here would dilute an audit whose value is its narrowness (F11)

## 8. Staff surface: S3

- [x] 8.1 Add `/staff/blocks` and its navigation entry, visible to professionals only — the first professional-role branch the staff shell has ever rendered (F12)
- [x] 8.2 Build the list plus the dialog primitive 3a introduced; no new primitive is needed
- [x] 8.3 Use native `datetime-local` inputs, interpreted and displayed as clinic wall clock — the input collects a time with no zone attached, which is exactly what the server interprets
- [x] 8.4 Render the `block.invalid_range` refusal inside the dialog, above the buttons, with the stored list untouched
- [x] 8.5 Add pt-BR and en keys for every new string and for `block.invalid_range`, and confirm `pnpm check:i18n` passes both the consistency and the usage scan
- [x] 8.6 Confirm an administrator and a front-desk user see no navigation entry, and that requesting the route directly renders no protected data

## 9. Dev seed

- [x] 9.1 Extend the seed with a block or two for Dra. Helena, constructed through the domain factory as everything else in it is, so the subtraction is visible on a fresh stack without hand-entry — and confirm a restart still does not duplicate

## 10. Integration tests

- [x] 10.1 Availability end to end against the seeded clinic: slots returned for a real appointment type, with the shape the spec describes
- [x] 10.2 The round trip that proves the subtrahend is real: create a block through the API, request availability again, and assert exactly the overlapping slots disappeared and the abutting one did not
- [x] 10.3 Window validation and both `config.not_found` refusals
- [x] 10.4 The authorization matrix: patient, front-desk, administrator and professional all read availability; unauthenticated is `401`; another professional's block is `403 auth.ownership_denied`; administrator and front-desk are `403 auth.forbidden`
- [x] 10.5 Rate limiting trips on the availability endpoint and reports `429`
- [x] 10.6 Specific and any-professional modes over the same data, asserting the specific result is the subset the union contains
- [x] 10.7 Assert the block's start and end columns are timestamp-with-timezone (5.2)

## 11. Documentation

- [x] 11.1 Flip **this change's own** README status cell for increment 4 and the "N are built and running" line (`00-context.md` §8 — its own cell in its own feature commit, which is the off-by-one the last two changes produced)
- [x] 11.2 Confirm the README local-run section needs **no** edit: the scheduling parameters carry defaults, so this change adds no prerequisite a person must set
- [x] 11.3 Widen `auth.rate_limited`'s description in `07-error-codes.md` so it is not read as login-only now that the availability search shares it (6.7)

## 13. Design open question 1, reversed after review

Recorded as its own group rather than by editing group 3 and 4's text, so the task list stays a
record of what was done and in what order.

- [x] 13.1 Replace the boolean resource gate with `ResourceCandidate` — id, turnaround buffer, and a busy-interval seam in the same shape as a professional's, so change 5 fills a list rather than implementing a rule (F6)
- [x] 13.2 Add the resource to `AvailabilitySlot` and to the wire contract, documenting in both that the id explains the answer and is **not** a reservation
- [x] 13.3 Evaluate the resource constraint **per slot**: first free candidate in the caller's order, fall through when one is taken, withhold the slot when all are
- [x] 13.4 Apply the turnaround buffer (*domain-model F1*) to a resource's occupied interval — trailing only, and to resources only, since turnaround belongs to the room and not to the person leaving it
- [x] 13.5 Order the loading step's candidates by name, so "first free" is a stable and explicable choice rather than whatever the database returned
- [x] 13.6 Rewrite the spec requirement and its scenarios: the slot names a resource, the choice is deterministic, exhaustion withholds, the buffer is kept out, and the professional's busy period carries no buffer
- [x] 13.7 Replace the structural "names no resource" unit test with real coverage — the choice, the fall-through, the exhaustion case, a zero buffer, a non-zero buffer, and the professional/resource asymmetry
- [x] 13.8 Integration-test that the room appears on the wire and that the named room is stable across two identical requests
- [x] 13.9 Rewrite design F6 keeping the rejected reasoning, close open question 1 with the decision, and record the residual hazard as a risk **plus a constraint on change 5**: the booking path assigns the resource itself and must not trust a caller-supplied id

## 12. Definition of Done

- [x] 12.1 Unit and integration tests green in CI, integration against a real PostgreSQL via Testcontainers
- [x] 12.2 pt-BR and en keys present for every new user-facing string, `pnpm check:i18n` green
- [x] 12.3 `openspec validate availability-read --strict` passes
- [x] 12.4 The build and `DomainBoundaryTests` confirm `Domain` gained a solver and not infrastructure (3.11)
- [x] 12.5 The demonstrable behaviour works end to end: a professional blocks time in S3, and availability for that professional stops offering those hours
- [ ] 12.6 `validation.md` rewritten to match what was actually built, its checks **run** against the local Compose stack, and its Outcome section recorded — including a plain statement of what was not examined, per the standard 3b set
- [ ] 12.7 Change archived into the living spec, creating the `availability` capability
