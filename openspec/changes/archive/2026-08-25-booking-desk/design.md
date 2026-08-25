## Context

`booking-core` and `booking-lifecycle` built the whole booking mechanism and pointed every one of
its call sites at a patient. This change turns the other way round and finds most of the work already
done — not by accident, but because 5a and 5b were written knowing 5c was coming and left the seams
named. Four of them are load-bearing here:

- `AppointmentSource.FrontDesk` exists, is documented as reception's, and has never been written.
- `CancellationCutoffPolicy.Permits` takes `cutoffApplies`, and `AppointmentLifecycleTests.cs:258`
  already asserts what happens when it is `false`, in a test whose comment says "nothing passes
  `false` today — this test is what says what happens when 5c does".
- `PatientDataGuard` evaluates the ownership rule and writes the `AccessLog` row from the same
  decision, so the two cannot drift. It has one caller: the patient-profile read.
- `booking.appointment_not_found` is in the catalogue, marked "on a path whose caller is entitled to
  distinguish absence from denial" and "deliberately unreachable" from the patient file.

The constraint that shapes almost everything below is that **`Domain` knows nothing about roles**.
The compiler enforces it. So every piece of authority in this change enters the domain as a *fact* a
caller has already established, never as an identity — the bargain `ProfessionalHoldsDurationForType`
struck in 5a and `cutoffApplies` struck in 5b.

