# clinic-configuration Specification

## Purpose

The administrator-owned reference data the scheduler reads. This capability answers two
questions the booking path depends on, and it is being built in two halves.

The **catalog** half — established here — answers *what the clinic offers*: the specialties it
practises, the types of room and equipment it has, the concrete resources of each type, and the
kinds of appointment it offers. An appointment type belongs to one specialty and requires one
resource type, so two of availability's three constraints are derivable from the appointment
type alone. A resource type carries the turnaround buffer that availability computation keeps
out of the bookable window. An appointment type deliberately carries no duration, because
duration varies per professional.

Catalog entities are retired rather than deleted, and retirement is refused while active
records still reference the entity. That rule has no database floor available to it: soft-delete
keeps the referenced row present, so a foreign key stays satisfied forever and the domain is the
only enforcement layer. Name uniqueness, by contrast, does get a floor — a partial unique index
over the lower-cased name among active rows.

The **professional** half answers *who can deliver it, and when*: a configuration record created
separately from the user account that identifies someone, the specialties they hold, the duration
each takes them, and their recurring hours plus one-off exceptions. The specialties a professional
holds are the qualification gate over their durations, which is what makes invariant I2 checkable
at booking time.

Working hours are stored as the wall-clock times entered, never as instants. A recurring rule
("every Monday 09:00") is not an event: it generates events only once a date is supplied, and under
daylight saving the same rule yields different offsets across the year. Interpreting hours against
a date therefore belongs to whoever supplies the date, and the clinic's single configured timezone
is what they interpret against.

Established by change `clinic-catalog` (change 3a of the build order) and completed by
`professional-configuration` (change 3b), following `identity-session` from change 2, whose
administrator role policy every endpoint here carries.

## Requirements

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

### Requirement: A professional's clinical configuration exists separately from their identity

The system SHALL hold a professional's clinical configuration on a record separate from the user account that identifies them, created when an administrator first configures them rather than when they are invited. An administrator SHALL be able to see every invited professional, whether or not they have been configured and whether or not they have yet signed in. Configuring a professional SHALL NOT alter their identity, their role, or their sign-in path.

The configuration record SHALL carry the professional's **name**, set by an administrator and changeable by one. The name SHALL be optional, because the record itself is created only on first configuration and an invited professional may have neither. Wherever the system presents a professional to a person, it SHALL show the stored name when there is one; where there is none it SHALL show a label derived from the account address rather than the address itself, and SHALL NOT present a staff email address to a patient.

#### Scenario: Every invited professional is listed

- **WHEN** an administrator opens the professionals screen after professionals have been invited
- **THEN** every user holding the professional role is listed, including those who have never signed in and those with no configuration yet, each distinguishable by whether it has been configured

#### Scenario: The configuration record is created on first save

- **WHEN** an administrator saves configuration for an invited professional who has none
- **THEN** the professional's configuration record is created and holds what was saved
- **AND** no second user, role change, or credential is created

#### Scenario: Setting a name creates the record for a professional who has none

- **WHEN** an administrator sets the name of an invited professional who has no configuration record
- **THEN** the record is created holding that name, as any other first save would create it

#### Scenario: The stored name is what the system shows

- **WHEN** a professional's name has been set and a surface presents that professional
- **THEN** the stored name is shown rather than a label derived from their account address

#### Scenario: A professional with no stored name is still presented readably

- **WHEN** a surface presents a professional whose name has not been set
- **THEN** a label derived from their account address is shown, and their email address is not

#### Scenario: Changing the name changes what every surface shows

- **WHEN** an administrator changes a professional's name
- **THEN** subsequent presentations of that professional use the new name, and no appointment already booked is altered

#### Scenario: Configuring again reuses the same record

- **WHEN** an administrator saves configuration for a professional who already has a record
- **THEN** the existing record is updated and no duplicate is created

#### Scenario: A professional who has not signed in can still be configured

- **WHEN** an administrator configures a professional whose invitation has not yet been claimed
- **THEN** the configuration succeeds, so that a professional's schedule can be prepared before their first sign-in

#### Scenario: Configuration is refused for a user who is not a professional

