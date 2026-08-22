## Context

3a's entities were nouns that stood still. This change introduces the first data whose **meaning
depends on when you ask** — a recurring weekly pattern — and that is what makes it more than the
five CRUD tables it looks like.

The load-bearing observation, which shapes decision E3 and therefore the size of this change:
**"every Monday, 09:00 to 12:00" cannot be stored as instants at all.** It is not an event; it is
a rule that generates events once you supply a date. Converting it on write would require picking
a date, and under daylight saving the same rule yields different UTC offsets across the year. So
the wall-clock representation is not a deferral of the real model — it *is* the real model, and
the conversion belongs wherever a concrete date enters, which is change 4's solver.

Brazil abolished DST in 2019, which is precisely why this deserves stating rather than assuming:
a fixed-offset implementation would pass every test anyone writes today and would break only if
the law changed or the clinic moved. That is the shape of bug this project's whole posture exists
to avoid.

Two inherited decisions arrive pre-made and are treated as settled here: the `Professional` row is
created separately from the `User` (`00-context.md` §5, Fork 1 · iii), and `ProfessionalSpecialty`
is the qualification gate behind invariant I2 (`02-domain-model.md` §6). This design records *how*
they are honoured and what they cost, not whether to adopt them.

## Goals / Non-Goals

**Goals:**

- Leave change 4 nothing to invent: every term of the availability formula except busy intervals
  is stored, indexed, and reachable — a professional's segments, their exceptions, their eligible
  appointment types, and the duration for each.
- Make the qualification gate visible in the screen, not merely enforced at the API. An
  administrator should not discover the rule by being refused.
- Refuse every ambiguous working-hour input rather than interpret it, matching 3a's posture.
- Make a runnable clinic one command away, so change 4's first demo is not thirty minutes of
  form-filling.

**Non-Goals:**

- No wall-clock→UTC conversion (E3). No solver, no intervals, no `tstzrange`.
- No clinic-wide calendar (E4).
- No self-service. A professional configuring their own qualifications is not a feature.
- No calendar connection — change 6, with the OAuth scope change 2 deferred.
- No history of configuration edits. Knowing that a duration was 40 minutes last March is not a
  requirement; and I1 already protects existing appointments by baking their duration in at
  booking time.

## Decisions

### E1 — The `Professional` row is born on first save, and S7 lists users

S7's list comes from `User where Role = Professional`, left-joined to the configuration record.
Saving anything for a professional who has none creates it.

This is Fork 1 · option iii, and the reason it wins is a boundary rather than a convenience:
`identity-session` owns who someone is, `clinic-configuration` owns what they do clinically.
Option (i) — having S11's invite also write the `Professional` row — would put a
clinic-configuration write inside the identity slice, which is exactly the coupling the capability
split exists to prevent. Option (ii) — creating the row on first Google sign-in — fails an actual
requirement: an administrator must be able to prepare a schedule *before* someone's first sign-in,
which the spec asserts directly. Option (iv) — moving the invite into S7 and leaving S11 to
front-desk and administrator accounts — is arguably the better product, and is rejected only
because S11 is already on `main` and rewriting shipped identity behaviour to improve an adjacent
screen is a poor trade at this point in the build.

**Trade-off, stated plainly:** the list has two sources, so "a professional" is a join rather than
a row, and every query about professionals must decide whether an unconfigured one counts. The
spec resolves that explicitly — they are listed, and distinguishable. **Revisit trigger:** if a
third screen needs the same join with the same "does it count?" question, the answer is a single
read model, not a third hand-written join.

### E2 — `ProfessionalSpecialty` is credentialing; it is not derivable from the durations

A duration may be set only for an appointment type whose specialty the professional holds
(`config.specialty_not_held`, 422). Removing a specialty that active durations depend on is
refused with `config.in_use`.

The alternative is genuinely tempting and worth spelling out: an `AppointmentType` already belongs
to a `Specialty`, so a professional with a duration for "cardiology consultation" evidently does
cardiology — the junction looks like duplicate data, and dropping it would remove a table and a
screen section. It is rejected on two grounds. First, a professional legitimately holds a specialty
with no durations configured yet; that is the normal state right after invitation, and a derived
model cannot represent it. Second, the two answer different questions: *what is this person
qualified for* is a credentialing fact that an administrator asserts, and *how long do they take*
is operational configuration. Collapsing them means the only way to revoke a qualification is to
delete operational data, which is the wrong lever.

Enforcing it in this direction also makes I2 cheap at booking time: change 5 checks one junction
instead of walking from professional to durations to types to specialties.

**Alternative considered:** enforce the gate only at booking (change 5), letting configuration hold
contradictory data meanwhile. Rejected — it defers the refusal to the moment a patient is trying
to book, which is the worst possible time to discover a configuration error.

### E3 — The timezone is configured here; the conversion is not

`Clinic__Timezone` (an IANA zone id) becomes required configuration, validated at startup against
the zone database, with startup **failing** rather than falling back. Working hours are stored as
wall-clock `LocalTime`/day-of-week and are never converted in this change.

