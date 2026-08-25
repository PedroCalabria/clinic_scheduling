## Why

Five changes have built a clinic that only patients can use. The `Appointment` aggregate, the
tri-constraint solver, the three enforcement floors, the cutoff and the ownership guard are all in
place — and every screen that reaches them lives in `apps/patient-portal`. `apps/staff` has an
app-shell, a catalog, a professional's blocked time, and nothing that shows an appointment. A
receptionist cannot see today.

This is the last of the three booking slices, and it is unusually pre-cut. `AppointmentSource.FrontDesk`
has been a shipped, unused enum value since 5a. `cutoffApplies` was threaded through the aggregate in
5b with four call sites all passing `true`, and a domain test — `AppointmentLifecycleTests.cs:258` —
already states what happens when something passes `false`, in a comment naming this change as the
thing that will. `AccessLog` and `PatientDataGuard` have been writing rows since change 2.
`booking.appointment_not_found` was reserved in the catalogue "for the staff paths `booking-desk`
adds". The work here is mostly to **call** what already exists, from three screens that do not.

## What Changes

**Three staff screens, none of which exist today.** S1 — a professional's own schedule. S4 — the day
across professionals, with the room and quick actions. S5 — book on behalf, for a walk-in or a phone
call.

**Booking gains an authorized staff path.** `POST /api/appointments` takes the patient from
`actor.UserId()` today and the contract carries no such field, deliberately. It gains one,
**role-gated**: supplied by a front-desk or administrator caller, refused from anyone else. A patient
sending it is refused rather than silently ignored — ignoring it means a caller who believed they had
booked for somebody else booked for themselves, and nobody finds out until the wrong person arrives.
The staff booking records `AppointmentSource.FrontDesk`, the first time that value is ever written.

**The cancel and reschedule paths gain the same staff widening, and that is where the override
lives.** This corrects a category error worth naming plainly: **the cutoff has never governed
booking**. A new appointment is bounded by the minimum lead time (I8), not by
`CancellationCutoffPolicy`, and `Appointment.Book` takes no cutoff at all. So "reception books a
walk-in inside the cutoff" cannot be the thing that exercises the override — the override is
`cutoffApplies: false` on `Cancel` and `RescheduleTo`, and the only caller who can pass it is
reception acting on an appointment the patient was just refused. S4's quick actions are therefore in
scope, and they are where 5b's authority parameter finally acquires a second caller. Booking a
walk-in for later today stays in scope and stays ordinary: it succeeds because the lead time permits
it, which is a different rule with a different code (`booking.lead_time_violation`), and reception
does **not** override that one.

**A professional gets a name.** P-5 in `02-domain-model.md` §10, open since 3b. `Professional`
carries no name; `booking-core` derived a patient-facing label from the account's local part behind a
`displayName` field precisely so that this change would be server-only. The column lands, S7 grows
the field, and the derivation becomes the fallback for a professional whose name has not been entered
yet rather than the answer.

**A professional may see their own patients, and that access is recorded.** `PatientDataAccess`
refuses the professional role today, under a comment saying the allowance should arrive "with the
scoping that makes the access defensible, rather than a blanket allow granted early just in case".
That requirement now exists, and the scoping is exact: the patients on this professional's own
schedule. S1 and S4 are the first screens in the product that put a patient's name in front of
somebody who is not that patient, so they are the first that must write an `AccessLog` row. The
`TimeBlock` path documented *not* writing one; this is where the trail becomes load-bearing, and
omitting it would break an LGPD claim silently rather than loudly.

**An availability slot names its room.** The response already carries `resourceId`; it gains the
room's name so S4 and S5 can show a room without a second lookup. D7 — "no room is named on patient
surfaces" — is restated rather than weakened: it was always a rule about what a *patient surface
renders*, and the wire has carried the room's identity since change 4.

**The two shared primitives `booking-surface` deferred are fixed.** Its task 2.3 left the `success`
Alert and the `active` Badge filled with `primary-subtle` — the colour the design system reserves for
a bookable slot — and recorded the revisit trigger as "they serve the staff console too". Three staff
screens now do. The fix is bounded to those two variants; it is not a pass over the primitives.

## Capabilities

### New Capabilities

None. Four existing capabilities gain requirements; none of the six is new.

### Modified Capabilities

- `booking`: **four requirements change, four are added.** *"A patient books a slot by naming its
  instant"* and *"Booking belongs to the patient role"* gain the role-gated patient identifier — the
  shipped scenario **"Staff cannot book through the patient path" becomes false**, replaced by an
  authorized staff path plus a refusal for a patient who supplies the field. *"An appointment may only
  be changed by the patient it belongs to"* gains the staff path, on which an unknown id is
  `booking.appointment_not_found` rather than `auth.ownership_denied`, because staff are entitled to
  distinguish absence from denial. *"Changing an appointment is refused inside the cancellation
  cutoff, for those the cutoff applies to"* gains the concrete caller the cutoff does not apply to,
  and states that the cutoff never bore on booking. Added: the recorded booking source, the day read
  serving S1 and S4, reception's exact-email patient lookup, and the staff console's three surfaces.
- `clinic-configuration`: a professional's configuration record carries their **name**, entered on
  S7, and that name is what the rest of the system shows.
- `identity-session`: *"Patient data is protected by ownership, not only by role"* gains the
  professional's scoped allowance — the patients on their own schedule, and no others. *"Staff access
  to patient personal data is recorded"* extends to that access.
- `availability`: *"A slot names a free resource of the appointment type's required resource type"* —
  the slot now names it **by name** as well as by id.

## Impact

| Area | Change |
|---|---|
| `apps/api` — Domain | `Professional.FullName`; `PatientDataAccess` takes a *relationship fact* for the professional role, in the same shape as `cutoffApplies` and `ProfessionalHoldsDurationForType`. No new mechanism: the override, the source and the cutoff authority all exist already |
| `apps/api` — Api | `POST /api/appointments` and the two lifecycle routes widen into role-gated staff paths; one new read serves both the day view and a professional's own schedule; one exact-email patient lookup for reception; `PatientDataGuard` gains a set-shaped entry point so a whole day writes its records in one save; `resourceName` on the availability slot; the professional label switches source |
| Migration | One: `professionals.full_name`, nullable — an invited-but-unconfigured professional has no record at all, so the column cannot be required |
| `packages/shared` | The `success` Alert and `active` Badge stop using `primary-subtle`; generated types for the new read; i18n keys for three screens in pt-BR and en |
| `apps/staff` | S1, S4, S5 with their routes and navigation entries — the first staff screens that render an appointment |
| `apps/patient-portal` | **Nothing**, beyond the two shared-primitive variants changing colour |
| `docs/` | `02-domain-model.md` §10 closes P-5; `07-error-codes.md` gains no code — every refusal here is already catalogued |
| Dependencies | None |
| **Deliberately not done** | A front-desk override of the **minimum lead time**. It is a read/write agreement the solver applies from one value (`SchedulingParameters`), so relaxing it for one caller would make the staff availability view offer what booking refuses, or the reverse. A clinic that genuinely takes zero-notice walk-ins configures `Scheduling__MinimumLeadTimeMinutes=0`, which the domain already calls legitimate |
| Not touched | Cross-professional reschedule (still a cancel plus a booking, and still what keeps the professional lock single-keyed); calendar propagation, which is change 6 and stays a seam; the patient portal's screens; the patient `SlotGrid`, neither extracted to `packages/shared` nor duplicated; `GET /api/resources`, which stays administrator-only |
