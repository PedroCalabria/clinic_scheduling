# booking Specification

## Purpose

`availability` answers *when could this appointment actually happen?* Until something writes, that
answer is only a description. This capability is the write, and it is what makes the read's promise
true: a slot the search offered can be booked, a slot the write refuses was never offered, and two
patients pressing the button at the same moment cannot both take the same time.

The guarantee stands on three floors, each doing a job the others cannot.

The **database** refuses a second live appointment overlapping an existing one for the same
professional, the same resource, or the same patient (I4, I5, I6) — exclusion constraints over the
appointment's time range, partial on the live state, so an appointment in a terminal state frees the
time it held. This is the only floor that holds under a race, where two requests each read a free
slot before either commits, and it holds whatever the code above it does or forgets to do.

The **domain** carries the rules about a single request: the duration in force at booking is baked
into the appointment's own range (I1), a professional holding no active duration for the appointment
type is refused (I2), a room of the wrong resource type cannot be assigned (I3), and the configured
minimum lead time and scheduling horizon bound the start (I8). None of these is a race, so none of
them belongs in a constraint.

The **cross-table refusal** (I7) — a booking over one of the professional's internal blocks, and a
block over one of their live appointments — spans two tables, which no constraint can express. Both
directions are checked under a transaction-scoped lock keyed on the professional, taken by each of
the two paths that mutate one professional's schedule before either reads the table the other
writes. `availability` therefore carries the other half of this rule, in the block-creation path it
first shipped unlocked.

The application check never makes the guarantee true; it makes the refusal **legible**. So every
refusal names its cause — blocked, taken, patient busy, no free room, outside working hours, inside
the lead time, beyond the horizon — because a patient told "somebody just booked it" when their
professional had in fact declared themselves unavailable goes looking for a race that never
happened.

The read and the write **cannot drift**, because they are one solver entered from two directions:
enumerate every offerable slot across a window, or walk the same steps for one requested start and
return either the room it would assign or a typed reason it would not. Working hours, exceptions,
blocks, step, lead time and horizon have a single implementation, so agreement is structural rather
than something a test has to remember to check.

Two shapes of the request are load-bearing. It carries **no resource**: the server assigns a free
room of the required type itself, whatever room an earlier availability response named, because that
naming explained the answer and never reserved anything. And it identifies the slot by **UTC
instant**, never by a wall-clock label — on a date the clinic turns its clock back, two slots
legitimately read the same local time an hour apart in real time, and only the instant tells them
apart. It carries no patient either: an appointment belongs to the signed-in caller.

Only the create path exists here. An appointment is born `Scheduled`, the one live state; the status
enum is declared whole, because which states hold time and which release it is part of the
guarantee, while the four terminal transitions — completed, no-show, cancelled, rescheduled — belong
to `booking-lifecycle`.

Established by change `booking-core` (change 5a of the build order), on top of `availability` from
change 4, whose solver it reuses and whose block-creation path it retrofits.
## Requirements
### Requirement: A patient books a slot by naming its instant, and the server assigns the room

The system SHALL accept a booking request naming the appointment type, the professional, and the slot's start as a UTC instant, and SHALL NOT accept a wall-clock label as the slot's identity. The request SHALL carry no resource, and the system SHALL assign a resource itself from those of the required type that are free for the slot, regardless of any resource an availability response previously named. The request SHALL carry no patient, and the appointment SHALL belong to the signed-in caller's own patient record. The appointment's end SHALL be derived from the professional's duration for that appointment type rather than supplied by the caller.

#### Scenario: A booking succeeds and names what was created

- **WHEN** a signed-in patient books an offered slot by its start instant, for an appointment type and a professional
- **THEN** an appointment is created for that patient, professional, appointment type and start instant, ending one duration later, with a resource of the required type assigned by the server, and the response describes it

#### Scenario: The slot is identified by instant, not by local time

- **WHEN** two distinct slots on a date the clinic timezone turns its clock back read the same wall-clock time an hour apart in real time
- **THEN** booking either one by its instant creates an appointment at that instant, and the two are bookable independently

#### Scenario: A caller cannot choose the room

- **WHEN** a booking request is made
- **THEN** it carries no way to name a resource, and the resource on the created appointment is one the server selected as free for the slot

#### Scenario: A caller cannot book for somebody else

- **WHEN** a signed-in patient books
- **THEN** the appointment belongs to their own patient record, and the request carries no way to name a different patient

#### Scenario: The end is derived, not supplied

- **WHEN** two professionals hold different durations for the same appointment type and a patient books each of them at the same time of day
- **THEN** each appointment is as long as that professional's own duration for the type

### Requirement: A new appointment is scheduled, and its duration is baked in

