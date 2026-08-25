# availability Specification

## Purpose

The question the whole product exists to answer: **when could this appointment actually happen?**

`clinic-configuration` stores rules — what the clinic offers, who is qualified, which hours they
work. Those are rules rather than events: "every Monday 09:00" generates an event only once a date
is supplied, and under daylight saving the same rule yields different offsets across the year. This
capability is what supplies the date. It therefore runs the system's first wall-clock-to-instant
conversion, and both cases an offset cannot represent are answered rather than avoided — a local
time that does not exist resolves forward past the gap, one that occurs twice takes its earlier
occurrence, and the resulting day is genuinely shorter or longer than the clock suggests.

The load-bearing consequence is that slots are cut from the **converted** interval, never from the
wall clock before conversion. The other way round produces duplicate instants on a spring-forward
date and silently loses an hour of bookable time on a fall-back one, and it passes every test
written in a zone without daylight saving.

An answer is built by subtracting one list of busy intervals from a professional's candidate hours
and pairing what survives with a free resource of the required type. The busy list deliberately does
not record *why* somebody is busy: this capability fills it from internal `TimeBlock`s and from the
live appointments `booking` writes, and `calendar-integration` will append externally-sourced blocks
into the same subtraction. One loading step serves both this computation and booking's own check, so
the read and the write cannot see different busy sets. A resource's occupied period extends past
the appointment by its type's turnaround buffer, so time reserved for cleaning is never offered —
trailing only, and to resources rather than to professionals, because turnaround belongs to the room
and not to the person leaving it.

A slot names the professional and the resource that satisfy it, and **naming them is not reserving
them**. By the time a patient confirms, that room may be taken; the pairing explains the answer, and
`booking` assigns the resource itself rather than trusting one a caller sends back.

This capability also owns **internal time blocks** — a professional declaring their own
unavailability — because a block is only meaningful as something availability removes. Overlapping
blocks are permitted, unlike overlapping working hours: two rules covering one moment leave real
ambiguity about which applies, and two statements of being busy leave none. A block is refused,
though, where it would cover one of that professional's live appointments: a patient is already
expecting that time, and the appointment has to be dealt with first. That check spans two tables, so
it runs — like booking's own — under a transaction-scoped lock keyed on the professional, shared
between the two paths that mutate one professional's schedule.

Established by change `availability-read` (change 4 of the build order), on top of
`clinic-configuration` from changes 3a and 3b and the roles and session from change 2, and extended
by `booking-core` (5a), which filled the appointment half of the busy set and closed the collision
between a block and an appointment. The computation is deliberately uncached, since a cached slot may
already be taken.

## Requirements

### Requirement: Availability is computed from configuration for a concrete date window

The system SHALL compute, for a requested appointment type and date window, the intervals in which an appointment could actually be placed. The computation SHALL derive candidate hours from the professional's recurring working hours and exceptions, convert them against each concrete date in the clinic's configured timezone, slice them by the duration that appointment type takes that professional, remove intervals in which the professional is busy, and offer a slot only where a resource of the required type is free for it. The computation SHALL be a function of stored configuration and SHALL NOT be cached.

#### Scenario: A configured professional yields slots

- **WHEN** an authenticated caller requests availability for an appointment type over a date window, for a professional who holds the type's specialty, has a duration for it, and has working hours covering days in the window
- **THEN** the response lists slots falling inside those working hours, each naming its start instant, its end instant, the professional it belongs to, and the resource that serves it

#### Scenario: A professional with no working hours yields nothing

- **WHEN** availability is requested for a professional who has a duration for the appointment type but no working-hour segment covering any date in the window
- **THEN** the response contains no slots, and is a success rather than an error

#### Scenario: Every offered slot is exactly the professional's duration for that type

- **WHEN** two professionals hold different durations for the same appointment type and availability is requested for both
- **THEN** each professional's slots are as long as that professional's own duration, and neither adopts the other's

#### Scenario: The response is not cached

- **WHEN** any availability response is returned
- **THEN** it instructs caches not to store it, so a slot that has since been taken cannot be re-served

