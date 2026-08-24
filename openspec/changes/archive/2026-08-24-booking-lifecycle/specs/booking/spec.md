## ADDED Requirements

### Requirement: A patient cancels their own appointment, and the time it held comes back

The system SHALL allow the patient an appointment belongs to to cancel it, moving it to a terminal cancelled state without deleting the row, so that the professional, the room and the patient are all released. The cancellation SHALL be refused if the appointment is already in a terminal state. Cancelling SHALL NOT require an active data-processing consent, since withdrawing from a service must not be blocked by having exercised a right over one's own data.

#### Scenario: A patient cancels an upcoming appointment

- **WHEN** the patient an appointment belongs to cancels it outside the cutoff
- **THEN** the appointment is recorded in a cancelled state, its row still exists with its original time range, and the request succeeds

#### Scenario: The cancelled slot is offered again

- **WHEN** a slot is booked, then cancelled, and availability is requested again for the same professional, appointment type and window
- **THEN** that slot is offered again, together with the neighbouring slots the booking had removed

#### Scenario: The cancelled appointment's room is released for another professional

- **WHEN** an appointment using the only active resource of the required type is cancelled, and availability is requested for a different qualified professional over the same time
- **THEN** slots are offered for that time again, because the room is free

#### Scenario: An already-terminal appointment cannot be cancelled again

- **WHEN** a patient cancels an appointment that is already cancelled or already rescheduled
- **THEN** the request is refused and no second write occurs

#### Scenario: A revoked consent does not trap a patient in an appointment

- **WHEN** a patient whose data-processing consent is revoked cancels their own appointment
- **THEN** the cancellation succeeds

### Requirement: A patient reschedules their own appointment to a new time with the same professional

The system SHALL allow the patient an appointment belongs to to move it to a different start time with the **same professional and the same appointment type**, by moving the existing appointment to a terminal rescheduled state and creating a new scheduled appointment linked to it. The new appointment's duration SHALL be baked from the duration in force at the moment of the reschedule, and the original appointment SHALL retain its own time range. A request naming a different professional or a different appointment type SHALL be refused. Rescheduling SHALL require an active data-processing consent at the configured current version, since it creates an appointment.

#### Scenario: A patient moves an appointment to a later time

- **WHEN** the patient an appointment belongs to reschedules it to an offerable start with the same professional and appointment type, outside the cutoff
- **THEN** the original appointment is recorded in a rescheduled state with its range unchanged, a new scheduled appointment exists at the new start, and the new appointment records which appointment it replaced

#### Scenario: A patient moves an appointment by a few minutes

- **WHEN** a patient reschedules an appointment to a start whose range overlaps the range it currently holds
- **THEN** the reschedule succeeds, the patient's own outgoing appointment not being treated as an obstacle to their new one

#### Scenario: The vacated time is offered again and the new time is not

- **WHEN** an appointment is rescheduled and availability is requested for the same professional, appointment type and window
- **THEN** the slots the original appointment had removed are offered again, and the slots the new appointment covers are absent

#### Scenario: The new time is validated exactly as a new booking would be

- **WHEN** a patient reschedules to a start that availability would not offer
- **THEN** the request is refused with the same code a booking of that start would be refused with, naming the same cause

#### Scenario: A reschedule cannot change the professional

- **WHEN** a reschedule request names a professional other than the appointment's own, or an appointment type other than the appointment's own
- **THEN** the request is refused, moving to a different professional being a cancellation followed by a new booking

#### Scenario: An already-terminal appointment cannot be rescheduled

- **WHEN** a patient reschedules an appointment that is already cancelled or already rescheduled
- **THEN** the request is refused and no new appointment is created

#### Scenario: A rescheduled appointment can itself be rescheduled

- **WHEN** an appointment that replaced an earlier one is rescheduled again
- **THEN** the request succeeds and the new appointment records the appointment it directly replaced

#### Scenario: A revoked consent refuses a reschedule

- **WHEN** a patient whose data-processing consent is revoked reschedules their own appointment
- **THEN** the request is refused with code `auth.consent_required` and no appointment is created or terminated

### Requirement: A reschedule leaves either both appointments or neither

