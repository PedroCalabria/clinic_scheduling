## ADDED Requirements

### Requirement: A professional's clinical configuration exists separately from their identity

The system SHALL hold a professional's clinical configuration on a record separate from the user account that identifies them, created when an administrator first configures them rather than when they are invited. An administrator SHALL be able to see every invited professional, whether or not they have been configured and whether or not they have yet signed in. Configuring a professional SHALL NOT alter their identity, their role, or their sign-in path.

#### Scenario: Every invited professional is listed

- **WHEN** an administrator opens the professionals screen after professionals have been invited
- **THEN** every user holding the professional role is listed, including those who have never signed in and those with no configuration yet, each distinguishable by whether it has been configured

#### Scenario: The configuration record is created on first save

- **WHEN** an administrator saves configuration for an invited professional who has none
- **THEN** the professional's configuration record is created and holds what was saved
- **AND** no second user, role change, or credential is created

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

## MODIFIED Requirements

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
