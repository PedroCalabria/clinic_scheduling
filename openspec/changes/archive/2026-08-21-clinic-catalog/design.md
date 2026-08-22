## Context

The catalog is the first part of this system with **referential structure**. Change 1 stored one
marker row; change 2 stored identity, where the only relationship was a 1:1 to `Patient` and every
entity stood on its own. Here four entities point at each other, and every one of them is
soft-deleted (I10) — which is what makes an otherwise dull CRUD change worth designing rather than
typing.

The reason is worth stating plainly, because it shapes almost every decision below. **Soft-delete
defeats foreign keys as a protection.** A foreign key guarantees that an `AppointmentType`'s
`specialty_id` names a row that exists. It says nothing about whether that row is still *usable* —
and under I10 the row never goes away, so the constraint is satisfied forever. The rule this change
actually needs ("you cannot retire a specialty that active appointment types still belong to") has
no database floor available to it. That is an honest asymmetry against the project's central
argument, where `EXCLUDE` gives the no-double-booking promise a real floor. Here the domain is the
only enforcement layer, and the design says so rather than implying a rigour it does not have.

Two pieces of inherited context are load-bearing:

- Change 2's `IdentityConfigurations.cs` recorded a revisit trigger — *"Revisit at change 3, when
  the clinic schema multiplies the table count"* — about whether explicit EF mapping stays
  tolerable. This change is that revisit.
- Change 2 established the partial unique index (`.IsUnique().HasFilter("deleted_at_utc IS NULL")`)
  for user email. That pattern is exactly the shape "unique among active records" needs, so this
  change inherits a solved problem rather than inventing one.

## Goals / Non-Goals

**Goals:**

- Four catalog entities in the protected core, with the reference and uniqueness rules as *domain*
  rules that unit tests can exercise without a database.
- The `AppointmentType` → (`Specialty`, `ResourceType`) shape that lets change 4 derive two of its
  three constraints from the appointment type alone.
- Three administrator screens that make a refusal comprehensible — a blocked deactivation must say
  what is blocking it, not just fail.
- Leave change 4 nothing to invent: `bufferMinutes` is stored where the solver expects it, and
  "which resources are of this type" is one indexed query.

**Non-Goals:**

- No abstraction over the four entities. See D4.
- No intervals, no `tstzrange`, no timezone. Nothing here has a wall-clock time.
- No Dapper. The catalog is small, read through EF, and not on any hot path.
- No history of catalog edits. Knowing that Cardiology's name changed in March is not a
  requirement anyone has asked for; `AccessLog` covers patient data, which the catalog is not.
- No cascade behaviour of any kind. Deactivation refuses; it never propagates.

## Decisions

### D1 — One lifecycle flag per catalog entity, not a status enum plus a delete marker

Each catalog entity carries a single nullable `DeactivatedAtUtc`. Null means active; a timestamp
means retired. Reactivation clears it. This *is* the I10 soft-delete marker, doing double duty as
the business "is this offered?" flag.

`02-domain-model.md` §2 gives `Resource` a `status`, and change 2's `User` carries both
`UserStatus` and `DeletedAtUtc`, so the precedent for two fields exists. It is rejected here
because for a user the two genuinely differ — *disabled* is a reversible administrative act,
*deleted* is gone, and `PendingClaim` is a third real state. A catalog entity has exactly one
meaningful distinction: the clinic offers this, or it used to. Two fields would produce four
states, three of which mean the same thing, and every query would have to remember both — the
first forgotten `&& status == Active` is a retired room being offered to a patient.

**Alternative considered:** `Status` enum (`Active` / `Retired`) plus `DeletedAtUtc` for "really
gone". Rejected: nothing in the MVP ever really deletes, so the second field would be permanently
null — a field that is always null is a field that is wrong.