The system SHALL create every appointment in the scheduled state, which SHALL be the only live state. The duration in force at the moment of booking SHALL be stored on the appointment, and a later change to the professional's duration for that appointment type SHALL NOT alter any existing appointment. The system SHALL represent the completed, no-show, cancelled and rescheduled states, and SHALL NOT offer any transition into them in this change.

#### Scenario: Booking produces the scheduled state

- **WHEN** an appointment is created
- **THEN** its status is scheduled

#### Scenario: Changing a duration afterwards leaves the appointment alone

- **WHEN** an appointment has been booked and an administrator then changes that professional's duration for the appointment type
- **THEN** the existing appointment keeps the time range it was booked with, and only later availability searches reflect the new duration

#### Scenario: The terminal states exist and free the slot

- **WHEN** an appointment is recorded in a cancelled or rescheduled state
- **THEN** its time no longer prevents another appointment for the same professional, resource or patient, so a terminal state releases what it held

#### Scenario: No transition is reachable yet

- **WHEN** a caller attempts to complete, cancel, reschedule or mark a no-show on an appointment
- **THEN** no such operation is offered by this capability

### Requirement: Booking is refused unless availability would have offered the slot, and the refusal names its cause

The system SHALL refuse to book a slot that the availability computation would not offer, evaluated from the same rules and the same configured values the availability read uses, so that a slot offered by the read cannot be refused by the write and a slot refused by the write cannot be offered by the read. Each refusal SHALL name its cause: a start outside the professional's candidate hours with `booking.outside_working_hours` (422), a start sooner than the configured minimum lead time with `booking.lead_time_violation` (422), a start beyond the configured scheduling horizon with `booking.horizon_exceeded` (422), an overlap with one of the professional's active internal blocks with `booking.slot_blocked` (409), an overlap with one of their active appointments with `booking.slot_taken` (409), an overlap with one of the patient's own active appointments with `booking.patient_busy` (409), and no free resource of the required type with `booking.resource_unavailable` (409).

#### Scenario: Every offered slot is bookable and every bookable slot is offered

- **WHEN** availability is computed for a professional, appointment type and window, and each returned slot is then booked in isolation
- **THEN** every one of them is accepted; and any start the write accepts appears among the slots the read offers for the same inputs

#### Scenario: Lead time and horizon agree between the read and the write

- **WHEN** the configured minimum lead time and scheduling horizon are changed and availability is computed again
- **THEN** the earliest and latest bookable starts move with them on both paths, so the read never offers a start the write refuses for lead time or horizon

#### Scenario: A start outside working hours is refused with its own cause

- **WHEN** a booking names a start at which the professional has no candidate hours
- **THEN** the API responds `422` with code `booking.outside_working_hours` and nothing is stored

#### Scenario: A start inside the lead time is refused

- **WHEN** a booking names a start sooner from now than the configured minimum lead time
- **THEN** the API responds `422` with code `booking.lead_time_violation` and nothing is stored

#### Scenario: A start beyond the horizon is refused

- **WHEN** a booking names a start further ahead than the configured scheduling horizon
- **THEN** the API responds `422` with code `booking.horizon_exceeded` and nothing is stored

#### Scenario: A slot covered by the professional's own block is refused as blocked

- **WHEN** a booking names a start overlapping an active internal block belonging to that professional
- **THEN** the API responds `409` with code `booking.slot_blocked` and nothing is stored, the cause being distinguishable from somebody else having taken the time

#### Scenario: A slot the professional already has an appointment in is refused as taken

- **WHEN** a booking names a start overlapping an active appointment of that professional
- **THEN** the API responds `409` with code `booking.slot_taken` and nothing is stored

#### Scenario: A patient who is already busy is told so

- **WHEN** a patient books a start overlapping one of their own active appointments, with a different professional, and a resource of the required type is free for it
- **THEN** the API responds `409` with code `booking.patient_busy` and nothing is stored

#### Scenario: A slot with every room occupied is refused

- **WHEN** a booking names a start for which every active resource of the required type is occupied
- **THEN** the API responds `409` with code `booking.resource_unavailable` and nothing is stored

#### Scenario: A touching appointment is not an overlap

- **WHEN** a booking names a start at the exact instant an existing appointment or block for that professional ends
- **THEN** the booking succeeds, because touching endpoints do not overlap

### Requirement: The professional must be qualified, and the room must be of the required type

The system SHALL refuse a booking for a professional who holds no active duration for the requested appointment type with code `booking.specialty_mismatch` (422), the duration being the configuration gate that already implies the professional holds the type's specialty. The system SHALL assign only a resource whose resource type is the one the appointment type requires, and SHALL NOT be capable of creating an appointment whose resource is of another type. A booking naming an appointment type or professional that does not exist or is not active SHALL be refused with code `config.not_found` (404).

