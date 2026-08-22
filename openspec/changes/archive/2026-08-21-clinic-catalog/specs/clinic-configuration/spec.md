## ADDED Requirements

### Requirement: The catalog defines what the clinic offers

The system SHALL hold, as administrator-managed reference data, the specialties the clinic practises, the types of room and equipment it has, the concrete resources of each type, and the kinds of appointment it offers. An appointment type SHALL belong to exactly one specialty and SHALL require exactly one resource type, so that the specialty and resource constraints of a future booking are both derivable from the appointment type alone. An appointment type SHALL NOT carry a duration, because duration varies by professional.

#### Scenario: Specialty created

- **WHEN** an administrator creates a specialty with a name no active specialty holds
- **THEN** the specialty exists in an active state and is available for appointment types to belong to

#### Scenario: Resource type carries its turnaround buffer

- **WHEN** an administrator creates a resource type with a turnaround buffer in minutes
- **THEN** the resource type is stored with that buffer, which is the value availability computation will later treat as occupied after an appointment ends

#### Scenario: Resource belongs to a resource type

- **WHEN** an administrator creates a resource naming an existing active resource type
- **THEN** the resource exists in an active state, typed by that resource type

#### Scenario: Appointment type ties a specialty to a required resource type

- **WHEN** an administrator creates an appointment type naming an existing active specialty and an existing active resource type
- **THEN** the appointment type exists in an active state holding both references, and carries no duration of its own

#### Scenario: Appointment type referencing something that does not exist

- **WHEN** an administrator creates or edits an appointment type naming a specialty or resource type that no active record matches
- **THEN** the API responds `404` with code `config.not_found` and nothing is created or modified

#### Scenario: Buffer must be a non-negative duration

- **WHEN** an administrator supplies a negative turnaround buffer
- **THEN** the API refuses the request through the validation contract and no resource type is created or modified

### Requirement: Deactivation is refused while active records still reference the entity

Catalog entities SHALL be deactivated rather than deleted, consistent with the system-wide soft-delete rule. Deactivating an entity that an active record still references SHALL be refused with code `config.in_use`, so the catalog cannot be left holding an appointment type whose specialty or required resource type has disappeared. A reference held only by an already-deactivated record SHALL NOT block deactivation.

#### Scenario: Specialty still used by an active appointment type

- **WHEN** an administrator deactivates a specialty that an active appointment type belongs to
- **THEN** the API responds `409` with code `config.in_use` and the specialty remains active

#### Scenario: Resource type still holding active resources

- **WHEN** an administrator deactivates a resource type that at least one active resource is typed by
- **THEN** the API responds `409` with code `config.in_use` and the resource type remains active

#### Scenario: Resource type still required by an active appointment type

- **WHEN** an administrator deactivates a resource type that an active appointment type requires, even though no active resource is typed by it
- **THEN** the API responds `409` with code `config.in_use` and the resource type remains active

#### Scenario: Deactivated references do not block

- **WHEN** an administrator deactivates a specialty whose only appointment types are themselves already deactivated
- **THEN** the deactivation succeeds

#### Scenario: Entity with no dependents

- **WHEN** an administrator deactivates a resource, or an appointment type, that no active record references
- **THEN** the entity becomes inactive and is excluded from the lists offered when creating other catalog entities

#### Scenario: Deactivation is never a hard delete

- **WHEN** any catalog entity is deactivated
- **THEN** its record is retained and marked inactive rather than removed, so a later reference to it remains resolvable

#### Scenario: Acting on an entity that does not exist

- **WHEN** an administrator edits or deactivates a catalog entity no record matches
- **THEN** the API responds `404` with code `config.not_found`

### Requirement: Names are unique among active records only

A catalog entity's name SHALL be unique within its own kind among active records, so that two active specialties cannot share a name while a name freed by deactivation becomes available again. A deactivated entity SHALL be reactivatable, and reactivation SHALL be refused when an active entity of the same kind has since taken its name.

#### Scenario: Duplicate name among active records