**Trade-off:** the name `DeactivatedAtUtc` diverges from `Resource.status` in the domain doc. The
substance (one reversible retirement flag) is unchanged, and the doc's ERD is a model, not a
column list. **Revisit trigger:** a genuine third resource state — "room under maintenance this
week" is the plausible one. Note that such a state is time-bounded unavailability, which is a
`TimeBlock` against a resource rather than a status, so it would arrive with change 5 or 7 and not
by widening this enum.

### D2 — The rule is domain; the counting is infrastructure

`Domain` cannot reach the database, so it cannot answer "does an active appointment type reference
this specialty?". Splitting the responsibility keeps the rule testable anyway: the slice performs
the dependent count, and a pure domain rule decides what the count means.

```
  Slice (Api)                          Domain (protected core)
  ─────────────────────                ────────────────────────────────
  count active dependents  ─────────▶  CatalogDeactivation.Ensure(
    (EF query)                            dependents: int, kind: …)
                                              │
  translate refusal to     ◀─────────    throws DomainRuleViolation
    config.in_use                            when dependents > 0
```

The lookup is infrastructure and belongs in the slice; the *meaning* of the answer is a business
rule and belongs where the compiler protects it. Unit tests cover the rule with plain integers;
integration tests cover that the query counts the right rows — which is the half that actually
breaks, since "active" means `DeactivatedAtUtc IS NULL` on the *dependent*, and forgetting that
predicate makes the rule fire on retired records.

**Alternative considered:** put the whole check in the slice and cover it only with integration
tests. Rejected — it would make the one interesting rule in this change invisible to the unit tier
and set a precedent that erodes the protected core in exactly the way change 2's design warned
about for vertical slices.

**Alternative considered:** a domain repository interface implemented in `Api`, so the domain
"owns" the query. Rejected as ceremony: it inverts a dependency to no benefit here, and the project
rule is that every abstraction answers what problem it solves. One integer parameter solves it.

### D3 — Name uniqueness: case-insensitive, enforced by a partial unique index and checked in the slice

A partial unique index on `lower(name) WHERE deactivated_at_utc IS NULL`, per entity table. The
slice also checks before inserting, so the ordinary path returns `config.duplicate_name` rather
than surfacing a constraint violation.

Case-insensitivity is the point: "Cardiology" and "cardiology" are one specialty to every human who
will use this, and letting both exist produces two entries in the dropdown that a patient's booking
would be split across. The expression index gives it a floor at negligible cost.

This is the one rule in the change that *does* get defense in depth — a real DB floor plus a
friendly domain check — which is the same layering the booking story uses, applied where it is
actually available.

**Alternatives considered:** the `citext` extension (a second extension to install alongside
`btree_gist`, and column-level case-insensitivity is stronger than needed); a plain unique index on
`name` (case-sensitive, so it does not solve the problem); domain-only checking (loses the floor,
and two concurrent creates race).

**Trade-off:** trimming and Unicode normalization are not addressed. Names are trimmed on input;
`lower()` is Postgres's, not culture-aware. Adequate for a clinic's specialty list. **Revisit
trigger:** genuinely multilingual catalog names.

### D4 — Four explicit slices, no generic CRUD abstraction

`Features/AdminConfig/` holds separate endpoint groups for specialties, resource types, resources,
and appointment types, at `/api/config/{specialties,resource-types,resources,appointment-types}`,
all behind one `RequireAuthorization(AuthorizationPolicies.Administrator)` at the group level — the
same shape `StaffAccountEndpoints` established.

The generic option is genuinely tempting: four entities, five verbs each, near-identical handlers.
It is rejected because **the entities differ precisely where the code is interesting**. A
`Specialty` has one dependent kind; a `ResourceType` has two (`Resource`s and `AppointmentType`s);
a `Resource` has none; an `AppointmentType` has none but two *outbound* references that reactivation
must re-validate. A generic base class would express all of this as configuration or overrides and
bury the only part of this change worth reviewing. The duplication is real, mechanical, and
reviewable; the abstraction that removes it would be neither.

