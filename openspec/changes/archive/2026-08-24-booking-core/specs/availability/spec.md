## MODIFIED Requirements

### Requirement: Busy intervals are subtracted from candidate slots

The system SHALL remove from the offered slots any slot overlapping an interval in which the professional is busy. Busy intervals SHALL be taken from the professional's active internal time blocks AND from their live appointments, both contributing to one set through the same subtraction. The computation SHALL treat busy intervals as one set regardless of origin, so that externally-sourced blocks can later contribute to the same subtraction without changing how it is performed. The internal-block and appointment intervals for a window SHALL be read in one place serving both the availability computation and the booking check, so the two cannot see different busy sets. Touching endpoints SHALL NOT count as an overlap.

#### Scenario: A block removes the slots it covers

- **WHEN** a professional has an active internal block covering part of a date's working hours
- **THEN** slots overlapping the block are absent from the response and slots elsewhere in those hours are present

#### Scenario: An appointment removes the slots it covers

- **WHEN** a professional has a live appointment covering part of a date's working hours
- **THEN** slots overlapping the appointment are absent from the response and slots elsewhere in those hours are present

#### Scenario: A slot merely abutting a block is still offered

- **WHEN** a slot ends at the exact instant a block begins, or begins at the exact instant a block ends
- **THEN** that slot is offered, because touching is not overlapping

#### Scenario: A slot merely abutting an appointment is still offered

- **WHEN** a slot begins at the exact instant one of the professional's appointments ends
- **THEN** that slot is offered, the professional carrying no turnaround of their own

#### Scenario: Overlapping blocks subtract their union

- **WHEN** a professional has two active internal blocks that overlap each other
- **THEN** every slot overlapping either block is absent, and the result is the same as it would be for one block spanning both

#### Scenario: A block and an appointment subtract together

- **WHEN** a professional has both an active internal block and a live appointment in one date's working hours
- **THEN** slots overlapping either are absent, and the two are subtracted identically regardless of which caused it

#### Scenario: A retired block subtracts nothing

- **WHEN** an internal block is retired
- **THEN** the slots it had removed are offered again

#### Scenario: A terminally-stated appointment subtracts nothing

- **WHEN** an appointment reaches a state that is no longer live
- **THEN** the slots it had removed are offered again

#### Scenario: A block outside working hours changes nothing

- **WHEN** a professional creates an internal block covering a period in which they have no candidate hours
- **THEN** the block is stored and the offered slots are unchanged

#### Scenario: A block affects only its own professional

- **WHEN** one professional has an active internal block and another has none over the same period
- **THEN** only the first professional's slots are reduced

#### Scenario: An appointment affects only its own professional's own time

- **WHEN** one professional has a live appointment and another qualified professional is free over the same period
- **THEN** only the first professional's slots are reduced for that reason, the room's occupancy being accounted for separately

### Requirement: A slot names a free resource of the appointment type's required resource type

The system SHALL offer a slot only when a resource of the resource type that the appointment type requires is active and free for that slot, and SHALL name that resource on the slot alongside its professional. A resource SHALL be treated as occupied for the time of every live appointment assigned to it, whichever professional it belongs to. Where several qualify, the system SHALL choose deterministically. A resource's occupied period SHALL extend past the appointment by its resource type's turnaround buffer, so time reserved for cleaning is not offered. The named resource SHALL NOT constitute a reservation, and a booking SHALL assign the resource itself rather than trusting a resource named by a caller.

#### Scenario: A slot names its professional, appointment type, and resource

- **WHEN** any slot is returned
- **THEN** it identifies the professional, the appointment type requested, and the concrete resource that satisfies it

#### Scenario: No resource of the required type means no slots

- **WHEN** availability is requested for an appointment type whose required resource type has no active resources
- **THEN** no slots are offered, however free the professional is

#### Scenario: Deactivating the last resource of the type removes the slots

- **WHEN** the only active resource of the required type is deactivated and availability is requested again
- **THEN** no slots are offered

#### Scenario: The choice among free resources is deterministic

- **WHEN** several resources of the required type are free for the same slot
- **THEN** the same one is named every time the same question is asked, rather than varying between requests

#### Scenario: A slot falls through to another resource when the first is occupied

- **WHEN** one resource of the required type is occupied by a live appointment for a slot and another is free
- **THEN** the slot is offered and names the free resource

#### Scenario: A slot is withheld when every resource of the type is occupied

- **WHEN** every active resource of the required type is occupied by live appointments for a slot
- **THEN** that slot is not offered, even though the professional is free

#### Scenario: A room booked by one professional is unavailable to another

- **WHEN** the only active resource of the required type is taken by one professional's live appointment, and availability is requested for a different qualified professional over the same time
- **THEN** no slot is offered for that time

#### Scenario: A resource's turnaround buffer is kept out of the bookable window

- **WHEN** a resource is occupied until a given time and its resource type carries a turnaround buffer
- **THEN** slots beginning within that buffer after the occupied period are not offered for that resource