#### Scenario: An unqualified professional is refused

- **WHEN** a booking names a professional who has no active duration for the requested appointment type
- **THEN** the API responds `422` with code `booking.specialty_mismatch` and nothing is stored

#### Scenario: A qualification revoked between search and confirm is caught

- **WHEN** a slot is offered, the professional's duration for that appointment type is then cleared, and the slot is booked
- **THEN** the booking is refused rather than stored

#### Scenario: The assigned room is always of the required type

- **WHEN** any appointment is created
- **THEN** its resource is of the resource type the appointment type requires

#### Scenario: An appointment cannot be constructed with the wrong kind of room

- **WHEN** the domain is asked to create an appointment whose resource is of a resource type the appointment type does not require
- **THEN** it is refused, so no code path can persist one

#### Scenario: Unknown references are refused

- **WHEN** a booking names an appointment type or a professional that does not exist or is not active
- **THEN** the API responds `404` with code `config.not_found`

### Requirement: Overlapping appointments are impossible under concurrency

The database SHALL reject any second live appointment overlapping an existing live one for the same professional, the same resource, or the same patient, independently of the application checks, so that the guarantee holds when two requests race. The rejection SHALL be scoped to live appointments, so an appointment in a terminal state SHALL NOT prevent another in the time it held. A rejection SHALL be reported as a conflict naming one of the invariants that was violated; where a race violates more than one at once — two racers cannot see each other's uncommitted rows, so each assigns the same first-free room — the system MAY report any of them, since each is true and each has the same remedy.

#### Scenario: Two simultaneous bookings for one professional's slot cannot both succeed

- **WHEN** two requests book the same slot with the same professional at the same moment
- **THEN** exactly one appointment exists afterwards, and the loser is refused with `409` naming a collision it genuinely had — the professional's, or the room both racers were assigned

#### Scenario: Two simultaneous bookings cannot share the last room

- **WHEN** two requests with different professionals race for the only free resource of the required type
- **THEN** exactly one appointment holds that resource afterwards, and the loser is refused with `409` and code `booking.resource_unavailable`

#### Scenario: One patient cannot be in two places at once

- **WHEN** two requests book one patient into overlapping times with different professionals at the same moment
- **THEN** exactly one appointment exists for that patient afterwards, and the loser is refused with `409` naming a collision it genuinely had

#### Scenario: A patient already booked elsewhere is told so

- **WHEN** a patient holds an appointment and then books an overlapping time with a different professional, with a room free for it
- **THEN** the API responds `409` with code `booking.patient_busy`, because being in two places at once is the only thing wrong

#### Scenario: The guarantee does not depend on the application check

- **WHEN** an overlapping appointment is written directly, bypassing the application's own validation
- **THEN** the database refuses it

#### Scenario: A terminal appointment frees its time

- **WHEN** an appointment in a cancelled or rescheduled state covers a time, and a new appointment is booked for the same professional, resource and patient in that time
- **THEN** the booking succeeds

#### Scenario: A repeated confirmation does not book twice

- **WHEN** the same booking request is submitted twice in quick succession
- **THEN** one appointment exists and the second attempt is refused, because the patient now overlaps themselves

### Requirement: Booking and internal-block creation serialize per professional

The system SHALL serialize, per professional, the two paths that mutate that professional's schedule — creating an appointment and creating an internal block — by taking a transaction-scoped lock keyed on the professional before reading the state each path checks against. The lock SHALL be released when the transaction ends, whether it commits or fails. The lock SHALL NOT be relied upon for appointment-to-appointment exclusion, which the database enforces regardless.

#### Scenario: A booking and a colliding block cannot both commit

- **WHEN** a booking and an internal-block creation covering the same time for the same professional are attempted concurrently
- **THEN** exactly one of them succeeds and the other is refused as colliding with what the winner created

#### Scenario: The lock is taken before the check it protects

- **WHEN** either schedule-mutating path runs
- **THEN** the lock is held before that path reads the other table it checks against, so the read cannot see state a concurrent writer is about to change

#### Scenario: The lock does not outlive the transaction

- **WHEN** a schedule-mutating request fails after taking the lock
- **THEN** the lock is released and a subsequent request for the same professional proceeds

#### Scenario: Two professionals do not block each other

- **WHEN** bookings for two different professionals are attempted concurrently
- **THEN** both succeed, because the lock is scoped to one professional's schedule

### Requirement: Booking requires an active data-processing consent