### Requirement: Wall-clock hours become instants against each date, and daylight saving is resolved explicitly

The system SHALL interpret a working-hour segment's wall-clock times against each concrete date using the clinic's configured timezone, producing instants. Slots SHALL be cut from the converted instant interval, never from the wall-clock interval before conversion. A local time that does not exist on a date SHALL resolve forward past the gap, and a local time that occurs twice SHALL resolve to its earlier occurrence; neither SHALL cause the request to fail.

#### Scenario: An ordinary date converts to the expected instants

- **WHEN** a segment of 09:00 to 12:00 is interpreted against a date with no clock change
- **THEN** the interval spans three hours of real time, beginning at the instant corresponding to 09:00 in the clinic timezone

#### Scenario: A date that loses an hour yields a shorter interval

- **WHEN** a segment is interpreted against a date on which the clinic timezone advances its clock, and the segment spans the transition
- **THEN** the interval is one hour shorter in real time than its wall-clock length, because the clinic is genuinely open for less time that day

#### Scenario: A start time that does not exist resolves forward

- **WHEN** a segment begins at a wall-clock time that the clinic timezone skips on that date
- **THEN** the interval begins at the first instant after the gap, and the request succeeds

#### Scenario: A date that gains an hour yields a longer interval

- **WHEN** a segment is interpreted against a date on which the clinic timezone turns its clock back, and the segment spans the transition
- **THEN** the interval is one hour longer in real time than its wall-clock length

#### Scenario: An ambiguous start time takes the earlier occurrence

- **WHEN** a segment begins at a wall-clock time that occurs twice on that date
- **THEN** the interval begins at the earlier of the two instants, and the request succeeds

#### Scenario: Slicing after conversion produces distinct, real slot starts

- **WHEN** slots are computed for a date on which the clinic timezone changes its clock
- **THEN** no two slots share a start instant, and no slot begins at an instant inside a skipped interval

### Requirement: A working-hour segment applies only on dates inside its effective period

The system SHALL match a working-hour segment to a date only when the segment's weekday matches that date's weekday AND that date falls inside the segment's effective period. An open-ended period SHALL apply to every date from its start onward. Where several active segments match one date, the candidate hours for that date SHALL be the union of all of them.

#### Scenario: A segment effective only from a future date does not apply earlier

- **WHEN** availability is requested for a date before a segment's effective period begins, and no other segment covers that date
- **THEN** no slots are offered for that date

#### Scenario: A segment that has ended does not apply afterwards

- **WHEN** availability is requested for a date after a segment's effective period ended, and no other segment covers that date
- **THEN** no slots are offered for that date

#### Scenario: A schedule change mid-window is honoured on both sides

- **WHEN** two segments for the same weekday hold different hours over consecutive effective periods, and the requested window spans the boundary
- **THEN** dates before the boundary offer slots from the earlier segment's hours and dates after it offer slots from the later segment's hours

#### Scenario: A split day contributes both segments

- **WHEN** a professional holds a morning segment and an afternoon segment on the same weekday, both effective on the requested date
- **THEN** slots are offered inside both, and none are offered in the gap between them

#### Scenario: A retired segment contributes nothing

- **WHEN** a working-hour segment has been retired
- **THEN** it produces no candidate hours on any date, whether or not its effective period covers them

### Requirement: An exception replaces a date's hours rather than reducing them

The system SHALL let a professional's active exception for a date determine that date's candidate hours outright. An unavailable-all-day exception SHALL yield no candidate hours for that date. A different-hours exception SHALL yield exactly those hours for that date, in place of every matching recurring segment. An exception SHALL affect only the professional it belongs to and only its own date.

#### Scenario: A day off removes the date entirely

- **WHEN** availability is requested for a date on which the professional has an unavailable-all-day exception, and recurring segments would otherwise cover it
- **THEN** no slots are offered for that date

#### Scenario: Different hours replace the recurring hours