- **WHEN** an administrator creates or renames a catalog entity to a name an active entity of the same kind already holds
- **THEN** the API responds `409` with code `config.duplicate_name` and nothing is created or modified

#### Scenario: The same name is free in a different kind

- **WHEN** an administrator creates a resource type with a name an active specialty holds
- **THEN** the request succeeds, because uniqueness is scoped to one kind of entity

#### Scenario: A deactivated entity frees its name

- **WHEN** an administrator deactivates a specialty and then creates a new specialty with the same name
- **THEN** the request succeeds and both records exist, one active and one inactive

#### Scenario: Reactivation

- **WHEN** an administrator reactivates a deactivated catalog entity whose name no active entity of that kind holds
- **THEN** the entity becomes active again and is offered once more when creating other catalog entities

#### Scenario: Reactivation blocked by a name taken since

- **WHEN** an administrator reactivates a deactivated entity whose name an active entity of the same kind has since taken
- **THEN** the API responds `409` with code `config.duplicate_name` and the entity remains inactive

#### Scenario: Reactivation cannot resurrect a broken reference

- **WHEN** an administrator reactivates a deactivated appointment type or resource whose specialty or resource type has since been deactivated
- **THEN** the API responds `404` with code `config.not_found` and the entity remains inactive, so that reactivation cannot produce an active record pointing at an inactive one — the state the in-use rule exists to prevent

### Requirement: Only administrators may shape the catalog

Every catalog endpoint SHALL require the administrator role. A front-desk user runs the clinic's day but SHALL NOT alter its structure, and the refusal SHALL be the role refusal rather than the unauthenticated one. Hiding a navigation entry SHALL NOT be the mechanism that enforces this.

#### Scenario: Front-desk user attempts a catalog change

- **WHEN** an authenticated front-desk user creates, edits, or deactivates any catalog entity
- **THEN** the API responds `403` with code `auth.forbidden` and nothing is modified

#### Scenario: Front-desk user attempts to read the catalog

- **WHEN** an authenticated front-desk user requests a catalog listing directly
- **THEN** the API refuses with `403` and code `auth.forbidden`, regardless of what the staff navigation shows them

#### Scenario: Unauthenticated request is distinguishable

- **WHEN** the same catalog request is made without a session
- **THEN** the API responds `401` with code `auth.session_expired`, distinct from the role refusal

#### Scenario: Administrator succeeds

- **WHEN** an authenticated administrator performs the same catalog request
- **THEN** the request succeeds

### Requirement: The staff surface presents the catalog to administrators

The staff application SHALL offer administrators screens to manage specialties, resource types and resources, and appointment types, mounted inside the existing staff app-shell. Each screen SHALL list both active and inactive records distinguishably, SHALL surface a refusal by its error code as a translated explanation rather than a raw response, and SHALL offer only active entities as the target of a new reference. All user-facing text introduced SHALL resolve through the i18n layer in both product languages.

#### Scenario: Administrator manages the catalog end to end

- **WHEN** an administrator opens the specialties, resources, and appointment-type screens
- **THEN** they can create, edit, and deactivate records of each kind, and the result of each action is reflected in the list without a manual reload

#### Scenario: A refusal is explained, not dumped

- **WHEN** a deactivation is refused with `config.in_use`, or a name is refused with `config.duplicate_name`
- **THEN** the screen shows the translated explanation for that code, naming what blocked the action, and the record is left as it was

#### Scenario: Only active entities are offered as references

- **WHEN** an administrator creates an appointment type after some specialties have been deactivated
- **THEN** the specialty and resource-type choices offered contain only active records

#### Scenario: Inactive records remain visible

- **WHEN** an administrator views a catalog list containing deactivated records
- **THEN** those records are shown as inactive rather than hidden, and can be reactivated from the same screen

#### Scenario: Navigation reflects the administrator role

- **WHEN** a front-desk user signs in to the staff surface
- **THEN** the catalog navigation entries are absent, and requesting those screens' endpoints directly is still refused by the API

#### Scenario: Both languages

- **WHEN** the active language is switched between pt-BR and en on any catalog screen
- **THEN** every rendered string changes to its translation with no missing-key fallback displayed
