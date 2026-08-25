## MODIFIED Requirements

### Requirement: A patient books a slot by naming its instant, and the server assigns the room

The system SHALL accept a booking request naming the appointment type, the professional, and the slot's start as a UTC instant, and SHALL NOT accept a wall-clock label as the slot's identity. The request SHALL carry no resource, and the system SHALL assign a resource itself from those of the required type that are free for the slot, regardless of any resource an availability response previously named. The appointment's end SHALL be derived from the professional's duration for that appointment type rather than supplied by the caller.

The request MAY name the patient the appointment is for, and that identifier SHALL be honoured **only** for a caller holding an operational staff role. For a caller acting as a patient the appointment SHALL belong to their own patient record, and a patient identifier they supply SHALL be refused rather than ignored, so that a caller who believed they were booking for somebody else cannot silently book for themselves. A staff caller SHALL name the patient explicitly, having no patient record of their own to fall back to.

#### Scenario: A booking succeeds and names what was created

- **WHEN** a signed-in patient books an offered slot by its start instant, for an appointment type and a professional
- **THEN** an appointment is created for that patient, professional, appointment type and start instant, ending one duration later, with a resource of the required type assigned by the server, and the response describes it

#### Scenario: The slot is identified by instant, not by local time

- **WHEN** two distinct slots on a date the clinic timezone turns its clock back read the same wall-clock time an hour apart in real time
- **THEN** booking either one by its instant creates an appointment at that instant, and the two are bookable independently

#### Scenario: A caller cannot choose the room

- **WHEN** a booking request is made
- **THEN** it carries no way to name a resource, and the resource on the created appointment is one the server selected as free for the slot

#### Scenario: A patient books for themselves and cannot name another patient

- **WHEN** a signed-in patient books without naming a patient
- **THEN** the appointment belongs to their own patient record

#### Scenario: A patient naming another patient is refused

- **WHEN** a signed-in patient books and names a patient identifier, whether their own or another's
- **THEN** the API responds `403` with code `auth.forbidden` and no appointment is created

#### Scenario: Staff book for a named patient

- **WHEN** a front-desk or administrator caller books an offered slot and names an existing patient
- **THEN** an appointment is created for that named patient, with the room assigned by the server, and the response describes it

#### Scenario: Staff must name the patient

- **WHEN** a front-desk or administrator caller books without naming a patient
- **THEN** the API responds `400` with code `validation.required` naming the missing field, and no appointment is created

#### Scenario: Staff naming a patient that does not exist

- **WHEN** a front-desk or administrator caller books naming a patient identifier that resolves to no patient
- **THEN** the API responds `404` with code `patient.not_found` and no appointment is created

#### Scenario: The end is derived, not supplied

- **WHEN** two professionals hold different durations for the same appointment type and a patient books each of them at the same time of day
- **THEN** each appointment is as long as that professional's own duration for the type

#### Scenario: The staff path obeys the same availability rules

- **WHEN** a front-desk caller books a slot that the availability computation would not offer
- **THEN** the same refusal a patient would receive is returned, with the same code, the booking rules being independent of who is booking

#### Scenario: The minimum lead time is not overridden for staff

- **WHEN** a front-desk caller books a start sooner from now than the configured minimum lead time
- **THEN** the API responds `422` with code `booking.lead_time_violation`, the lead time being a rule about what may be offered rather than about who may act

### Requirement: Booking belongs to the patient role, and an appointment reveals only its own patient's data

The system SHALL permit an authenticated patient to create an appointment for themselves, and an authenticated front-desk or administrator caller to create one for a named patient. It SHALL refuse a professional with code `auth.forbidden` (403) and an unauthenticated caller with code `auth.session_expired` (401). The response to a successful booking SHALL describe only the appointment just created; the response to a staff booking SHALL additionally name the room assigned, and the response to a patient's own booking SHALL NOT.

#### Scenario: A patient may book

- **WHEN** an authenticated patient books an offered slot
- **THEN** the request succeeds

#### Scenario: Front desk and administrators may book on a patient's behalf

- **WHEN** an authenticated front-desk or administrator caller books an offered slot for a named patient
- **THEN** the request succeeds and the appointment belongs to the named patient

#### Scenario: A professional cannot book on anybody's behalf

- **WHEN** an authenticated professional attempts to book, with or without naming a patient
- **THEN** the API responds `403` with code `auth.forbidden`, booking being reception's work rather than the clinician's

#### Scenario: An anonymous caller cannot book

