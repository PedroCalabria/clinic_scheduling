## 1. Timezone: the configuration that everything wall-clock depends on

- [x] 1.1 Add NodaTime to `Domain` and confirm the `ForbidInfrastructureReferences` target and `DomainBoundaryTests` still pass — NodaTime is a domain-modelling library, not infrastructure, and that judgement should be made explicitly rather than discovered by a failing build
- [x] 1.2 Add `Clinic__Timezone` options bound at startup, validated against the zone database, failing startup with a message naming the setting when missing or unrecognized (E3)
- [x] 1.3 Add `Clinic__Timezone` to `.env.example` and to `infra/docker-compose.yml`, and confirm the API container still starts
- [x] 1.4 Integration-test both halves: a recognized zone starts, and an unrecognized one fails to start rather than falling back

## 2. Domain: the professional and the two junctions

- [x] 2.1 Add `Professional` in the `Configuration` area, keyed to its `UserId`, with the `DeactivatedAtUtc` lifecycle flag 3a established (D1)
- [x] 2.2 Add `ProfessionalSpecialty` as an explicit junction entity rather than a collection, so the qualification set can be queried directly by change 5's I2 check (E2)
- [x] 2.3 Add `ProfessionalAppointmentType` carrying `DurationMinutes`, refusing zero or negative at construction and on edit
- [x] 2.4 Add the qualification-gate rule: a duration may exist only for an appointment type whose specialty the professional holds, raising the refusal the slice maps to `config.specialty_not_held` (E2)
- [x] 2.5 Add the rule that removing a held specialty is refused while active durations depend on it, carrying the count so the refusal can name it — the same shape 3a's `BlockingRecords` established
- [x] 2.6 Unit-test the gate directly: held specialty permits, unheld refuses, removing a depended-on specialty refuses with the count, zero and negative durations refuse

## 3. Domain: working hours

- [x] 3.1 Add `WorkingHoursTemplate` holding an `IsoDayOfWeek`, two `LocalTime` values, and its effective range — wall-clock throughout, with no instant anywhere in the type (E3)
- [x] 3.2 Add the validity rule refusing `startTime >= endTime`, which covers both the zero-length and the midnight-crossing case in one predicate (E5)
- [x] 3.3 Add the overlap rule as the **two-dimensional** check: a conflict requires both the effective ranges and the times of day to overlap (E5)
- [x] 3.4 Add `WorkingHoursException` for one professional on one date, either unavailable-all-day or carrying replacement hours governed by the same validity rule
- [x] 3.5 Unit-test the overlap rule with the three cases from the design's table — and note that two of them must be **allowed**, since a rule with only refusal tests is indistinguishable from one that always refuses
- [x] 3.6 Unit-test the validity rule: midnight-crossing refused, zero-length refused, an ordinary morning segment accepted

## 4. Persistence: schema and mapping

- [x] 4.1 Add `Configurations/ProfessionalConfigurations.cs` with the five `IEntityTypeConfiguration` classes, snake_case and enums-as-strings per the established convention
- [x] 4.2 Map `LocalTime` and the day-of-week to Postgres `time`/text via explicit value converters, so no `timestamptz` appears anywhere in these tables — the schema itself should make it impossible to store an instant here
- [x] 4.3 Add the unique index making `Professional` genuinely 1:1 with its user, partial over active rows, matching change 2's `Patient` pattern
- [x] 4.4 Add composite unique indexes on both junctions so a specialty cannot be held twice and one appointment type cannot carry two durations for the same professional
- [x] 4.5 Map every foreign key `Restrict`, and add the indexes change 4 will read by — segments and exceptions by professional, durations by professional and by appointment type
- [x] 4.6 Register the five entity sets, generate the migration, and read the generated SQL to confirm the two things conventions do not give: the partial unique index and the absence of any timestamp-with-timezone column
- [x] 4.7 Confirm the migration applies on top of change 1, 2, and 3a's schema, and that dropping the five tables is a clean rollback
- [x] 4.8 Decide and record whether NodaTime's Npgsql plugin is needed, or whether plain converters suffice — a dependency that is not required should not arrive by reflex

## 5. API: the professional and their qualifications

- [x] 5.1 Add the professionals group under `/api/config/professionals` in the existing `AdminConfig` slice, behind the administrator policy at the group level
- [x] 5.2 Implement the list as the left join in E1: every user with the professional role, each carrying whether it has been configured and whether its invitation is still unclaimed
- [x] 5.3 Implement create-on-first-save: any configuring write for a professional with no record creates it, and a second write reuses it (E1)
- [x] 5.4 Refuse configuring a user that does not exist or does not hold the professional role, with `config.not_found`
- [x] 5.5 Implement assigning and removing specialties, refusing an inactive specialty with `config.not_found` and a depended-on removal with `config.in_use` plus its count
- [x] 5.6 Implement setting and clearing per-type durations, mapping the gate refusal to `config.specialty_not_held` with `422`

## 6. API: working hours and exceptions

