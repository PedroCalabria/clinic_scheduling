## MODIFIED Requirements

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

## ADDED Requirements

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