Storing UTC instants is not "more correct but premature" — for a recurring rule it is wrong, per
the Context above. Storing a fixed offset (`-03:00`) is the trap: it works, forever, until it
doesn't, and no test written today would notice.

NodaTime arrives now even though most of its value lands in change 4. It earns the dependency here
on two narrow grounds: it is what validates that a configured zone id is real, and its
`LocalTime`/`IsoDayOfWeek` types make "this is a wall-clock time, not an instant" a compile-time
distinction rather than a comment. `TimeOnly` plus `TimeZoneInfo` could cover this change alone,
but change 4 needs `ZonedDateTime` and the DST-ambiguity handling that `TimeZoneInfo` makes
awkward, and introducing the concept twice is worse than introducing it once.

**Trade-off:** a new required environment variable means an existing deployment fails to start
until `.env` is updated. That is deliberate — the alternative is a silent default that is wrong for
every clinic but one — and it is why the refusal message names the setting. `.env.example` is
updated in the same change, per the convention change 1 set.

**Revisit trigger:** none expected. If per-professional or per-resource timezones ever appear, that
contradicts the single-clinic anti-scope and is a much larger conversation than a config key.

### E4 — Exceptions are per-professional, and a clinic holiday is N exceptions

`WorkingHoursException` names one professional and one date. A clinic-wide closure is entered once
per professional.

A shared clinic calendar is a new first-class concept: it needs its own screen, its own
precedence rules against individual exceptions, and a decision about whether it also blocks rooms.
None of that is budgeted, and `02-domain-model.md` §2 now records the same conclusion.

**Trade-off, and it is real:** a clinic with twelve professionals enters a holiday twelve times,
and can get it wrong once. **Revisit trigger:** the first time a real user complains about
repetition, or the first feature that needs to close the clinic without enumerating people —
at which point the design question is precedence, not storage.

### E5 — Ambiguous hours are refused; a split day is not ambiguous

Three refusals: overlapping segments (`config.working_hours_overlap`, 409), a segment whose end is
not after its start — which covers both midnight-crossing and zero-length —
(`config.working_hours_invalid`, 422).

The precise definition of "overlap" matters, and is the part most likely to be implemented wrongly.
A segment has **two** ranges: the dates it applies over, and the time of day. Two segments conflict
only when **both** overlap:

```
  Monday, effective Jan–Jun, 08:00–12:00
  Monday, effective Jan–Jun, 13:00–17:00   → allowed  (times disjoint)

  Monday, effective Jan–Jun, 08:00–12:00
  Monday, effective Apr–Dec, 10:00–14:00   → refused  (both overlap)

  Monday, effective Jan–Mar, 08:00–12:00
  Monday, effective Apr–Dec, 08:00–12:00   → allowed  (dates disjoint)
```

A morning and an afternoon block on the same weekday is an ordinary clinic schedule, so treating
"same weekday" alone as a conflict would refuse the most common real input. Conversely, checking
only the time of day would refuse a legitimate schedule change that takes effect in April.

Midnight-crossing is refused rather than auto-split into two segments. An overnight shift is not a
real case for an outpatient clinic, and silently turning one input into two records is the system
deciding on the administrator's behalf — the same instinct rejected for reconciliation conflicts
and for cascade-reactivation in 3a.

**Revisit trigger:** a genuine 24-hour or overnight service. Then midnight-crossing becomes a
modelled case with an explicit representation, not a relaxed validation.

### E6 — The seed goes through the domain, as a hosted service, gated on environment

The seed is a hosted service in the shape `AdministratorBootstrap` established: it runs at startup,
it is idempotent, and it constructs entities through the same domain factories the API uses.

Going through the domain is the point. A seed written as raw SQL or as EF `HasData` can create data
the API would have refused — a duration outside a held specialty, an overlapping segment — and then
the demo shows a state the product cannot produce. Constructing through the factories means the
seed breaks loudly the moment it contradicts a rule, which makes it a second, cheap test of the
rules rather than a liability.

`HasData` is rejected specifically because it binds seed data to migrations, and migrations run in
production.

**Trade-off:** the seed is code that must be maintained as the model grows. Accepted, because the
alternative is every later change paying the form-filling tax. **Revisit trigger:** if the seed
starts needing conditional logic about what already exists beyond simple presence checks, it wants
to become a fixture the tests share rather than a startup service.

### E7 — S7 is master-detail, and the matrix shows only what the gate permits

A professional list on the left or top, and a detail view with three sections: specialties held,
the duration matrix, and the hours editor. The matrix's rows are the appointment types belonging to
the specialties the professional holds — so the gate is a visible consequence rather than a
surprise refusal.

Separate routes per section were considered and rejected: the three sections are one task
("configure this person"), and splitting them would make an administrator navigate three times to
finish one job. The dialog primitive 3a introduced carries the add/edit forms.

**Trade-off:** assigning specialties and setting durations become an ordered task — the matrix is
empty until a specialty is held. Mitigated by the matrix explaining why it is empty rather than
merely being empty.

### E8 — S7 reuses 3a's catalog endpoints

3a's design left this open on purpose. Resolved: S7 reads the existing
`/api/config/specialties` and `/api/config/appointment-types` and filters to active client-side,
because both are already administrator-only and both already return exactly what S7 needs. No new
read shape is introduced speculatively.