- **WHEN** an administrator attempts to configure a user whose role is not professional, or a user that does not exist
- **THEN** the API responds `404` with code `config.not_found` and no record is created

### Requirement: Specialties a professional holds gate the durations they can be given

A professional SHALL hold zero or more specialties, and that set SHALL be the authority on what they are qualified to do. A per-type duration SHALL be assignable only for an appointment type whose specialty the professional holds; any other assignment SHALL be refused with code `config.specialty_not_held`. Removing a specialty SHALL NOT silently discard the durations that depended on it.

#### Scenario: Specialty assigned

- **WHEN** an administrator assigns an active specialty to a professional
- **THEN** the professional holds that specialty, and appointment types belonging to it become assignable

#### Scenario: Duration assigned within a held specialty

- **WHEN** an administrator sets a duration for an appointment type whose specialty the professional holds
- **THEN** the duration is stored against that professional and that appointment type

#### Scenario: Duration refused outside held specialties

- **WHEN** an administrator sets a duration for an appointment type whose specialty the professional does not hold
- **THEN** the API responds `422` with code `config.specialty_not_held` and no duration is stored

#### Scenario: Removing a specialty that durations depend on

- **WHEN** an administrator removes a specialty from a professional while durations exist for appointment types belonging to it
- **THEN** the request is refused with code `config.in_use`, naming how many active durations depend on it, and the specialty remains held

#### Scenario: Only active catalog entries are assignable

- **WHEN** an administrator assigns a specialty or sets a duration naming a specialty or appointment type that is not active
- **THEN** the API responds `404` with code `config.not_found` and nothing is stored

#### Scenario: Duration must be a usable length

- **WHEN** an administrator sets a duration of zero or less
- **THEN** the request is refused through the validation contract and no duration is stored

#### Scenario: A duration is per professional and per type

- **WHEN** two professionals are given different durations for the same appointment type
- **THEN** both are stored independently, and neither overwrites the other nor becomes a clinic-wide default

### Requirement: Working hours are recurring wall-clock segments per professional

A professional's recurring availability SHALL be expressed as segments naming a day of the week, a start time, an end time, and the period over which the segment applies. Times SHALL be stored as the wall-clock times an administrator entered, in the clinic's configured timezone, without conversion to instants. A segment that is ambiguous or impossible SHALL be refused rather than interpreted.

#### Scenario: Working-hour segment defined

- **WHEN** an administrator defines a segment for a professional on a weekday with a start earlier than its end
- **THEN** the segment is stored as entered, and reads back with the same wall-clock times

#### Scenario: Overlapping segments for the same weekday

- **WHEN** an administrator defines a segment for a weekday whose applicable period overlaps an existing active segment for that professional on the same weekday
- **THEN** the API responds `409` with code `config.working_hours_overlap` and the segment is not stored

#### Scenario: Non-overlapping segments on the same weekday are allowed

- **WHEN** an administrator defines two segments for the same weekday whose times do not overlap, such as a morning and an afternoon block
- **THEN** both are stored, because a split working day is ordinary rather than ambiguous

#### Scenario: Segment crossing midnight

- **WHEN** an administrator defines a segment whose end time is earlier than its start time, such as 22:00 to 02:00
- **THEN** the API responds `422` with code `config.working_hours_invalid` and the segment is not stored

#### Scenario: Zero-length segment

- **WHEN** an administrator defines a segment whose start and end times are equal
- **THEN** the API responds `422` with code `config.working_hours_invalid` and the segment is not stored

#### Scenario: A segment can be retired without losing its history

- **WHEN** an administrator removes a working-hour segment
- **THEN** the record is retained and marked inactive rather than deleted, and it no longer counts as an overlap against a new segment

### Requirement: A professional's exceptions override their recurring hours

An administrator SHALL be able to record a one-off override for a single professional on a specific date, expressing either that they are unavailable that day or that they work different hours from their recurring pattern. An exception SHALL apply to exactly one professional. The same validity rules that govern a recurring segment SHALL govern an exception's hours.

#### Scenario: A day off