**Revisit trigger:** if 3b's professional-configuration slices turn out to want the same shape and
the pattern has stabilized across seven or eight entity sets, extract then, with evidence.

### D5 — Reactivation re-validates outbound references, reusing `config.not_found`

Reactivating an entity is not merely clearing a flag; it must not create an active record pointing
at an inactive one. That state is exactly what D2's in-use rule exists to prevent, and reactivation
is the back door into it:

```
   1. AppointmentType "Consulta" deactivated       → inactive
   2. Specialty "Cardiology" deactivated           → allowed! its only
                                                     appointment type is inactive
   3. AppointmentType "Consulta" reactivated       → ✗ active row now points
                                                     at an inactive specialty
```

Step 3 is refused with `config.not_found`, on the reading that from the perspective of active data
an inactive specialty *is* not found — the same code and the same reading the create path already
uses for "no active record matches". The administrator's remedy is to reactivate the specialty
first, which the message names.

**Alternatives considered:** a new `config.reference_inactive` code (more precise, but
`07-error-codes.md`'s rule is one code per user-meaningful failure, and "the thing you named isn't
available" is the same failure the create path reports); cascade-reactivating the references
(silently reactivating a specialty because someone restored an appointment type is the system making
a structural decision on the administrator's behalf — the same instinct the project rejects for
reconciliation conflicts).

### D6 — S9 carries both resource types and resources on one screen

Resource types and resources are two tables but one mental model ("what rooms and equipment does
this clinic have?"), and a resource cannot be created before its type exists. Splitting them into
two screens would make the first thing an administrator does a navigation puzzle. One screen, two
sections, types above resources — matching `06-ui-surfaces.md`, which already names S9 "Resources &
types".

`bufferMinutes` is edited on the type, where it belongs, and the screen labels it as turnaround
time rather than "buffer" — the person filling it in is thinking about cleaning a room.

### D7 — The `dialog` primitive arrives here, from the shadcn CLI

Create and edit need a modal form. `00-context.md` §2 anticipated exactly this: widgets the platform
does not provide accessibly (dialog, combobox, popover) come from the shadcn CLI with their Radix
dependencies when a screen requires one. This is the first requirement. It lands in
`packages/shared/src/ui` per the shared-primitives rule, not app-local.

**Alternative considered:** a separate route per form, avoiding the dependency. Rejected: four
entity kinds × create/edit is eight routes for what is a five-field form, and modal focus
management is precisely the accessibility problem Radix exists to solve correctly.

### D8 — EF mapping: a second configuration file, and the naming-convention package is rejected again

Change 2's recorded revisit trigger is answered: four new `IEntityTypeConfiguration` classes in
`Configurations/CatalogConfigurations.cs`, alongside the identity one. Nine tables total.

The naming-convention package is rejected a second time, with the same reasoning and now more
evidence: snake_case column names are written once per property, they show up in the migration diff
where they are reviewable, and inferring them would mean a rename could happen without appearing in
a diff at all. **Revisit trigger:** when a third configuration file appears (3b adds five more
tables) and the mapping is demonstrably mechanical, reconsider — the cost of the package falls as
the count rises, and this is a genuine judgement call rather than a settled one.

Enums-as-strings and the FK-with-no-cascade convention carry over. Every foreign key is
`OnDelete(DeleteBehavior.Restrict)`: under I10 nothing is ever deleted, so a cascade rule is either
dead configuration or, if a hard delete ever slipped in, a silent data-loss path.

## Risks / Trade-offs

- **The in-use rule has no database floor** (Context). A future slice that deactivates a catalog
  row without going through the rule creates the exact inconsistency this change forbids, and
  nothing will stop it. → Mitigation: the rule lives in `Domain` where it is discoverable, the
  refusal is covered by an integration test per reference case, and deactivation exists at exactly
  one endpoint per entity. **Revisit trigger:** a second write path to any catalog table — at which
  point the check belongs behind a narrow domain service rather than duplicated.

- **Four near-identical slices will tempt the next person to unify them** (D4). → Mitigation: the
  rationale is recorded here with the specific evidence (each entity's dependent shape differs) and
  a concrete revisit trigger, so unifying becomes a decision with a stated bar rather than a tidy-up.

- **`config.not_found` now carries two readings** — "no such row" and "the row exists but is
  inactive" (D5). A frontend cannot distinguish them. → Mitigation: for this change's screens the
  remedy is identical ("that item isn't available; reactivate it or pick another"), so the
  distinction has no user consequence. **Revisit trigger:** the first screen that must react
  differently to the two; then `config.reference_inactive` earns its place.

- **Reactivation is a real feature with a real cost.** It is not in the proposal's minimum, and it
  exists only because D1 makes deactivation the sole lifecycle operation — without reactivation, a
  mis-click permanently retires a specialty and the administrator's only recovery is a
  same-named duplicate. Accepting it adds the D3 name-collision case and the D5 reference case,
  which is two of this change's more interesting rules. → Judged worth it: both cases are cheap
  once stated, and a catalog you can only ever shrink is not credible.

- **No upper bound on `bufferMinutes`.** A fat-fingered `1500` silently removes a day of
  availability, and the symptom will surface in change 4 as "no slots" with no obvious cause. →
  Mitigation: non-negative is enforced; the field is labelled in minutes with the unit visible. A
  cap is deliberately not invented here because no stakeholder has stated one and a wrong cap
  (a genuinely long equipment turnaround) is worse than none. **Revisit trigger:** change 4's
  empty-result diagnostics — if "why are there no slots?" turns out to be hard to answer, a
  sanity bound on buffer is part of the answer.

- **Three screens plus a new UI primitive is the bulk of the work**, and the backend rules are the
  interesting part. → Mitigation: the task order puts the domain rules and their tests first, so
  the reviewable core is complete and demonstrable through the API before any screen exists. That
  is also the clean stopping line if the change runs long.

## Migration Plan

One EF migration adds four tables. It is additive — no existing table is touched, no data is
backfilled, and nothing in change 1 or 2 reads these tables — so the rollback is dropping them.

The migration must be reviewed for two things the schema does not get from EF conventions: the
partial unique indexes on `lower(name)` (D3), which EF expresses through a raw index filter and
which should be read in the generated SQL rather than assumed, and `Restrict` on every foreign key
(D8).

`.env.example` gains nothing; this change reads no new configuration.

## Open Questions

All three were settled during implementation; kept here with their answers rather than deleted,
so the reasoning survives.

1. **Does an administrator need to see *why* a deactivation was refused, specifically?**
   **Resolved: a count, carried as `params.records` on `config.in_use`.** The count travels on
   `CatalogRuleViolationException.BlockingRecords`, set by the entity that already received it,
   so the mapping in `CatalogRefusals` stays generic. `ResourceType` reports the sum of its two
   dependent kinds — the administrator wants to know how much is in the way, not which join it
   came from. A list of names was rejected: it has no length bound, and a refusal message is the
   wrong place to paginate. The parameter is deliberately named `records` and not `count`, because
   i18next treats `count` as its pluralization key and would silently demand `_one`/`_other`
   variants of the message.

2. **Should `AppointmentType` be renamed on screen?**
   **Resolved: yes, in the translation only.** pt-BR reads "Tipo de consulta", en reads
   "Appointment type", and the code keeps `AppointmentType` throughout. The screen also explains
   the concept in its subtitle rather than relying on the term, since "kind of visit" is what an
   administrator is actually choosing.

3. **Does 3b want the catalog's listing endpoints, or its own?**
   **Still open, deliberately.** Nothing in this change is shaped speculatively for it. The
   listing endpoints are administrator-only and S7 is too, so they can serve it directly if 3b
   wants them; if 3b needs a differently-shaped read (for instance, only active records, or
   specialties with their appointment types nested), that is a 3b decision. Recorded so 3b starts
   from a question rather than an assumption.