The system SHALL refuse to book for a patient who holds no active data-processing consent at the configured current version, with code `auth.consent_required` (422), and SHALL let the patient grant it and continue without leaving the booking flow. A patient provisioned on first sign-in already holds this consent, so the refusal SHALL arise where it has since been withdrawn or superseded.

#### Scenario: A patient who withdrew consent cannot book

- **WHEN** a patient revokes their data-processing consent and then attempts to book
- **THEN** the API responds `422` with code `auth.consent_required` and no appointment is created

#### Scenario: Granting the consent unblocks the same booking

- **WHEN** the patient grants the consent from the confirmation step and submits again
- **THEN** the booking succeeds

#### Scenario: A consent of an older version does not satisfy the gate

- **WHEN** a patient holds an active consent recorded under a version other than the configured current one
- **THEN** the booking is refused with `auth.consent_required` until the current version is granted

#### Scenario: An ordinary first-time patient is not gated

- **WHEN** a patient who signed in with Google for the first time books immediately afterwards
- **THEN** the booking succeeds, because provisioning already recorded the consent

### Requirement: Booking belongs to the patient role, and an appointment reveals only its own patient's data

The system SHALL permit only an authenticated patient to create an appointment through this path, refusing other roles with code `auth.forbidden` (403) and an unauthenticated caller with code `auth.session_expired` (401). The response to a successful booking SHALL describe only the appointment just created.

#### Scenario: A patient may book

- **WHEN** an authenticated patient books an offered slot
- **THEN** the request succeeds

#### Scenario: Staff cannot book through the patient path

- **WHEN** a front-desk user, a professional or an administrator attempts to book through this path
- **THEN** the API responds `403` with code `auth.forbidden`, booking on a patient's behalf being a separate, later surface

#### Scenario: An anonymous caller cannot book

- **WHEN** a booking is attempted with no session
- **THEN** the API responds `401` with code `auth.session_expired`

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

### Requirement: The patient portal lets a patient search real availability and book

The patient portal SHALL present a search for free times by specialty, appointment type, either a chosen professional or any qualified professional, and a date window; SHALL show only genuinely free slots grouped by day in clinic wall clock; and SHALL carry the search in the address so it survives a reload and a return from later steps. It SHALL render distinct results, loading, empty, error and just-taken states. It SHALL then present the chosen slot for confirmation, collect the minimal patient data the record still lacks, show the data-processing consent state with a way to grant it, and confirm the completed booking. Every string SHALL be translated in pt-BR and en, refusals SHALL be shown as translated messages from their codes, and the surface SHALL be reachable by a patient signed in with Google.

#### Scenario: A patient searches and books end to end

- **WHEN** a patient signed in with Google searches for free times, selects a slot, confirms it, and the booking succeeds
- **THEN** the search shows only free slots, the confirmation step summarises the professional, time and appointment type, and the final step confirms the appointment was created

#### Scenario: An empty result is a success, not an error

- **WHEN** a search returns no slots
- **THEN** the screen explains that nothing is free in that window and invites another, rather than showing a failure

#### Scenario: A failing search is explained

- **WHEN** the availability request is refused because the service cannot answer or the caller has asked too often
- **THEN** the translated message for that code is shown, and the search can be retried

#### Scenario: A slot taken in the meantime is handled where it happened

- **WHEN** a patient confirms a slot that has been taken since the search
- **THEN** the translated message for the refusal is shown, that slot is no longer offered, and the search the patient had made is still on screen

#### Scenario: Times are shown in clinic wall clock

- **WHEN** slots are rendered
- **THEN** their times are the clinic's wall clock, converted from the instants using the timezone the response carries rather than the browser's own

#### Scenario: Two slots at the same local time are distinguishable

- **WHEN** a date on which the clinic timezone turns its clock back yields two slots reading the same local time
- **THEN** both are shown and are told apart on screen, rather than one being hidden

#### Scenario: The search survives a reload and a return

- **WHEN** a patient reloads the search, or goes on to the confirmation step and comes back
- **THEN** the same search and its results are shown again without being re-entered

#### Scenario: A missing contact detail is collected once

- **WHEN** a patient whose record has no contact phone reaches the confirmation step
- **THEN** the phone is requested there, saved with the booking, and not requested again on a later booking

#### Scenario: A withdrawn consent is recoverable in place

- **WHEN** a patient whose data-processing consent is not active reaches the confirmation step
- **THEN** the consent is shown with a way to grant it, and granting it allows the booking to proceed without leaving the flow

#### Scenario: Both languages

- **WHEN** the language is switched between pt-BR and en on the search with results, on the empty, error and just-taken states, on the confirmation step, and on the final confirmation
- **THEN** every string changes and no raw translation key is shown

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