- **WHEN** an administrator records an exception marking a professional unavailable on a date
- **THEN** the exception is stored for that professional and that date, carrying no hours

#### Scenario: Different hours on one date

- **WHEN** an administrator records an exception giving a professional different hours on a date
- **THEN** the exception is stored with those wall-clock hours, and the recurring segments for that weekday are not modified

#### Scenario: An exception's hours obey the same rules

- **WHEN** an administrator records an exception whose hours cross midnight or whose start is not before its end
- **THEN** the API responds `422` with code `config.working_hours_invalid` and the exception is not stored

#### Scenario: One exception per professional per date

- **WHEN** an administrator records a second exception for a professional on a date that already has an active one
- **THEN** the API responds `409` with code `config.working_hours_overlap` and the second is not stored, so a date never has two conflicting answers

#### Scenario: An exception belongs to one professional only

- **WHEN** an exception is recorded for one professional
- **THEN** no other professional's availability is affected by it

### Requirement: The clinic's timezone is configured, and working hours are stored unconverted

The deployment SHALL carry a single configured clinic timezone, named as a recognized zone identifier, and the application SHALL refuse to start rather than assume one if the configured value is missing or unrecognized. Working hours SHALL be stored as the wall-clock times entered, so that interpreting them against a calendar date remains a separate concern from recording them.

#### Scenario: Configured timezone

- **WHEN** the application starts with a recognized clinic timezone configured
- **THEN** startup succeeds and the configured zone is the one the system reports as the clinic's

#### Scenario: Missing or unrecognized timezone

- **WHEN** the application starts with no clinic timezone configured, or with a value no zone database recognizes
- **THEN** startup fails with a message naming the setting, rather than falling back to a default or to the host's local zone

#### Scenario: Hours are stored as entered

- **WHEN** a working-hour segment is stored and read back
- **THEN** the times are the wall-clock times the administrator entered, unshifted, regardless of the timezone the server or the caller runs in

### Requirement: A development deployment can seed a complete, runnable clinic

A development deployment SHALL be able to provision a clinic that is complete enough to demonstrate scheduling — a catalog plus at least one professional holding specialties, per-type durations, and working hours. Applying the seed repeatedly SHALL NOT duplicate or overwrite what already exists. The seed SHALL NOT run outside development.

#### Scenario: First start seeds a runnable clinic

- **WHEN** a development deployment starts against a database holding no catalog and seeding is enabled
- **THEN** specialties, resource types with turnaround, resources, and appointment types exist, and at least one professional holds specialties, per-type durations, and working hours

#### Scenario: Restart does not duplicate

- **WHEN** the same development deployment starts again with the seed enabled
- **THEN** no duplicate records are created and any edits an operator has since made are left as they are

#### Scenario: The seed does not run in production

- **WHEN** the application starts in a non-development environment
- **THEN** no seed data is created, whatever the seed configuration says

#### Scenario: Seeded data is ordinary data

- **WHEN** an administrator opens the configuration screens against a seeded deployment
- **THEN** the seeded records appear and behave exactly as hand-entered ones, including their deactivation and gate rules

### Requirement: Only administrators may shape the catalog

Every endpoint in this capability — the catalog and the professionals' clinical configuration alike — SHALL require the administrator role. A front-desk user runs the clinic's day but SHALL NOT alter its structure or decide what a professional is qualified to do, and the refusal SHALL be the role refusal rather than the unauthenticated one. Hiding a navigation entry SHALL NOT be the mechanism that enforces this.

#### Scenario: Front-desk user attempts a catalog change

- **WHEN** an authenticated front-desk user creates, edits, or deactivates any catalog entity
- **THEN** the API responds `403` with code `auth.forbidden` and nothing is modified

#### Scenario: Front-desk user attempts to read the catalog

- **WHEN** an authenticated front-desk user requests a catalog listing directly
- **THEN** the API refuses with `403` and code `auth.forbidden`, regardless of what the staff navigation shows them

#### Scenario: Front-desk user attempts to configure a professional

- **WHEN** an authenticated front-desk user assigns a specialty, sets a per-type duration, or changes working hours
- **THEN** the API responds `403` with code `auth.forbidden` and nothing is modified