The system SHALL apply the termination of the original appointment and the creation of its replacement as one atomic unit, so that no observer and no failure can leave an appointment terminated without a replacement, or a replacement without the original terminated. The unit SHALL serialize against the other path that mutates the same professional's schedule, as a new booking does.

#### Scenario: A refused new time leaves the original appointment untouched

- **WHEN** a reschedule is refused for any reason after the request is accepted
- **THEN** the original appointment is still scheduled with its original range, and no new appointment exists

#### Scenario: A reschedule and a colliding internal block serialize

- **WHEN** a reschedule and the creation of an internal block covering the requested new time are attempted concurrently for the same professional
- **THEN** exactly one succeeds and the other is refused as colliding

#### Scenario: The database refuses a reschedule that would double-book

- **WHEN** a reschedule's new time is taken by another appointment for the same professional, room or patient between validation and commit
- **THEN** the request is refused naming that cause, and the original appointment remains scheduled

### Requirement: Changing an appointment is refused inside the cancellation cutoff, for those the cutoff applies to

The system SHALL refuse a cancellation or a reschedule with code `booking.cutoff_passed` (422) when the appointment starts within the configured cancellation cutoff of the present moment and the cutoff applies to the caller. The cutoff SHALL be configurable with a default of 24 hours. Whether the cutoff applies SHALL be a decision made outside the rule itself, so that a caller with the authority to act inside the cutoff is admitted by the same rule.

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

#### Scenario: The cutoff does not affect what availability offers

- **WHEN** the cancellation cutoff is changed
- **THEN** the slots availability offers are unchanged, the cutoff governing only whether an existing appointment may be changed

### Requirement: An appointment may only be changed by the patient it belongs to

The system SHALL permit only an authenticated patient to cancel or reschedule an appointment through this path, and only their own. A request naming an appointment belonging to another patient, and a request naming an appointment that does not exist, SHALL both be refused with code `auth.ownership_denied` (403), so that the response cannot be used to discover which appointments exist. An unauthenticated caller SHALL be refused with code `auth.session_expired` (401).

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

#### Scenario: Staff cannot change an appointment through this path

- **WHEN** a front-desk user, a professional or an administrator attempts to cancel or reschedule through this path
- **THEN** the API responds `403` with code `auth.forbidden`, acting on a patient's behalf being a separate, later surface

### Requirement: One appointment cannot be changed twice at once

The system SHALL ensure that concurrent attempts to change the same appointment result in exactly one transition, so that a cancellation and a reschedule racing on one appointment cannot both take effect.

#### Scenario: Two concurrent cancellations yield one cancellation

- **WHEN** the same appointment is cancelled by two simultaneous requests
- **THEN** exactly one succeeds and the other is refused as already terminal

#### Scenario: A concurrent cancellation and reschedule yield one outcome

- **WHEN** the same appointment is cancelled and rescheduled by two simultaneous requests
- **THEN** exactly one succeeds; if the cancellation wins no replacement appointment exists, and if the reschedule wins the appointment is rescheduled rather than cancelled

### Requirement: A patient sees their own appointments and whether they can still change them

The system SHALL return, to an authenticated patient, only their own appointments, separated into those still to come and those already past, each carrying the professional, the appointment type, its start and end instants, its status, and **whether the caller may still change it**. Whether an appointment may still be changed SHALL be decided by the server and carried in the response, never left to the caller to compute.

#### Scenario: A patient sees only their own appointments

- **WHEN** an authenticated patient requests their appointments
- **THEN** the response contains their appointments and no appointment belonging to any other patient

#### Scenario: Terminal appointments are visible with their status

- **WHEN** a patient has cancelled and rescheduled appointments
- **THEN** they appear in the response with the status that describes what happened to them, rather than being absent

#### Scenario: The response says whether an appointment can still be changed

- **WHEN** a patient has one appointment outside the cutoff and one starting sooner than the cutoff
- **THEN** the first is marked as changeable and the second is not

#### Scenario: A terminal appointment cannot be changed

- **WHEN** a patient's appointment is in a terminal state
- **THEN** it is marked as not changeable

#### Scenario: An anonymous caller sees nothing