- **WHEN** a professional's recurring segment covers 09:00 to 17:00 on a date and an exception gives 14:00 to 18:00 for that date
- **THEN** slots are offered only inside 14:00 to 18:00, and none between 09:00 and 14:00

#### Scenario: An exception does not affect neighbouring dates

- **WHEN** a professional has an exception on one date
- **THEN** the dates before and after it in the same window offer their ordinary recurring hours

#### Scenario: An exception does not affect another professional

- **WHEN** one professional has an unavailable-all-day exception on a date and another professional has none
- **THEN** the second professional's slots for that date are unaffected

#### Scenario: A retired exception restores the recurring hours

- **WHEN** an exception has been retired
- **THEN** its date offers the hours its recurring segments give, as though the exception had never existed

### Requirement: Slot starts step by configuration, and no slot is offered that booking would refuse

The system SHALL offer slot starts at a configured interval within the candidate hours, so consecutive candidate slots may overlap; every offered slot SHALL fit entirely within candidate hours. The system SHALL NOT offer a slot beginning sooner than the configured minimum lead time, nor a slot beginning beyond the configured scheduling horizon. The step, lead time, and horizon SHALL be configuration with defaults, and the same configured values SHALL be the ones a later booking enforces.

#### Scenario: Starts step at the configured interval

- **WHEN** candidate hours run 09:00 to 12:00, the professional's duration for the type is 40 minutes, and the configured step is 15 minutes
- **THEN** slot starts appear at 09:00, 09:15, 09:30 and onward at that step

#### Scenario: A slot that would run past the candidate hours is not offered

- **WHEN** a candidate start plus the professional's duration would end after the candidate hours end
- **THEN** that slot is not offered, even though its start lies inside the hours

#### Scenario: Slots inside the minimum lead time are withheld

- **WHEN** the window includes the present moment and some slots begin sooner than the configured minimum lead time
- **THEN** those slots are absent from the response and later ones are present

#### Scenario: Dates beyond the horizon offer nothing

- **WHEN** the requested window extends beyond the configured scheduling horizon
- **THEN** dates within the horizon offer slots and dates beyond it offer none, and the request succeeds

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

The system SHALL offer a slot only when a resource of the resource type that the appointment type requires is active and free for that slot, and SHALL name that resource on the slot — by its identifier **and by its name** — alongside its professional, so that a surface entitled to show the room can do so without a second request. A resource SHALL be treated as occupied for the time of every live appointment assigned to it, whichever professional it belongs to. Where several qualify, the system SHALL choose deterministically. A resource's occupied period SHALL extend past the appointment by its resource type's turnaround buffer, so time reserved for cleaning is not offered. The named resource SHALL NOT constitute a reservation, and a booking SHALL assign the resource itself rather than trusting a resource named by a caller.

Naming the resource on the response SHALL NOT entitle every surface to display it: whether a room is shown to the person reading a screen remains a decision of that surface.

#### Scenario: A slot names its professional, appointment type, and resource

- **WHEN** any slot is returned
- **THEN** it identifies the professional, the appointment type requested, and the concrete resource that satisfies it, by identifier and by name

#### Scenario: The named resource carries the room's name as configured

- **WHEN** a resource is renamed and availability is requested again
- **THEN** the slots name the resource by its new name

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

#### Scenario: The patient portal still names no room

- **WHEN** a patient views availability results
- **THEN** no room is displayed anywhere on the surface, the response naming one notwithstanding

### Requirement: Availability can be asked of one professional or of any qualified professional

The system SHALL accept an availability request naming a specific professional, and a request naming none. Where none is named, the system SHALL compute availability across every professional qualified for the requested appointment type and return the union, each slot naming the professional it belongs to. A professional SHALL be treated as qualified exactly when they hold an active duration for that appointment type, which the configuration rules already guarantee implies they hold its specialty.

#### Scenario: Any-professional mode unions the qualified professionals

- **WHEN** availability is requested for an appointment type without naming a professional, and two professionals are qualified for it
- **THEN** the response contains slots from both, each naming its own professional