- **WHEN** a booking is attempted with no session
- **THEN** the API responds `401` with code `auth.session_expired`

#### Scenario: A staff booking names the room, a patient's own does not

- **WHEN** the same slot is booked once by a front-desk caller and once by the patient themselves
- **THEN** the staff response names the assigned room and the patient response does not

### Requirement: Changing an appointment is refused inside the cancellation cutoff, for those the cutoff applies to

The system SHALL refuse a cancellation or a reschedule with code `booking.cutoff_passed` (422) when the appointment starts within the configured cancellation cutoff of the present moment and the cutoff applies to the caller. The cutoff SHALL be configurable with a default of 24 hours. Whether the cutoff applies SHALL be a decision made outside the rule itself, so that a caller with the authority to act inside the cutoff is admitted by the same rule. The cutoff SHALL apply to a patient acting on their own appointment and SHALL NOT apply to a front-desk or administrator caller, who is the clinic's remedy when a patient is refused.

The cutoff SHALL govern only the changing of an existing appointment. It SHALL NOT bear on whether a new appointment may be created, which SHALL remain governed by the configured minimum lead time for every caller.

#### Scenario: A patient cannot cancel inside the cutoff

- **WHEN** a patient cancels an appointment starting sooner than the cutoff from now
- **THEN** the API responds `422` with code `booking.cutoff_passed` and the appointment remains scheduled

#### Scenario: A patient cannot reschedule inside the cutoff

- **WHEN** a patient reschedules an appointment starting sooner than the cutoff from now
- **THEN** the API responds `422` with code `booking.cutoff_passed`, the appointment remains scheduled, and no new appointment is created

#### Scenario: A patient can act at the cutoff boundary

- **WHEN** a patient cancels an appointment starting exactly the cutoff away from now
- **THEN** the cancellation succeeds, the cutoff being a minimum notice rather than an exclusive bound

#### Scenario: A caller the cutoff does not apply to is admitted inside it

- **WHEN** the cutoff is declared not to apply and an appointment starting sooner than the cutoff is cancelled or rescheduled
- **THEN** the request succeeds

#### Scenario: The front desk cancels inside the cutoff after the patient was refused

- **WHEN** a patient is refused with `booking.cutoff_passed` on an appointment starting sooner than the cutoff, and a front-desk caller then cancels the same appointment
- **THEN** the cancellation succeeds and the time it held is offered again

#### Scenario: The front desk reschedules inside the cutoff

- **WHEN** a front-desk caller reschedules an appointment starting sooner than the cutoff to a new time with the same professional
- **THEN** the reschedule succeeds, the original is recorded as rescheduled, and the replacement is scheduled

#### Scenario: The cutoff does not admit a booking the lead time refuses

- **WHEN** a front-desk caller books a new appointment starting sooner from now than the minimum lead time
- **THEN** the request is refused, the front desk's authority over the cutoff conferring no authority over the lead time

#### Scenario: The cutoff does not affect what availability offers

- **WHEN** the cancellation cutoff is changed
- **THEN** the slots availability offers are unchanged, the cutoff governing only whether an existing appointment may be changed

### Requirement: An appointment may only be changed by the patient it belongs to

The system SHALL permit an authenticated patient to cancel or reschedule only their own appointment, and SHALL permit an authenticated front-desk or administrator caller to cancel or reschedule any patient's appointment. A patient naming an appointment belonging to another patient, and a patient naming an appointment that does not exist, SHALL both be refused with code `auth.ownership_denied` (403), so that the response cannot be used to discover which appointments exist. A staff caller naming an appointment that does not exist SHALL be refused with code `booking.appointment_not_found` (404), staff being entitled to distinguish absence from denial. A professional SHALL be refused with code `auth.forbidden` (403), and an unauthenticated caller with code `auth.session_expired` (401).

A staff reschedule SHALL be scoped to the appointment's own professional and appointment type, exactly as a patient's is.

#### Scenario: A patient may change their own appointment

- **WHEN** an authenticated patient cancels or reschedules an appointment that belongs to them
- **THEN** the request succeeds

#### Scenario: A patient cannot change another patient's appointment

- **WHEN** an authenticated patient names an appointment belonging to a different patient
- **THEN** the API responds `403` with code `auth.ownership_denied` and nothing is written

#### Scenario: An unknown appointment is indistinguishable from someone else's

- **WHEN** an authenticated patient names an appointment id that does not exist
- **THEN** the API responds `403` with code `auth.ownership_denied`, the same answer another patient's appointment produces