#### Scenario: A professional cannot configure themselves

- **WHEN** an authenticated professional attempts to change their own specialties, durations, or working hours
- **THEN** the API responds `403` with code `auth.forbidden`, because what a professional is qualified for is an administrative decision rather than a self-service one

#### Scenario: Unauthenticated request is distinguishable

- **WHEN** the same catalog request is made without a session
- **THEN** the API responds `401` with code `auth.session_expired`, distinct from the role refusal

#### Scenario: Administrator succeeds

- **WHEN** an authenticated administrator performs the same catalog request
- **THEN** the request succeeds

### Requirement: The staff surface presents the catalog to administrators

The staff application SHALL offer administrators screens to manage specialties, resource types and resources, appointment types, and each professional's clinical configuration, mounted inside the existing staff app-shell. Each screen SHALL list both active and inactive records distinguishably, SHALL surface a refusal by its error code as a translated explanation rather than a raw response, and SHALL offer only active entities as the target of a new reference. All user-facing text introduced SHALL resolve through the i18n layer in both product languages.

#### Scenario: Administrator manages the catalog end to end

- **WHEN** an administrator opens the specialties, resources, and appointment-type screens
- **THEN** they can create, edit, and deactivate records of each kind, and the result of each action is reflected in the list without a manual reload

#### Scenario: Administrator configures a professional end to end

- **WHEN** an administrator opens the professionals screen, selects an invited professional, assigns specialties, sets per-type durations, and defines working hours
- **THEN** each step is saved and reflected without a manual reload, and the professional reads back as configured

#### Scenario: The duration matrix offers only what the professional is qualified for

- **WHEN** an administrator sets durations for a professional
- **THEN** the appointment types offered are those belonging to the specialties that professional holds, so the gate is visible in the screen rather than only discovered as a refusal

#### Scenario: A refusal is explained, not dumped

- **WHEN** a deactivation is refused with `config.in_use`, or a name is refused with `config.duplicate_name`
- **THEN** the screen shows the translated explanation for that code, naming what blocked the action, and the record is left as it was

#### Scenario: A working-hours refusal names what is wrong

- **WHEN** a working-hour segment is refused with `config.working_hours_overlap` or `config.working_hours_invalid`
- **THEN** the editor shows the translated explanation for that code beside the segment that caused it, and the previously stored hours are unchanged

#### Scenario: Only active entities are offered as references

- **WHEN** an administrator creates an appointment type after some specialties have been deactivated
- **THEN** the specialty and resource-type choices offered contain only active records

#### Scenario: Inactive records remain visible

- **WHEN** an administrator views a catalog list containing deactivated records
- **THEN** those records are shown as inactive rather than hidden, and can be reactivated from the same screen

#### Scenario: Navigation reflects the administrator role

- **WHEN** a front-desk user signs in to the staff surface
- **THEN** the catalog and professional-configuration navigation entries are absent, and requesting those screens' endpoints directly is still refused by the API

#### Scenario: Both languages

- **WHEN** the active language is switched between pt-BR and en on any catalog or professional-configuration screen
- **THEN** every rendered string changes to its translation with no missing-key fallback displayed

### Requirement: The professionals screen edits a professional's name

The staff application's professional-configuration screen SHALL offer an administrator a field for the professional's name, showing what is stored and saving a change without a manual reload. The field SHALL be reachable for an invited professional who has no configuration record yet. Its labels and its refusals SHALL resolve through the i18n layer in both product languages.

#### Scenario: An administrator sets a professional's name

- **WHEN** an administrator opens a professional on the professionals screen and saves a name
- **THEN** the name is stored, the screen reflects it without a manual reload, and the list shows the professional by that name

#### Scenario: The name can be set before the professional is otherwise configured

- **WHEN** an administrator sets the name of a professional who has never been configured
- **THEN** the save succeeds and the professional is thereafter listed as configured

#### Scenario: Both languages

- **WHEN** the active language is switched between pt-BR and en on the professionals screen
- **THEN** the name field's label and any refusal it shows change to their translation with no missing-key fallback displayed