#### Scenario: An unqualified professional contributes nothing

- **WHEN** a professional has working hours but no duration for the requested appointment type
- **THEN** none of their slots appear in any-professional mode

#### Scenario: The same time from two professionals is offered twice

- **WHEN** two qualified professionals are both free at the same time
- **THEN** both slots are present and distinguishable by professional

#### Scenario: Specific mode restricts to that professional

- **WHEN** availability is requested naming one professional
- **THEN** only that professional's slots are returned, computed identically to how they would be in any-professional mode

#### Scenario: A specific professional who is not qualified yields nothing

- **WHEN** availability is requested naming a professional who has no duration for that appointment type
- **THEN** the response contains no slots and is a success rather than an error

### Requirement: The requested window is validated, and unknown references are refused

The system SHALL refuse a malformed or oversized date window with code `availability.window_invalid`, and SHALL refuse a request naming an appointment type or professional that does not exist or is not active with code `config.not_found`.

#### Scenario: A window that ends before it begins

- **WHEN** availability is requested with a window whose end precedes its start
- **THEN** the API responds `400` with code `availability.window_invalid`

#### Scenario: A window wider than the maximum

- **WHEN** availability is requested for a window wider than the configured maximum
- **THEN** the API responds `400` with code `availability.window_invalid` and no computation is attempted

#### Scenario: An unknown appointment type

- **WHEN** availability is requested for an appointment type that does not exist or is not active
- **THEN** the API responds `404` with code `config.not_found`

#### Scenario: An unknown professional

- **WHEN** availability is requested naming a professional that does not exist or is not active
- **THEN** the API responds `404` with code `config.not_found`

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

### Requirement: Blocks belong to their professional; availability is readable by any authenticated caller

The system SHALL permit a professional to create, edit, retire and list only their own blocks. Creating a block SHALL NOT accept a professional to create it for — a new block always belongs to the caller, so it cannot be aimed at anyone else. Acting on an existing block that belongs to another professional SHALL be refused with code `auth.ownership_denied`, and an administrator or front-desk user acting on any professional's blocks SHALL be refused with code `auth.forbidden` — personal time is the professional's own, the mirror of qualification being the administrator's. Because a block is not patient data, the ownership check SHALL NOT record an access-log entry. Availability SHALL be readable by any authenticated caller and refused to an unauthenticated one, and the availability endpoint SHALL be rate-limited.

#### Scenario: A professional manages their own blocks

- **WHEN** a professional creates, edits, retires and lists blocks belonging to themselves
- **THEN** each request succeeds

#### Scenario: A new block cannot be aimed at another professional

- **WHEN** a professional creates a block
- **THEN** it belongs to them, and the request carries no way to name a different professional as its owner

#### Scenario: A professional acting on another's existing block is refused

- **WHEN** a professional attempts to edit or retire a block belonging to another professional
- **THEN** the API responds `403` with code `auth.ownership_denied` and nothing changes

#### Scenario: An administrator cannot block on a professional's behalf

- **WHEN** an administrator attempts to create, modify or list a professional's blocks
- **THEN** the API responds `403` with code `auth.forbidden`

#### Scenario: A front-desk user cannot manage blocks

- **WHEN** a front-desk user attempts to create, modify or list a professional's blocks
- **THEN** the API responds `403` with code `auth.forbidden`

#### Scenario: No access log is written for a block

- **WHEN** a professional reads or writes their own blocks
- **THEN** no patient-data access-log entry is recorded, because no patient data was involved

#### Scenario: Any authenticated role may read availability

- **WHEN** a patient, a front-desk user, an administrator, or a professional requests availability
- **THEN** the request succeeds, because availability exposes free time rather than patient data

#### Scenario: An unauthenticated availability request is refused

- **WHEN** availability is requested with no session
- **THEN** the API responds `401` with code `auth.session_expired`

#### Scenario: Repeated availability requests are rate-limited

- **WHEN** availability is requested more often than the configured limit permits
- **THEN** further requests are refused with `429` and code `auth.rate_limited`

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