Decisions are numbered **N1–N13** to avoid colliding with the letters already spent: `B` (5a), `C`
(5b), `D` (5a's D7 and `booking-surface`), `E` (3b), `F` (change 4).

## Goals / Non-Goals

**Goals:**

- Reception can see today across professionals, book a walk-in for an existing patient, and change
  an appointment a patient could not — the front-desk cutoff override, finally called.
- A professional can see their own day.
- Every staff read of a patient's name leaves an `AccessLog` row.
- A professional has a real name, entered once on S7, shown everywhere.
- Nothing new is invented: the source, the override and the cutoff authority are wired, not built.

**Non-Goals:**

- Cross-professional reschedule. Still a cancel plus a booking; still what keeps the professional
  advisory lock single-keyed and the two-lock deadlock non-existent rather than solved.
- Calendar propagation (change 6). A staff cancel leaves the same seam a patient cancel leaves.
- Any override of the **minimum lead time** — see N1.
- Extracting the patient `SlotGrid` into `packages/shared`, or copying it — see N4.
- Reception granting consent on a patient's behalf — see the risk table.
- Any change to a patient-facing screen.

## Decisions

### N1 · The override is on cancel and reschedule. Booking was never governed by the cutoff

The brief for this change asks reception to "book a walk-in **inside the 24 h cutoff**". Taken
literally that is not a thing the system can do, and the reason is worth stating rather than quietly
routing around: `Appointment.Book` takes no `CancellationCutoffPolicy` and never has. What bounds a
new appointment near in time is `SchedulingParameters.MinimumLeadTime` (I8, default 60 minutes),
refused as `booking.lead_time_violation`. The cutoff (F3, default 24 hours) governs only whether an
appointment that already exists may be undone, which is why `CancellationCutoffPolicy` is a separate
type with a comment explaining that it is deliberately not a fourth field on `SchedulingParameters`.

So the override is exercised where the authority parameter actually lives:

- **Reception cancels or reschedules inside the cutoff** — `cutoffApplies: false`. This is the second
  caller of the parameter 5b built, and it makes the behavior `AppointmentLifecycleTests.cs:258`
  specifies reachable from the product rather than only from a unit test.
- **Reception books a walk-in for later today** — ordinary. It succeeds because the lead time permits
  it. Reception does **not** override the lead time, and N1's whole point is that these are two rules
  with two codes and only one of them has an authority parameter.

*Alternative considered:* give `Book` a lead-time authority too, so "front desk books with no notice"
becomes possible. Rejected: the lead time is the one number the read and the write share so that
availability cannot offer what booking refuses (`SchedulingParameters`' own comment). A staff-only
relaxation makes the staff availability view either lie or need a second solver configuration. A
clinic that takes zero-notice walk-ins sets `Scheduling__MinimumLeadTimeMinutes=0`, which the domain
already documents as legitimate.

*Consequence for the validation guide:* the demo has **two** acts, not one. Booking the walk-in shows
the source and the room; cancelling or moving an appointment three hours out — after watching the
patient be refused on P5 — is the one that shows RBAC and ownership coexisting.

### N2 · Staff act through the same routes, role-gated, not through parallel `/api/staff/*` routes

`POST /api/appointments`, `POST /api/appointments/{id}/cancel` and `.../reschedule` widen their
policies and branch inside. They do not acquire mirrors.

The reschedule path is the argument. Its statement ordering — UPDATE the old row to `Rescheduled`
*before* INSERT the replacement, because the partial `EXCLUDE` indexes are non-deferrable — is a
correctness property with a fifteen-line comment above it saying so, and a naive test passes while it
is wrong. A second implementation of that path is a second place for it to be got subtly wrong, and
the failure mode is "a near reschedule always fails" in production only.

The catalogue also already assumed this shape: `booking.appointment_not_found` was reserved for "a
path whose caller is entitled to distinguish absence from denial". That is a *branch* description,
not a route description — a patient naming an unknown id still gets `auth.ownership_denied`, and a
staff caller gets the 404.

*Alternative considered:* `/api/staff/appointments/...`, which reads more explicitly and would let
the policies stay narrow. Rejected for the duplication above. The mitigation for the lost
explicitness is that the role branch is one helper (`ActingFor`) shared by all three handlers, and
every role × route combination is an integration test.

### N3 · An explicit patient identifier from a non-staff caller is refused, not ignored

`BookAppointmentRequest` gains `PatientId`. When the caller is a patient, supplying it is
`auth.forbidden` (403). When the caller is front desk or administrator, omitting it is
`validation.required` — staff have no patient record to fall back to.

The identity-session spec says a client-supplied identifier "never widens access", which admits two
readings: refuse, or ignore and use the session. Ignoring is worse here, and specifically because the
operation *writes*. A client that believed it was booking for someone else would have booked for
itself, successfully, and the mistake surfaces when the wrong person arrives at the clinic. Refusing
costs one branch and makes the mistake immediate.

The existing scenario **"Staff cannot book through the patient path" becomes false** and is replaced
rather than deleted — the new statement is that the path admits staff *only* with an explicit patient,
which is a stronger claim than the one it retires.

### N4 · S5 builds a utilitarian staff availability view over the same API — the patient grid is neither shared nor copied

Locked as option (c) before this change was drafted; recorded here with its reasoning.

S5 calls `GET /api/availability` exactly as P2 does and renders its own table. It does not import
`SlotGrid`, and `SlotGrid` is not moved to `packages/shared`.

The two screens disagree about almost everything except the data. P2 is the designed showcase (`06`
Z2), centers a bounded column, hides the room by decision (D7), and carries a trust panel naming the
external calendar — a claim change 7 makes true and a receptionist would immediately know is not.
S5 is a tool used with a patient waiting: dense rows, the room shown, no trust panel, no selection
ceremony. A shared component serving both would need a prop for each of those differences, and a
primitive with five behavioural switches is two components with extra steps. Duplication of ~80 lines
of table markup is the cheaper honest answer.

*Revisit trigger:* if a third availability surface appears (change 7's reconciliation queue is the
candidate), extract then, from three real examples rather than two speculative ones.

### N5 · The room reaches the staff surfaces as a name on the availability slot; D7 is rescoped, not weakened

`AvailabilitySlotResponse` gains `ResourceName` beside the `ResourceId` it already carries.

D7 said no room is ever named on patient surfaces. That was, and remains, a statement about what a
*patient surface renders* — the wire has carried `resourceId` since change 4, and the patient portal
simply does not display it. Adding the label does not disclose anything structurally new about the
clinic to a patient who was already being handed the room's identity; it lets S4 and S5 show a room
without a second round trip.

*Alternatives considered:*

- **Widen `GET /api/resources` from Administrator to ClinicStaff** and join client-side. Rejected: it
  falsifies a shipped `clinic-configuration` scenario ("a front-desk user ... requesting those
  screens' endpoints directly is still refused by the API") in order to obtain a label. A security
  boundary should not move to save a join.
- **A role-conditioned payload** — the name present only for staff. Rejected: a response whose shape
  depends on who asked cannot be described by one generated type, and the client then has to handle
  a field that is sometimes absent for reasons it cannot see.

The staff booking response likewise names the room actually assigned; the patient's
`AppointmentResponse` is untouched, keeping 5a's "no room" comment true where it was written.

**Honesty note carried into the screen:** a slot's room is the room that *would* be used —
change 4's own words, "an explanation, not a reservation". S5 labels it as such, and the room shown
after the booking is the assigned one, read back from the created appointment.

### N6 · The professional's allowance enters `Domain` as a relationship fact, not as a lookup

`PatientDataAccess.Evaluate` refuses `Role.Professional` today under a comment promising the
allowance would arrive "with the scoping ... that makes the access defensible, rather than a blanket
allow granted early just in case". The scoping is: **the patients on this professional's own
schedule**.

The signature grows one parameter:

```
Evaluate(Role actorRole, Guid actorUserId, Guid patientUserId, bool actorIsThisPatientsProfessional)
```

`Domain` cannot answer that question — it has no database, and the compiler enforces that it never
will. So the caller establishes it and hands it over as a fact, exactly as `cutoffApplies` and
`ProfessionalHoldsDurationForType` are handed over. For the day read the fact is free: the
appointments *are* the relationship, so the query that produced the list has already answered it.

`Role.FrontDesk` and `Role.Administrator` are unchanged — allowed, and recorded.

*Alternative considered:* let the professional role through unconditionally, since S1 only ever
queries their own schedule anyway. Rejected: it makes the guard permissive for every *future* caller
rather than for this one, which is precisely the "just in case" the existing comment refuses.

### N7 · `AccessLog` is written on the read that discloses names, once per request, for the patients actually disclosed

`PatientDataGuard` gains a set-shaped entry point. It evaluates the same domain rule per patient and
adds every required record, then saves **once**. A day with thirty appointments is thirty rows in one
statement, not thirty transactions.

Three placement decisions, each easy to get wrong by omission:

1. **The day read logs; the quick actions do not.** Cancelling or moving an appointment is not
   reaching a patient's personal data — `PatientDataAction` has `Viewed` and `Updated`, and both are
   about the patient record. The appointment is not PII. Reception necessarily read the name on S4
   before acting, and that read is already recorded. Logging the action too would double-count the
   same disclosure.
2. **The patient lookup on S5 logs** (N8), because it discloses a name in response to a search.
3. **Nothing is logged for a professional reading their own blocks**, which contain no patient.

The record is saved before the payload is produced, keeping the existing guard's property: "this
staff member looked" is true the moment they looked, whether or not the rest of the request
succeeded.

### N8 · Reception finds a patient by exact contact email, not by name search

S5 needs to resolve a walk-in to a patient id. `GET /api/patients/by-email?email=...`, front desk and
administrator only, returns at most one patient — id, name, and whether they currently hold an active
data-processing consent at the configured version.

*Alternative considered:* a name-substring search. Rejected on two grounds. It is a patient
enumeration surface — type "a" and read the register — which is exactly what this project's
non-disclosure rules exist to prevent, and every result would have to be logged, which turns the
access log into keystroke noise and buries the entries that matter. Asking a returning patient for
their email is how a clinic identifies them anyway.

*Trade-off, recorded — and worse than first written.* A patient who does not remember which
address they used is not findable from S5. This paragraph originally named "the administrator's user
list" as the remedy, and **that was false**: S11 lists `user.Role != Role.Patient`, so no screen in
this product lists patients at all. The dead end is total, for every role. Corrected here rather
than left to be discovered.

Two things soften it. A patient provisioned through Google carries their Google address as
`contactEmail`, so "which address do you sign in with?" is answerable in most cases. And a typed
address that matches nobody returns nothing, rather than somebody else.

**Revisit trigger, and what it should NOT reach for.** If real use shows the address is routinely
unknown, the answer is *not* the obvious one. A second exact identifier — the telephone — was
considered and deliberately deferred (see the answered questions at the end of this document), for
three reasons that only appear on inspection: `ContactPhone` is stored as free-form text, so an
exact match fails on a country code or a space; making it work needs normalisation, which is a
column, a migration and a default-country setting; and the field **starts empty on every patient**,
so the remedy would be missing in precisely the case that needs it. Whatever the answer turns out to
be, adding it is additive — `by-phone` beside `by-email`, reusing this path's guard, its access
record and its non-disclosure rule — so deferring costs a sibling route later, not a rewrite.

The consent flag is returned because the alternative is a receptionist discovering `auth.consent_required`
after taking the patient's time, and it costs one boolean over a query the booking gate already runs.

### N9 · One read serves S1 and S4, and the professional's scope is structural

`GET /api/schedule?date=<clinic date>[&professionalId=]`, returning the day's appointments and
internal blocks with the professional's name, the patient's name, the appointment type, the room, and
the instants.

The `professionalId` parameter is honoured for front desk and administrator and **ignored** for a
professional, whose scope is their own id from the session. That is the same shape as
`SaveTimeBlockRequest` carrying no professional: "a professional cannot aim this at somebody else" is
structural rather than a check somebody has to remember.

*Alternative considered:* two routes, `/api/schedule/mine` and `/api/schedule/day`. Rejected: the
payload is identical, and two routes means the `AccessLog` write exists twice — the highest-cost
duplication in this change to get wrong.

Blocks are included for both audiences. A professional's day is incomplete without them (`06` S1 says
so), and a receptionist running the day needs to see that a professional has declared themselves
unavailable rather than infer it from a gap.

### N10 · `Professional.FullName` is nullable, and the derived label stays as the fallback

The column is nullable because the record it lives on does not exist until first configuration, and
S7 deliberately lists invited-but-unconfigured professionals (a shipped `clinic-configuration`
scenario). Entering the name is one of the saves that can create the record.

`BookingOptionsEndpoints.DisplayName` therefore becomes: the stored name when there is one, the
derived local-part label when there is not. The field on the wire keeps its name, which is what
`booking-core` bought by calling it `displayName` rather than `email` — **no client changes**.

*Alternative considered:* backfill every existing professional's name from the derived label in the
migration, and make the column required. Rejected: it converts a placeholder into stored data that
looks entered, and an administrator can no longer tell which names are real.

### N11 · S4's quick actions are cancel, and move — and move reuses S5's view

Cancel is inline, with an explicit confirmation, and reports what the server said. Move navigates to
the S5 availability view scoped to that appointment's professional and appointment type, exactly as
P6 reuses P2's search — one staff availability view serving both jobs, which is also what makes N4's
"do not share with the patient grid" affordable.

Both act inside the cutoff. The screen does not compute the cutoff: the read carries `CanChange` for
the *patient*, so S4 can honestly say "the patient cannot change this — you can", which is the
sentence that makes the demo legible. The server decides; the browser's clock is not the clinic's
(5b's C10, and the reason `CanChange` exists at all).

### N12 · The `primary-subtle` fix is exactly two variants

`booking-surface`'s task 2.3 left the `success` Alert and the `active` Badge filled with
`primary-subtle`, the colour the design system reserves for a bookable slot, and named the revisit
trigger: "they serve the staff console too". Three staff screens now do.

- `Alert` `success`: keeps `border-primary` and `text-primary-strong`, background becomes
  `surface-raised`. The border already carries the semantic; the fill was the part that was borrowed.
- `Badge` `active`: an outline rather than a fill — `border border-primary`, `text-primary-strong`,
  the page's own surface behind it. A fill of `surface-raised` would have made `active` and `neutral`
  differ by text colour alone, which the badge's own comment forbids.

Nothing else in the primitives is touched. This is the deferral being closed, not a refactor wearing
its clothes.

### N13 · The staff surfaces are utilitarian, and that is a decision rather than a shortfall

`06` Z2 is explicit: the patient portal is the designed showcase, staff surfaces are tables and
forms. S1, S4 and S5 are built from the existing app-shell, `Table`, `Badge` and `Alert`, with
`font-mono` tabular figures on every time, duration and count — the rule the staff console already
honours everywhere and the booking flow had to be taught in `booking-surface`. No new primitive is
added.

## Risks / Trade-offs

| Risk | Mitigation |
|---|---|
| A widened policy is the whole security boundary for three routes, and a wrong one is invisible until exploited | Every role × route combination is an integration test, including the two that must fail: a patient supplying `patientId`, and a professional attempting to book on behalf. The role branch is one shared helper rather than three copies |
| Reception cannot grant data-processing consent for a patient, so a walk-in whose consent is withdrawn or stale is refused with `auth.consent_required` and reception can only tell them to open the portal | **Kept deliberately.** Dropping the gate for staff would let the clinic route around a patient's own withdrawal by telephoning reception, which is the wrong way round. N8's lookup surfaces the consent state *before* the receptionist takes the patient's time. **Revisit trigger:** a staff-witnessed consent capture is a real feature with its own versioning and evidence requirements, and it is not this change |
| `AccessLog` is now written on a read path that runs on every day-view load, including the receptionist who refreshes | One save per request, tens of rows. The alternative — not recording — is the failure this change exists partly to prevent, and it fails silently |
| The day read joins appointments, patients, users, professionals, types and resources for a whole day | Bounded by one clinic-day; EF read with `AsNoTracking`. If it ever matters, it is a Dapper read like availability's — recorded, not pre-optimised |
| `booking-surface` archived with its keyboard-path check unrun, and this change adds three more screens | S4 and S5 carry keyboard and both-locale checks in `validation.md` as judgement checks with written outcomes, not ticks |
| The demo's headline sentence changes shape because of N1 | Stated in the proposal and in `validation.md` rather than reconciled silently. The RBAC + ownership demonstration is *stronger* as a cancel inside the cutoff, because the patient is visibly refused first |

## Migration Plan

One EF migration: `professionals.full_name`, `text`, nullable. No backfill (N10), no data movement,
no constraint. Reversible by dropping the column; nothing else in this change is schema-bearing —
`AccessLog`, `AppointmentSource` and the cutoff authority are all existing structure.

Deployment is ordinary: the migration is additive and the previous API version ignores the column, so
there is no ordering requirement between migrating and deploying.

## Open Questions — answered 2026-08-25

Answered by the maintainer after the validation guide was run, so each of these is a decision taken
against a screen somebody had used rather than against a description of one.

1. **Does S4 want the day, or the day plus the next?** **One day, kept.** The deciding argument is
   not screen space: showing two days would **double the `AccessLog` rows on every open**, disclosing
   every one of tomorrow's patients whether or not anybody looked. A trail that records "somebody
   opened the screen" rather than "somebody read this patient's data" answers a weaker question than
   the one `02-domain-model.md` §8 promises, and it degrades quietly. Tomorrow stays one click away.
2. **Is the room useful on S1?** **Kept.** The room is assigned by the server (F2), so a
   professional cannot infer which one they are in — it is information available nowhere else. The
   cost of being wrong here is one line in a component, which is why it did not warrant more
   deliberation. If a professional in a single-room clinic calls it noise, the refinement is to show
   the column only when a day contains more than one distinct room.
3. **N8's email-only lookup.** **Kept, with the dead end recorded honestly and the telephone
   deliberately deferred.** See the corrected trade-off under N8: the remedy this document first
   claimed does not exist, and the obvious second identifier is not the cheap addition it appears to
   be. Deferring is safe because a second exact lookup is purely additive.
4. **Does S4 need to show `AppointmentSource`?** **Yes, and it is built** — this question said "not
   built" and was already stale when it was written; the column shipped with the screen. It is the
   only read of `AppointmentSource` anywhere in the system, so without it the enum would be written
   and never looked at. Kept after being seen on a real screen without the table feeling crowded. If
   it ever does, the refinement is to mark only the desk-booked rows rather than carry a column.

## Decided against

**A view of every appointment for one professional, across dates.** Raised while reviewing this
change and **declined**. The day read is by date and narrowable to one professional, which answers
"who is Dr X seeing today"; nothing answers "everything Dr X has, ever". Worth noting the asymmetry
it leaves: a patient sees all of their own appointments (P5, unbounded by date), and reception does
not see all of a professional's. That is a real gap, it is not what `06`'s S4 was scoped to be
("run the day"), and it is now a decision rather than an omission. Should it ever be wanted, the
shape is a date range on the existing read — the SQL already matches on `tstzrange` overlap and
there is precedent for a bounded window in `Scheduling__MaxWindowDays` — plus a screen that is a
list rather than an agenda, which is the actual work.