#### Scenario: An anonymous caller cannot change an appointment

- **WHEN** a cancellation or reschedule is attempted with no session
- **THEN** the API responds `401` with code `auth.session_expired`

#### Scenario: Front desk and administrators may change any patient's appointment

- **WHEN** an authenticated front-desk or administrator caller cancels or reschedules an appointment belonging to any patient
- **THEN** the request succeeds

#### Scenario: Staff are told when an appointment does not exist

- **WHEN** an authenticated front-desk or administrator caller names an appointment id that does not exist
- **THEN** the API responds `404` with code `booking.appointment_not_found`, rather than the answer a patient receives

#### Scenario: A professional cannot change an appointment through this path

- **WHEN** an authenticated professional attempts to cancel or reschedule
- **THEN** the API responds `403` with code `auth.forbidden`, changing an appointment being reception's work

#### Scenario: A staff reschedule cannot change the professional

- **WHEN** a front-desk caller reschedules an appointment
- **THEN** the request carries no way to name a different professional or appointment type, and the replacement keeps both

## ADDED Requirements

### Requirement: An appointment records how it was booked

The system SHALL record on every appointment whether it was booked by the patient themselves or by the clinic on their behalf, and SHALL derive that from the path the booking arrived through rather than from anything the caller supplies. A reschedule SHALL carry the original appointment's recorded source onto its replacement.

#### Scenario: A patient's own booking is recorded as self-service

- **WHEN** a patient books an appointment for themselves
- **THEN** the appointment records that the patient booked it

#### Scenario: A staff booking is recorded as front desk

- **WHEN** a front-desk or administrator caller books an appointment for a named patient
- **THEN** the appointment records that the clinic booked it

#### Scenario: The caller cannot declare the source

- **WHEN** a booking request attempts to state how it was booked
- **THEN** the request carries no such field and the recorded source is the one the path determines

#### Scenario: A reschedule preserves the source

- **WHEN** an appointment booked at the desk is rescheduled
- **THEN** the replacement records that the clinic booked it, the reschedule not changing where the appointment came from

### Requirement: Staff read a day's schedule, and a professional reads their own

The system SHALL return, for a named clinic date, the appointments and internal blocks of that day, each appointment carrying the professional, the patient, the appointment type, the assigned room, its start and end instants, its status, and whether the **patient** may still change it. The response SHALL carry the clinic timezone so its instants can be rendered as clinic wall clock.

A front-desk or administrator caller SHALL receive every professional's day, optionally narrowed to one professional. A professional SHALL receive their own day only, and any professional named in the request SHALL be disregarded rather than honoured, so that a professional cannot read another's schedule. A patient SHALL be refused with code `auth.forbidden` (403) and an unauthenticated caller with code `auth.session_expired` (401).

#### Scenario: Reception sees the day across professionals

- **WHEN** a front-desk caller requests a date on which several professionals hold appointments
- **THEN** the response contains every professional's appointments for that date, each naming its patient, its room and its professional

#### Scenario: Reception narrows the day to one professional

- **WHEN** a front-desk caller requests a date naming one professional
- **THEN** the response contains that professional's appointments for the date and no other's

#### Scenario: A professional sees only their own day

- **WHEN** a professional requests a date
- **THEN** the response contains their own appointments and blocks for that date and no other professional's

#### Scenario: A professional cannot ask for somebody else's day

- **WHEN** a professional requests a date naming a different professional
- **THEN** the response is still their own day, the named professional having been disregarded rather than obeyed

#### Scenario: Internal blocks appear beside appointments

- **WHEN** the requested date contains an active internal block
- **THEN** the block appears in the response alongside the appointments, so that declared unavailability is visible rather than inferred from a gap

#### Scenario: Terminal appointments do not clutter the day

- **WHEN** an appointment on the requested date has been cancelled or rescheduled
- **THEN** it is not presented as part of that day's schedule

#### Scenario: The day says whether the patient may still change each appointment

- **WHEN** the requested date contains an appointment starting sooner than the cancellation cutoff
- **THEN** that appointment is marked as one the patient may no longer change, decided by the server rather than by the caller

#### Scenario: A patient cannot read the day

- **WHEN** an authenticated patient requests the day's schedule
- **THEN** the API responds `403` with code `auth.forbidden`

#### Scenario: An anonymous caller cannot read the day

- **WHEN** the day's schedule is requested with no session
- **THEN** the API responds `401` with code `auth.session_expired`

