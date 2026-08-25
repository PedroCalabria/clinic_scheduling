## MODIFIED Requirements

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