## Risks / Trade-offs

- **The two-source professional list** (E1) means every professional query decides whether an
  unconfigured one counts, and a later slice can answer differently from this one. → Mitigation:
  the spec asserts the answer for S7, and there is one place that builds the join. **Revisit
  trigger:** a third consumer of the same question.

- **The overlap rule is the most likely thing to get wrong**, because it is two-dimensional (E5)
  and the naive one-dimensional version passes the obvious test. → Mitigation: the three cases in
  the E5 table become three explicit tests, including the two that must be *allowed* — a rule that
  only has refusal tests is a rule nobody has proven is not simply always refusing.

- **Storing wall-clock hands change 4 a real problem rather than solving it here.** → Judged
  correct rather than merely convenient (see Context), but the consequence is that change 4 carries
  the DST-ambiguity handling and the first genuine timezone test. Recorded so it is not a surprise.

- **NodaTime does comparatively little in this change.** A reviewer could reasonably ask why it is
  not `TimeOnly`. → The answer is recorded in E3; if change 4 turned out not to need
  `ZonedDateTime`, this dependency would have been premature. That is the honest exposure.

- **The seed can drift from what the screens produce** if it bypasses a rule added later. →
  Mitigation: it constructs through domain factories, so it fails at startup rather than producing
  impossible data. An integration test runs it twice.

- **S7 is the largest single screen in the project** — three sections, a matrix, and an editor —
  against a change that also adds five tables, a dependency, a config key, and a seed. → Mitigation:
  the task order puts the domain rules and the API ahead of any screen, so the reviewable core is
  demonstrable through the API before S7 exists. That is also the clean stopping line if the change
  runs long. Splitting was considered and rejected up front: the what/when seam is thin, change 4
  needs both halves, and the seed spans them.

## Migration Plan

One EF migration adds five tables. Additive — nothing existing is altered and no data is
backfilled — so the rollback is dropping them.

Two things in this migration do not come from EF conventions and must be read in the generated SQL
rather than assumed: the uniqueness that makes `Professional` genuinely 1:1 with its user (a unique
index on the user reference, partial over active rows, matching the pattern change 2 used for
`Patient`), and the composite uniqueness on both junctions so a professional cannot hold the same
specialty twice or carry two durations for one appointment type.

**The deployment change is not additive.** `Clinic__Timezone` is required, and a deployment that
restarts without it fails to start by design (E3). `.env.example` gains it in the same change, and
the README's local-run section gains a line only if it enumerates variables — the status-table edit
happens when this reaches `main`, per `00-context.md` §8.

## Open Questions

Settled during implementation; kept with their answers rather than deleted, so the reasoning
survives.

1. **Does the duration matrix need a bulk affordance?**
   **Resolved: no, not yet.** The section turned out to be a table of set durations plus a
   dialog, not a grid of inputs — so "six inputs for six types" never materialised as the shape
   the question assumed. A bulk action would now be a second way to do the same thing, and the
   validation guide is where a real need for it would surface. Revisit with feedback, not
   speculation.

2. **Is the hours editor a table or a week grid?**
   **Resolved: a table**, as planned, and the reason has hardened. A segment carries an
   effective period as well as a time, and a week grid has nowhere to put that dimension without
   either hiding it or drawing a grid per period. The trade-off stands: answering "does this
   person work Monday afternoon?" takes reading a row rather than glancing at a shape, which is
   exactly what check 6 of the validation guide asks a human to judge.

3. **Does change 4 need the effective-date dimension in its first cut?**
   **Still open, deliberately.** Stored and validated here, and the two-dimensional overlap rule
   depends on it, so the data is honest. Whether the solver filters on it initially or only
   considers currently-effective segments is change 4's call. Flagged so it is a decision rather
   than a column someone discovers and feels obliged to use.

## Discovered during implementation

Two things this change surfaced that its design did not predict, recorded because both outlived
the change.

**The domain-boundary guard had never actually worked.** `Domain.csproj`'s
`ForbidInfrastructureReferences` target put `%(ForbiddenDomainDependency.Identity)` in the
condition of an `ItemGroup` batching over `@(PackageReference)`. Two item lists in one condition
do not cross-batch: the prefix resolved to empty, `StartsWith('')` is true for every string, and
the target therefore flagged *any* package. It had never fired because `Domain` had no package
reference at all until NodaTime became the first — so change 1's guard was, for three changes, a
"no packages whatsoever" rule wearing the message of a "no infrastructure" rule. Fixed with
target batching, and verified in both directions: NodaTime builds, a deliberately added EF Core
reference fails with the offender named, and removing it builds again.

**Required configuration breaks every host that builds its own.** Adding `Clinic__Timezone` with
no default meant `HealthEndpointUnhealthyTests`, which constructs its own
`WebApplicationFactory` rather than borrowing the fixture's, stopped starting — and the symptom
looked like a health-check failure while being nothing of the kind. Both that class and
`ApiFixture` now supply the setting from one constant. Worth knowing before change 4 adds
configuration of its own.
