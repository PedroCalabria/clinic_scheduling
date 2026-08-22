## Why

Change 2 gave the system people; it gave the clinic nothing to schedule. An appointment can
only exist if a professional holds the right specialty, and a free resource of the required
type is available (`02-domain-model.md` §4) — but none of those nouns exist yet, so the
tri-constraint solver of change 4 has nothing to constrain against.

This change delivers the half of `clinic-configuration` that answers **what the clinic
offers**, independent of who works there. It is deliberately the first half: the catalog has
no reference to a professional, while every part of `professional-configuration` (3b) points
at an `AppointmentType` or a `Specialty` that must already exist. Splitting here — rather
than shipping nine entities and four screens at once — keeps each half reviewable in one
sitting, which change 2's design already flagged as strained (`05-openspec-workflow.md`,
decision W).

## What Changes

- **Four catalog entities** in the protected core, per `02-domain-model.md` §2
  (reference/configuration group): `Specialty`; `ResourceType`, carrying the F1 turnaround
  `bufferMinutes` that change 4's availability computation will subtract; `Resource`, a
  concrete room or piece of equipment of one `ResourceType`; and `AppointmentType`, which
  belongs to a `Specialty` and requires a `ResourceType` — the entity that ties two of the
  three scheduling constraints together.
- **Deactivation becomes a domain rule, not a delete.** Soft-delete is already the
  project-wide law (I10), but until now nothing had dependents. Deactivating a catalog entity
  that active records still reference is refused with `config.in_use`; the specific
  reference rules are the substance of this change's spec.
- **Name uniqueness is scoped to active records**, so a name freed by deactivation can be
  used again and a deactivated entity can be reactivated. Collisions return
  `config.duplicate_name`.
- **Three administrator screens** — S8 specialties, S9 resources and resource types, S10
  appointment types (`06-ui-surfaces.md` §4) — mounting into the staff app-shell change 2
  built, behind the administrator policy change 2 established. Reception runs the day; it
  does not reshape the clinic (`01-requirements.md` Phase 2).
- **First `AdminConfig` slice** in `Api`, and the first entities whose relationships the
  schema must express — the migration path's first real foreign keys.

## Capabilities

### New Capabilities
- `clinic-configuration`: the administrator-owned reference data the scheduler reads. This
  change contributes the catalog half — specialties, resource types and their turnaround
  buffer, resources, and appointment types, with the deactivation and uniqueness rules that
  keep the catalog referentially honest. `professional-configuration` (3b) will add the
  professional half to this same capability.

### Modified Capabilities
<!-- None. No requirement in identity-session or platform-health changes: this change
     consumes the administrator policy and the app-shell exactly as change 2 shipped them. -->

## Impact

- **`Domain`** — a new `Configuration` area alongside `Identity`: four entities and the
  reference rules that govern their deactivation. No infrastructure reference, as ever.
- **`Api`** — a new `Features/AdminConfig` slice; one EF migration adding four tables with
  their foreign keys; new EF configurations.
- **`docs/07-error-codes.md`** — already carries `config.in_use`, `config.duplicate_name`,
  and `config.not_found`, added for this change. No new codes are invented here.
- **`apps/staff`** — three feature folders and their navigation entries; new pt-BR/en keys.
  A `dialog` primitive may be the first widget these screens need that
  `packages/shared/src/ui` does not yet provide.
- **Tests** — unit tests for the reference rules; integration tests for each refusal case
  and for the administrator-only boundary, on change 2's `AsRoleAsync` seam.

### Not touched

- **No `Professional` row, `ProfessionalSpecialty`, `ProfessionalAppointmentType`,
  `WorkingHoursTemplate`, or `WorkingHoursException`** — and therefore no S7. A professional
  still has only the identity change 2 gave them. All of it is 3b.
- **No clinic timezone and no NodaTime.** Nothing in this change has a wall-clock time;
  `bufferMinutes` is a duration, not an instant. Decision H first bites in 3b, where
  working hours meet appointments (`00-context.md` §5).
- **No dev seed.** A seeded clinic is only runnable once a professional can be configured,
  so the seed lands whole in 3b rather than half here.
- **No `Appointment`, no `TimeBlock`, no `tstzrange`, no `EXCLUDE` constraint, no GiST
  index, no professional-scoped lock (G1).** This change stores no intervals.
- **No availability solver and no Dapper.** The catalog is written and read through EF; the
  hot read path arrives in change 4.
- **No patient-facing surface.** Nothing here is reachable from the portal, and no
  requirement touches `AccessLog`, consent, or ownership — the catalog is not personal data.
- **No change to the appointment-type duration model.** Duration is per professional × type
  (Decision C) and lives on the 3b junction; `AppointmentType` deliberately carries no
  duration of its own.
