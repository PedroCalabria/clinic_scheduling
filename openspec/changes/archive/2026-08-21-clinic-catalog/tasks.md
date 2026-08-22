## 1. Domain: the four catalog entities

- [x] 1.1 Add a `Configuration` area in `Domain` alongside `Identity`, and create `Specialty` with a private constructor, an intent-named factory, and `DeactivatedAtUtc` as its single lifecycle flag (D1)
- [x] 1.2 Add `ResourceType` carrying `BufferMinutes`, refusing a negative value at construction and on edit
- [x] 1.3 Add `Resource` holding its `ResourceTypeId`, and `AppointmentType` holding its `SpecialtyId` and required `ResourceTypeId` — and deliberately no duration (Decision C keeps duration on 3b's junction)
- [x] 1.4 Give each entity a rename operation that trims its name, and `Deactivate` / `Reactivate` operations that move the flag and are idempotent about their own current state
- [x] 1.5 Confirm `Domain` still compiles with no infrastructure reference and that `DomainBoundaryTests` passes unchanged

## 2. Domain: the reference and uniqueness rules

- [x] 2.1 Add the pure deactivation rule that turns a dependent count into a refusal (D2), raising `DomainRuleViolationException` so the slice can answer `config.in_use`
- [x] 2.2 Add the pure name-availability rule so a duplicate is refused as a domain rule and not only by the database index
- [x] 2.3 Add the rule that reactivation requires the entity's own outbound references to be active (D5), distinguishable from the in-use refusal so the slice can answer `config.not_found`
- [x] 2.4 Unit-test each rule directly — zero dependents permits, one blocks, an inactive reference blocks reactivation, a negative buffer is refused, names are compared case-insensitively after trimming

## 3. Persistence: schema and mapping

- [x] 3.1 Add `Configurations/CatalogConfigurations.cs` with the four `IEntityTypeConfiguration` classes, snake_case columns and enums-as-strings per the change-2 convention (D8)
- [x] 3.2 Map every foreign key with `OnDelete(DeleteBehavior.Restrict)` and add the indexes change 4 will read by — resource by type, appointment type by specialty and by required resource type
- [x] 3.3 Add the partial unique index on `lower(name) WHERE deactivated_at_utc IS NULL` for each of the four tables (D3)
- [x] 3.4 Register the four entity sets on `ClinicDbContext` and generate the migration
- [x] 3.5 Read the generated SQL and confirm the two things EF conventions do not give for free: the `lower(name)` expression in each partial index, and `Restrict` on every foreign key
- [x] 3.6 Confirm the migration applies to a database already holding change 1 and change 2's schema, and that dropping the four tables is a clean rollback

## 4. API: the specialty slice

- [x] 4.1 Create `Features/AdminConfig` and map `/api/config/specialties` as a group behind `AuthorizationPolicies.Administrator`, following the `StaffAccountEndpoints` shape
- [x] 4.2 Implement list (active and inactive, distinguishable), create, rename, deactivate, and reactivate
- [x] 4.3 Wire the refusals to their codes — `config.duplicate_name`, `config.in_use`, `config.not_found` — taking each from `07-error-codes.md` rather than inventing one
- [x] 4.4 Count active dependent appointment types in the slice and pass the count into the domain rule (D2), with the `DeactivatedAtUtc IS NULL` predicate on the dependent, not on the target

## 5. API: resource types and resources

- [x] 5.1 Map `/api/config/resource-types` and implement its five operations, including editing `BufferMinutes`
- [x] 5.2 Implement the resource-type deactivation check against **both** dependent kinds — active resources of that type and active appointment types requiring it — so neither reference alone is missed
- [x] 5.3 Map `/api/config/resources` and implement its five operations, refusing a resource whose named resource type is not active
- [x] 5.4 Apply the D5 reactivation check on `Resource`, refusing with `config.not_found` when its resource type has since been deactivated

## 6. API: appointment types

- [x] 6.1 Map `/api/config/appointment-types` and implement its five operations, resolving and validating both references on create and on edit
- [x] 6.2 Apply the D5 reactivation check against both the specialty and the required resource type
- [x] 6.3 Confirm an appointment type carries no duration field anywhere in its request or response shape, so 3b's junction remains the only place duration lives

## 7. Integration tests

- [x] 7.1 Cover the three in-use refusals — specialty with an active appointment type, resource type with an active resource, resource type required by an active appointment type — each asserting `409` and `config.in_use`
- [x] 7.2 Cover that a reference held only by a deactivated record does **not** block, which is the assertion that catches the wrong-side-of-the-join mistake
- [x] 7.3 Cover name uniqueness: duplicate among active refused with `config.duplicate_name`, the same name free in a different entity kind, a name freed by deactivation reusable, and case-insensitivity
- [x] 7.4 Cover reactivation: the plain case, the name-taken-since case, and the broken-reference case from D5
- [x] 7.5 Cover the authorization boundary on the `AsRoleAsync` seam — front desk refused `403 auth.forbidden` on both a write and a read, no session refused `401 auth.session_expired`, administrator succeeds
- [x] 7.6 Cover `config.not_found` on acting against an entity that does not exist, and the negative-buffer refusal
- [x] 7.7 Confirm no catalog row is ever physically removed by any endpoint in this change (I10)
- [x] 7.8 Add a database-level test proving the partial unique index refuses a duplicate active name even when the slice check is bypassed — the floor beneath D3
- [x] 7.9 Extend `compose-smoke` with the catalog through Caddy: create all four entity kinds, assert the `config.in_use` refusal and its count, and assert the three screens resolve as staff deep links

## 8. Frontend: shared groundwork

- [x] 8.1 Add the `dialog` primitive to `packages/shared/src/ui` from the shadcn CLI with its Radix dependency (D7), exported from the package like the existing primitives
- [x] 8.2 Add the catalog API client functions and TanStack Query hooks in `packages/shared`, with mutations invalidating their list query so a change reflects without a manual reload
- [x] 8.3 Add pt-BR and en keys for `config.in_use`, `config.duplicate_name`, and `config.not_found` so a refusal is translated prose rather than a code
- [x] 8.4 Confirm the new primitive's classes survive a production build, per the `@source` rule the compose-smoke tier already guards
- [x] 8.5 Extend `check-i18n-keys` with a usage scan, so a component asking for a key that exists in neither language fails CI instead of rendering the raw key — and verify the new check fails on a deliberately broken key

## 9. Frontend: the three screens

- [x] 9.1 Build S8 — specialties: table showing active and inactive distinguishably, create/rename in the dialog, deactivate and reactivate, refusals shown as translated explanations
- [x] 9.2 Build S9 — resources and types on one screen (D6): the type section carrying turnaround minutes labelled as turnaround rather than "buffer", the resource section below it
- [x] 9.3 Build S10 — appointment types, with specialty and required-resource-type choosers offering **only active** records
- [x] 9.4 Add the three navigation entries to the staff app-shell under the administrator condition, and confirm a front-desk user sees none of them while the API still refuses the endpoints directly
- [x] 9.5 Verify all three screens in pt-BR and en with no missing-key fallback
- [ ] 9.6 Verify the keyboard and contrast baseline on the dialog — focus trapped, Escape closes, focus returns to the trigger

## 10. Documentation

- [x] 10.1 Resolve the design's open questions in the artifacts once implementation settles them — the `config.in_use` params shape, the appointment-type wording, and whether 3b reuses these listing endpoints
- [x] 10.2 Update `docs/00-context.md` only if a convention actually changed during implementation, so the substrate doc stays the source of truth
- [x] 10.3 Update the README status table for increment 3a — a status cell and the shipped-count line, made when the change reaches `main`. No local-run change: this change adds no prerequisite and no environment variable

## 11. Definition of Done

- [x] 11.1 An administrator creates specialties, resource types with a turnaround buffer, resources of a type, and appointment types linking a specialty to a required resource type, through S8–S10
- [x] 11.2 Deactivating a referenced specialty or resource type is refused with `config.in_use`, and the screen explains what blocked it (tested)
- [x] 11.3 A duplicate active name is refused with `config.duplicate_name`, and a name freed by deactivation can be reused (tested)
- [x] 11.4 Reactivation works, and is refused when the name was taken or an outbound reference has gone inactive (tested)
- [x] 11.5 A front-desk user is refused `403 auth.forbidden` on catalog endpoints, distinct from the `401` an anonymous caller gets (tested)
- [ ] 11.6 The three screens are functional through Caddy, each rendering in pt-BR and en with no missing-key fallback
- [x] 11.7 `GET /api/health` and both change-2 sign-in paths still work unchanged through the proxy
- [x] 11.8 Unit and integration tests green in CI against real PostgreSQL
- [x] 11.9 `openspec validate clinic-catalog --strict` passes