- [x] 6.1 Implement the working-hour segment endpoints, mapping the two refusals to `config.working_hours_overlap` (409) and `config.working_hours_invalid` (422)
- [x] 6.2 Count the overlap against **active** segments only, so a retired segment never blocks a new one — the same predicate-on-the-dependent mistake 3a guarded against
- [x] 6.3 Implement the exception endpoints, including the one-per-professional-per-date rule and the shared validity rule
- [x] 6.4 Implement retiring a segment or an exception as deactivation rather than deletion (I10), and confirm a retired one stops counting as an overlap

## 7. Integration tests

- [x] 7.1 Cover create-on-first-save: a professional with no record gains one, a second write reuses it, and no duplicate user or role change occurs
- [x] 7.2 Cover that an unclaimed invitation can still be configured, which is the requirement option (ii) of the fork would have failed
- [x] 7.3 Cover the gate: a duration inside a held specialty succeeds, outside it is `422 config.specialty_not_held`, and removing a depended-on specialty is `409 config.in_use` with its count
- [x] 7.4 Cover the overlap rule end to end, including both cases that must be **allowed** (disjoint times, disjoint effective ranges)
- [x] 7.5 Cover the validity rule: midnight-crossing and zero-length refused, for both a segment and an exception
- [x] 7.6 Cover that stored hours read back as the wall-clock values entered, and assert no column in these tables is timestamp-with-timezone
- [x] 7.7 Cover the authorization boundary on the `AsRoleAsync` seam: front desk refused `403`, a professional refused `403` on their own configuration, anonymous `401`, administrator succeeds
- [x] 7.8 Cover that nothing in this change is ever physically deleted (I10)

## 8. The dev seed

- [x] 8.1 Add the seed as a hosted service in `AdministratorBootstrap`'s shape, constructing every entity through the domain factories so it cannot produce data the API would refuse (E6)
- [x] 8.2 Gate it on the development environment and on explicit configuration, and add that configuration to `.env.example`
- [x] 8.3 Seed 3a's catalog plus at least one professional holding specialties, per-type durations, and working hours — a clinic change 4 can immediately compute against
- [x] 8.4 Integration-test that running it twice creates no duplicates and preserves an edit made between runs
- [x] 8.5 Integration-test that it creates nothing outside development, whatever its configuration says

## 9. Frontend: S7

- [x] 9.1 Add the catalog and professional API client functions and query hooks in `packages/shared`, reusing 3a's catalog endpoints for the specialty and appointment-type lists (E8)
- [x] 9.2 Add pt-BR and en keys for the three new codes so each refusal is translated prose, not a code
- [x] 9.3 Build S7's master-detail frame: the professional list, showing configured and unconfigured distinguishably, and the detail shell (E7)
- [x] 9.4 Build the specialties section — assign and remove, with the depended-on refusal explained
- [x] 9.5 Build the duration matrix, whose rows are only the appointment types the held specialties permit, and which explains why it is empty rather than merely being empty (E7)
- [x] 9.6 Build the hours editor as a table, with the overlap and validity refusals shown beside the segment that caused them
- [x] 9.7 Build the exceptions section — a date plus either unavailable or replacement hours
- [x] 9.8 Add the navigation entry under the administrator condition, and confirm a front-desk user sees nothing while the API still refuses the endpoints directly
- [x] 9.9 Confirm the i18n usage scan passes, so no screen ships a key that exists in neither language

## 10. Documentation

- [x] 10.1 Resolve the design's open questions in the artifacts once implementation settles them — the matrix bulk affordance, table versus week grid, and whether change 4 needs the effective-date dimension
- [x] 10.2 Update `docs/00-context.md` only if a convention actually changed, so the substrate doc stays the source of truth
- [x] 10.3 Update the README status table for increment 3b, and add `Clinic__Timezone` to the local-run section since this change adds a required environment variable — made when the change reaches `main` (§8)

## 11. Validation guide (§9)

- [x] 11.1 Write `validation.md`: the numbered manual checks a human runs against the local app, each naming the role, the route, the action, the expected result, and both locales where user-facing
- [x] 11.2 Keep it to what tests cannot assert — the matrix's feel, the hours editor's legibility, refusal messages rendering in place, both locales. Anything automatable belongs in section 7 instead
- [x] 11.3 Run the guide against the Compose stack and record the outcome; the change is not done until it has been executed

## 12. Definition of Done

- [x] 12.1 An administrator opens S7, selects an invited professional, assigns specialties, sets per-type durations, and defines working hours and an exception — the record created on first save
- [x] 12.2 The gate is enforced: a duration outside held specialties is refused `config.specialty_not_held` (tested)
- [x] 12.3 All three working-hours refusals are enforced, and both legitimate non-overlap cases are accepted (tested)
- [x] 12.4 `Clinic__Timezone` is required, validated at startup, present in `.env.example`, and hours are stored as wall-clock with no instant in the schema (tested)
- [x] 12.5 The dev seed produces a complete runnable clinic, is idempotent across restarts, and never runs outside development (tested)
- [x] 12.6 Front desk and a professional are both refused these endpoints, distinctly from anonymous (tested)
- [x] 12.7 Unit and integration tests green in CI against real PostgreSQL
- [x] 12.8 The validation guide has been executed against the local app, in both locales
- [x] 12.9 `openspec validate professional-configuration --strict` passes
