## MODIFIED Requirements

### Requirement: Patient data is protected by ownership, not only by role

Access to a patient's personal data SHALL additionally require that the caller owns that data, or holds a role that overrides ownership for a stated reason. The system SHALL NOT trust a client-supplied identifier to establish ownership; ownership SHALL be derived from the authenticated session. Holding the patient role SHALL NOT by itself grant access to any patient record.

Operational staff — front desk and administrators — SHALL be permitted to reach any patient's personal data, that being the work those roles exist to do. A professional SHALL be permitted to reach the personal data of a patient **only** where that patient appears on the professional's own schedule, and SHALL be refused otherwise. Whether that relationship holds SHALL be established by the caller and supplied to the rule as a fact, never derived inside the rule, so that the rule stays free of any notion of schedules or storage.

#### Scenario: Patient reads their own data

- **WHEN** an authenticated patient requests their own profile and consents
- **THEN** the request succeeds and returns only that patient's data

#### Scenario: Patient reads another patient's data

- **WHEN** an authenticated patient requests a profile identified as another patient's
- **THEN** the API responds `403` with code `auth.ownership_denied` and discloses nothing about whether that record exists

#### Scenario: Patient updates another patient's data

- **WHEN** an authenticated patient attempts to modify another patient's profile or consents
- **THEN** the API responds `403` with code `auth.ownership_denied` and no data is modified

#### Scenario: Client-supplied identifier cannot override the session

- **WHEN** a request carries a patient identifier that differs from the one the session resolves to
- **THEN** the session's identity governs, and the mismatched identifier never widens access

#### Scenario: A professional reaches a patient on their own schedule

- **WHEN** a professional reads a schedule containing an appointment of theirs, and that appointment names its patient
- **THEN** the access is permitted, the patient being one of their own

#### Scenario: A professional cannot reach a patient who is not theirs

- **WHEN** a professional attempts to reach the personal data of a patient who holds no appointment with them
- **THEN** the access is refused, the professional role conferring no general access to patients

#### Scenario: The relationship is a fact given to the rule, not looked up by it

- **WHEN** the ownership rule is evaluated for a professional
- **THEN** it is told whether the patient is theirs and decides from that, holding no means of finding out for itself

### Requirement: Staff access to patient personal data is recorded

When a staff member accesses a patient's personal data, the system SHALL record who accessed which patient's data, what action was taken, and when. A patient accessing their own data SHALL NOT be recorded. Access permitted by role rather than by ownership SHALL be recorded whichever staff role permitted it, including a professional reading a patient on their own schedule.

A single request disclosing several patients SHALL record every patient it disclosed, and SHALL record them before the disclosure is returned, so that the record exists whether or not the rest of the request succeeds. Acting on an appointment SHALL NOT by itself produce a record, an appointment not being the patient's personal data; the reading of the patient's name that preceded it SHALL already have been recorded.

#### Scenario: Staff access is logged

- **WHEN** a front-desk user or administrator reads a patient's personal data
- **THEN** an access record is stored identifying the acting user, the patient, the action, and the time of access

#### Scenario: Self-access is not logged

- **WHEN** a patient reads their own personal data
- **THEN** no access record is created

#### Scenario: A professional reading their own patients is logged

- **WHEN** a professional reads a schedule that names the patients of their appointments
- **THEN** an access record is stored for each patient named

#### Scenario: A day of appointments records every patient it disclosed

- **WHEN** a front-desk user reads a day containing appointments for several patients
- **THEN** one access record exists for each distinct patient named in the response

#### Scenario: A day naming no patient records nothing

- **WHEN** a staff member reads a day that contains no appointments
- **THEN** no access record is created

#### Scenario: Resolving a patient by their email is logged

- **WHEN** a front-desk user resolves a patient by their contact email and the patient is returned
- **THEN** an access record is stored for that patient

#### Scenario: Cancelling an appointment does not produce a second record

- **WHEN** a front-desk user cancels an appointment on a day they have already read
- **THEN** the cancellation itself creates no further access record