### Requirement: Reception resolves a patient by their contact email

The system SHALL let a front-desk or administrator caller find a patient by their exact contact email, returning at most one patient with their identifier, their name, and whether they currently hold an active data-processing consent at the configured version. A caller of any other role SHALL be refused with code `auth.forbidden` (403). An address matching no patient SHALL be refused with code `patient.not_found` (404). The system SHALL NOT offer a partial or name-based search of patients.

#### Scenario: Reception finds a returning patient

- **WHEN** a front-desk caller looks up a patient by the exact contact email on their record
- **THEN** the response names that patient, their identifier, and their current data-processing consent state

#### Scenario: An unknown address finds nothing

- **WHEN** a front-desk caller looks up an address belonging to no patient
- **THEN** the API responds `404` with code `patient.not_found`

#### Scenario: The lookup is exact, not a search

- **WHEN** a front-desk caller supplies part of a patient's address
- **THEN** no patient is returned, the lookup being exact so that it cannot be used to enumerate patients

#### Scenario: A patient cannot look up patients

- **WHEN** an authenticated patient or professional uses the lookup
- **THEN** the API responds `403` with code `auth.forbidden`

#### Scenario: A patient whose consent is not in force is findable and flagged

- **WHEN** a front-desk caller looks up a patient who has revoked their data-processing consent
- **THEN** the patient is returned with their consent shown as not in force, so that the refusal to book is known before the booking is attempted

### Requirement: The staff console runs the day, and books on a patient's behalf

The staff application SHALL offer a professional their own schedule, and reception the day across professionals with the room shown and actions to cancel or move an appointment, and a booking surface for a walk-in or telephone booking. All three SHALL mount inside the existing staff app-shell, SHALL be reachable only by the roles that own them in the navigation as well as at the API, SHALL render times in clinic wall clock from the instants the responses carry, and SHALL surface a refusal by its error code as a translated explanation rather than a raw response. All user-facing text introduced SHALL resolve through the i18n layer in both product languages.

The booking surface SHALL search availability through the same availability read the patient portal uses, SHALL show the room a slot would use, and SHALL NOT claim that an external calendar has been consulted. It SHALL resolve the patient before searching, and SHALL report a patient whose consent is not in force before the booking is attempted rather than only as a refusal afterwards.

#### Scenario: A professional sees their own day

- **WHEN** a professional signs in and opens their schedule
- **THEN** the day's appointments and blocks are listed with their times in clinic wall clock, each appointment naming its patient and appointment type

#### Scenario: Reception runs the day across professionals

- **WHEN** a front-desk user opens the day view
- **THEN** the day's appointments are listed across professionals, each naming its professional, its patient and its room

#### Scenario: Reception books a walk-in end to end

- **WHEN** a front-desk user resolves an existing patient, searches availability, chooses a time and confirms
- **THEN** an appointment is created for that patient, the screen reports it, and it appears on the day view with its room

#### Scenario: Reception cancels an appointment the patient could not

- **WHEN** a front-desk user cancels an appointment starting sooner than the cancellation cutoff
- **THEN** the cancellation succeeds and the day view no longer shows the appointment as scheduled

#### Scenario: Reception moves an appointment through the same availability surface

- **WHEN** a front-desk user chooses to move an appointment from the day view
- **THEN** the booking surface opens scoped to that appointment's professional and appointment type, with no option to change either

#### Scenario: The day view shows whom the rule stops

- **WHEN** the day view lists an appointment the patient may no longer change
- **THEN** it is shown as one the patient cannot change while the front-desk actions remain available, rather than as one nobody can change

#### Scenario: A refusal is explained, not dumped

- **WHEN** a staff booking or a staff cancellation is refused with any booking code
- **THEN** the screen shows the translated explanation for that code and the schedule is left as it was

#### Scenario: The staff booking surface names the room and claims nothing about external calendars

- **WHEN** a front-desk user views availability for a walk-in
- **THEN** each offered time names the room it would use, and no statement is made that an external calendar was consulted

#### Scenario: Navigation reflects the role

- **WHEN** a professional signs in to the staff surface
- **THEN** the day view and booking entries are absent from the navigation, and requesting those endpoints directly is still refused by the API

#### Scenario: Both languages

- **WHEN** the active language is switched between pt-BR and en on the schedule, the day view and the booking surface, including their empty and refusal states
- **THEN** every rendered string changes to its translation with no missing-key fallback displayed
