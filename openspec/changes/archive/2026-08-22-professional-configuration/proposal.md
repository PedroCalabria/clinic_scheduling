## Why

`clinic-catalog` (3a) established what the clinic offers. Nothing yet says **who can deliver it,
or when** — and without that, change 4's tri-constraint solver has no professional to solve for.
The availability formula in `02-domain-model.md` §4 reads
`WorkingHoursTemplate − WorkingHoursException`, minus busy intervals, *sliced by the
`durationMinutes` from `ProfessionalAppointmentType`*. Every term in that expression except the
catalog arrives here.

This is the second and final half of `clinic-configuration`, and it is where a professional stops
being only an identity. Change 2 gave them a `User` with `Role=Professional` and nothing else; the
row that carries their clinical setup does not exist. This change creates it, connects it to the
catalog, and gives them a week.

It also closes two items that have been recorded as "somewhere later" since planning: the clinic
timezone (Decision H), which becomes real the moment a wall-clock working hour is stored, and the
dev seed, which only becomes possible once a professional can be fully configured.

## What Changes

- **A `Professional` row, born on first configuration.** S7 lists the users an administrator
  already invited in S11 and creates the `Professional` on first save (`00-context.md` §5,
  Fork 1 · iii). `identity-session` keeps owning who they are; this capability owns what they do.
  Nothing in change 2's provisioning is touched.
- **`ProfessionalSpecialty` as a qualification gate, not derived data.** A per-type duration may
  be assigned **only** for an appointment type whose specialty the professional holds; otherwise
  `config.specialty_not_held`. This is what makes invariant I2 checkable at booking time, and it
  is the decision that keeps credentialing (what someone is qualified for) distinct from
  operational configuration (how long they take).
- **`ProfessionalAppointmentType` carrying `durationMinutes`** (Decision C) — the entity that
  lets Dr. A run a cardiology visit in 40 minutes and Dr. B in 50, and the reason
  `AppointmentType` deliberately carries no duration of its own.
- **`WorkingHoursTemplate` and `WorkingHoursException`**, both storing **wall-clock** time. Three
  ambiguities are refused rather than guessed, mirroring 3a's posture: overlapping effective
  ranges for the same weekday (`config.working_hours_overlap`), a segment crossing midnight, and
  `startTime >= endTime` (both `config.working_hours_invalid`).
- **The clinic timezone arrives as configuration, not as conversion.** `Clinic__Timezone` (IANA)
  and NodaTime land here because working hours are the first wall-clock data in the system. The
  wall-clock→UTC conversion itself does **not** — see the design; there is nothing to convert
  against until change 4.
- **S7 — Professionals**, the hardest screen so far: a professional list, a specialty assignment,
  a professional × appointment-type duration matrix, and a working-hours editor.
- **The dev seed completes.** An idempotent, dev-only seed provisions a full runnable clinic —
  3a's catalog plus a configured professional — so every change from `availability-read` on has
  something to demonstrate against instead of ~30 minutes of form-filling.

## Capabilities

### New Capabilities
<!-- None. This change completes an existing capability rather than introducing one. -->

### Modified Capabilities
- `clinic-configuration`: adds the professional half. New requirements for the `Professional`
  row's creation-on-first-save, the specialty qualification gate over per-type durations,
  working-hour templates and per-professional exceptions with their refusal rules, and the dev
  seed. **Modifies** the existing "Only administrators may shape the catalog" and "The staff
  surface presents the catalog to administrators" requirements, whose wording is scoped to the
  catalog and must now cover the professional screens too.

## Impact

- **`Domain`** — five entities in the `Configuration` area alongside 3a's four, plus the
  qualification-gate rule and the working-hours validity rules. Still no infrastructure
  reference.
- **`Api`** — new endpoints in the existing `Features/AdminConfig` slice; one EF migration adding
  five tables; the dev seed as a hosted service in the shape `AdministratorBootstrap` established.
- **`Domain`/`Api` dependency** — NodaTime. It earns its place on one specific ground: converting
  a wall-clock time needs explicit handling of DST-ambiguous and nonexistent local times, and
  Brazil having no DST today means a fixed-offset bug would be invisible in tests and would
  surface only if the law changed or the clinic moved.
- **`docs/07-error-codes.md`** — already carries `config.specialty_not_held`,
  `config.working_hours_overlap`, and `config.working_hours_invalid`, added for this change.
- **`.env.example`** — gains `Clinic__Timezone`, the first configuration this capability reads.
- **`apps/staff`** — one feature folder for S7 and its navigation entry; new pt-BR/en keys.
- **Tests** — unit tests for the gate and the three working-hours rules; integration tests for
  each refusal, the creation-on-first-save behaviour, seed idempotency, and the administrator
  boundary on 3a's `AsRoleAsync` seam.

### Not touched

- **No availability solving and no wall-clock→UTC conversion.** The timezone is configured and
  the hours are stored; interpreting them against a date is change 4's solver. There are no
  appointments to convert against yet, and building the conversion without the query that uses it
  would be guessing at its shape.
- **No change to S11, `User`, or any provisioning rule.** A professional is still invited in S11
  and still claimed by their first Google sign-in. This change reads that user and adds clinical
  configuration beside it.
- **No clinic-wide holiday calendar.** `WorkingHoursException` is per-professional only
  (`02-domain-model.md` §2). A clinic-wide closure is modelled as one exception per professional;
  a shared clinic calendar is a new concept nobody has budgeted.
- **No `CalendarConnection`.** `02-domain-model.md` lists it under the professional, but it is
  change 6's, along with the OAuth scope change 2 deliberately deferred.
- **No `Appointment`, no `TimeBlock`, no `tstzrange`, no `EXCLUDE` constraint, no GiST index, no
  professional-scoped lock (G1).** This change still stores no intervals on a timeline.
- **No Dapper.** Configuration is written and read through EF; the hot read path arrives with the
  solver.
- **No change to how `AppointmentType` works.** It still carries no duration; this change adds
  the junction that does.
- **No further split of this change.** The what/when seam is thin, change 4 needs both halves,
  and the dev seed spans them.