#### Scenario: A professional's busy period carries no turnaround buffer

- **WHEN** a professional is busy until a given time
- **THEN** a slot beginning exactly at that time is offered, because turnaround belongs to the resource rather than to the professional

### Requirement: A professional declares their own unavailability as internal time blocks

The system SHALL let a professional record intervals in which they are unavailable, stored as instants rather than as wall-clock rules, and marked as internally sourced so that externally-sourced unavailability can later be held in the same form. A block SHALL end after it begins, and any other range SHALL be refused with code `block.invalid_range`. A block overlapping one of that professional's live appointments SHALL be refused with code `booking.block_overlaps_appointment` (409), and nothing SHALL be stored; the message SHALL let the professional understand that an appointment must be dealt with first. This check SHALL be performed under the same per-professional transaction lock the booking path takes, so a block and an appointment cannot both be created into the same time by concurrent requests. Two blocks belonging to one professional MAY overlap. A block SHALL be retired rather than deleted.

#### Scenario: A block is created

- **WHEN** a professional records a block whose end is after its start
- **THEN** the block is stored against them, marked internally sourced, and appears in their list of blocks

#### Scenario: A reversed range is refused

- **WHEN** a professional records a block whose end precedes its start
- **THEN** the API responds `422` with code `block.invalid_range` and nothing is stored

#### Scenario: A zero-length range is refused

- **WHEN** a professional records a block whose end equals its start
- **THEN** the API responds `422` with code `block.invalid_range` and nothing is stored

#### Scenario: A block over one of the professional's appointments is refused

- **WHEN** a professional records a block whose range overlaps one of their own live appointments
- **THEN** the API responds `409` with code `booking.block_overlaps_appointment` and nothing is stored

#### Scenario: A block over a terminally-stated appointment is accepted

- **WHEN** a professional records a block over a time held only by an appointment that is no longer live
- **THEN** the block is stored, because that appointment no longer occupies the time

#### Scenario: A block merely abutting an appointment is accepted

- **WHEN** a professional records a block beginning at the exact instant one of their appointments ends
- **THEN** the block is stored, because touching is not overlapping

#### Scenario: Moving a block onto an appointment is refused and changes nothing

- **WHEN** a professional edits a block so that its new range would overlap one of their live appointments
- **THEN** the API responds `409` with code `booking.block_overlaps_appointment` and the stored range is left untouched

#### Scenario: A block over another professional's appointment is unaffected

- **WHEN** a professional records a block over a time in which a different professional has an appointment
- **THEN** the block is stored, the check being scoped to the block's own professional

#### Scenario: Overlapping blocks are accepted

- **WHEN** a professional records a block overlapping one they already hold
- **THEN** both are stored, because two statements of being busy do not conflict

#### Scenario: A block is edited

- **WHEN** a professional changes the range of a block they hold to another valid range
- **THEN** the block holds the new range, and an invalid new range is refused with `block.invalid_range` while the stored range is left untouched

#### Scenario: Retiring a block preserves it

- **WHEN** a professional retires a block
- **THEN** the block still exists in a retired state, is excluded from availability computation, and is distinguishable from an active one

### Requirement: The staff surface lets a professional manage their own blocked time

The staff console SHALL present a professional with their blocked time as a list they can add to, edit and retire, entering and reading times as clinic wall clock. A refusal SHALL be shown where the action was attempted rather than at the top of the page, including the refusal of a block that collides with one of their appointments. The surface SHALL appear in navigation only for professionals, and SHALL be fully translated in pt-BR and en.

#### Scenario: A professional manages blocked time end to end

- **WHEN** a professional opens their blocked-time screen, adds a block, edits it, and retires it
- **THEN** each step is reflected on screen without a manual reload, and retired blocks remain distinguishable from active ones

#### Scenario: Times are entered and shown in clinic wall clock

- **WHEN** a professional enters a block's start and end
- **THEN** the times are interpreted in the clinic's configured timezone and displayed as entered, with no offset arithmetic asked of the user

#### Scenario: An invalid range is explained where it was entered

- **WHEN** a professional submits a block whose end does not follow its start
- **THEN** the translated message for `block.invalid_range` appears within the form that was submitted, and the list is unchanged

#### Scenario: A collision with an appointment is explained where it was entered

- **WHEN** a professional submits a block overlapping one of their own live appointments
- **THEN** the translated message for `booking.block_overlaps_appointment` appears within the form that was submitted, and the list is unchanged

#### Scenario: Navigation reflects the professional role

- **WHEN** a professional signs in to the staff console
- **THEN** blocked time appears in their navigation
- **AND** it does not appear for an administrator or a front-desk user, and requesting the route directly renders no protected data for them

#### Scenario: Both languages

- **WHEN** the language is switched between pt-BR and en on the blocked-time list and with the form open, including with a refusal displayed
- **THEN** every string changes and no raw translation key is shown