- **WHEN** the appointment list is requested with no session
- **THEN** the API responds `401` with code `auth.session_expired`

### Requirement: The patient portal lets a patient see and change their appointments

The patient portal SHALL present a patient's own upcoming and past appointments, each showing the professional, the appointment type and its time in clinic wall clock, with the option to reschedule or cancel. An appointment the server reports as unchangeable SHALL be presented with its change actions **disabled and explained**, directing the patient to telephone reception, rather than offering an action that will be refused. Rescheduling SHALL reuse the booking search, scoped to the appointment's own professional and appointment type. Cancelling SHALL require an explicit confirmation. Every string SHALL be translated in pt-BR and en, and refusals SHALL be shown as translated messages from their codes.

#### Scenario: A patient reschedules from the portal end to end

- **WHEN** a patient opens their appointments, chooses to reschedule one, picks a new time from the search and confirms
- **THEN** the appointment shows at its new time and the original is no longer listed as upcoming

#### Scenario: A patient cancels from the portal end to end

- **WHEN** a patient opens their appointments, chooses to cancel one and confirms
- **THEN** the appointment is shown as cancelled and its slot is offered again in a fresh search

#### Scenario: The reschedule search is scoped to the same professional

- **WHEN** a patient opens the reschedule screen for an appointment
- **THEN** the search offers times for that appointment's professional and appointment type only, with no option to change either

#### Scenario: An appointment inside the cutoff shows the rule rather than failing

- **WHEN** a patient views an appointment starting sooner than the cutoff
- **THEN** its reschedule and cancel actions are disabled with a translated explanation naming reception as the way to proceed

#### Scenario: A cutoff that passes while the screen is open is handled

- **WHEN** a patient attempts a change that the server refuses with `booking.cutoff_passed`
- **THEN** a translated message is shown and the list reflects that the appointment can no longer be changed

#### Scenario: Times are shown in the clinic's timezone

- **WHEN** a patient views their appointments from a device in a different timezone
- **THEN** every time shown is the clinic's wall clock, converted from the instants the response carries

#### Scenario: The confirmation screen links onward to the appointment list

- **WHEN** a patient completes a booking
- **THEN** the confirmation's onward link reaches their appointment list

## MODIFIED Requirements

### Requirement: A booked appointment is unavailable time

The system SHALL treat a live appointment as an interval in which its professional is busy and its resource is occupied, contributing to the same subtraction the availability computation already performs on internal blocks. A resource occupied by an appointment SHALL carry its resource type's turnaround buffer. An appointment in a terminal state SHALL contribute nothing to either, and this SHALL hold for a terminal state reached through the product, not only for one recorded directly.

#### Scenario: A booked slot is no longer offered

- **WHEN** a slot is booked and availability is requested again for the same professional, appointment type and window
- **THEN** that slot is absent from the response, and slots elsewhere in those hours are present

#### Scenario: A booking removes the neighbouring overlapping slots

- **WHEN** a slot is booked in a window where slot starts step more finely than the appointment's duration
- **THEN** every offered slot that would have overlapped the booked appointment is absent

#### Scenario: A booking occupies its room for other professionals too

- **WHEN** a slot is booked using the only active resource of the required type, and availability is requested for a different qualified professional over the same time
- **THEN** no slot is offered for that time, because the room is taken

#### Scenario: A room is not offered again until its turnaround has passed

- **WHEN** an appointment ends and its resource type carries a turnaround buffer
- **THEN** slots beginning within that buffer are not offered for that resource, while the professional is offerable at the instant their appointment ends

#### Scenario: A terminally-stated appointment stops being busy

- **WHEN** an appointment is recorded in a terminal state
- **THEN** the slots it had removed are offered again

#### Scenario: An appointment cancelled through the API stops being busy

- **WHEN** a patient cancels an appointment through the API and availability is requested again
- **THEN** the slots it had removed are offered again, the same result a directly-recorded terminal state produces

#### Scenario: A terminal appointment no longer blocks an internal block

- **WHEN** a professional creates an internal block covering the time of one of their cancelled or rescheduled appointments
- **THEN** the block is accepted, a terminal appointment being no obstacle
