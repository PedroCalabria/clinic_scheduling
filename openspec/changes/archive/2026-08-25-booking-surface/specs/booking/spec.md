## MODIFIED Requirements

### Requirement: The patient portal lets a patient search real availability and book

The patient portal SHALL present a search for free times by specialty, appointment type, either a chosen professional or any qualified professional, and a date window; SHALL show only genuinely free slots grouped by day in clinic wall clock; and SHALL carry the search in the address so it survives a reload and a return from later steps. The search controls SHALL remain visible alongside the results, so that adjusting the search does not displace the answer. The choice between a named professional and any qualified professional SHALL be presented as an explicit choice of two, not as one entry among the professionals. It SHALL render distinct results, loading, empty, error and just-taken states.

A patient SHALL be able to choose a slot without leaving the search, exactly one slot SHALL be chosen at a time, and choosing another SHALL replace the previous choice. The chosen slot SHALL be distinguishable by more than colour, SHALL be restated in a summary naming the professional, the appointment type, its duration and its time, and proceeding to confirmation SHALL be a separate, explicit act.

The surface SHALL state what was checked for every time it offers — the professional's working hours, blocks from their external calendar, and a free room of the required type. Times, counts and durations SHALL be rendered in tabular figures so that a column of them aligns.

It SHALL then present the chosen slot for confirmation, collect the minimal patient data the record still lacks, show the data-processing consent state with a way to grant it, and confirm the completed booking. Every string SHALL be translated in pt-BR and en, refusals SHALL be shown as translated messages from their codes, and the surface SHALL be reachable by a patient signed in with Google.

#### Scenario: A patient searches and books end to end

- **WHEN** a patient signed in with Google searches for free times, selects a slot, confirms it, and the booking succeeds
- **THEN** the search shows only free slots, the confirmation step summarises the professional, time and appointment type, and the final step confirms the appointment was created

#### Scenario: Choosing a slot does not leave the search

- **WHEN** a patient chooses an offered slot
- **THEN** the slot is shown as chosen, the search and its results are still on screen, and no navigation has occurred

#### Scenario: Only one slot is chosen at a time

- **WHEN** a patient chooses one slot and then chooses another
- **THEN** the second is chosen and the first is not, without the patient having to clear the first

#### Scenario: The chosen slot is restated before committing

- **WHEN** a slot is chosen
- **THEN** a summary names the professional, the appointment type, its duration and its time in clinic wall clock, and offers the single control that proceeds to confirmation

#### Scenario: Nothing is chosen until the patient chooses

- **WHEN** results are first shown
- **THEN** no slot is chosen and the control that proceeds to confirmation is unavailable

#### Scenario: A chosen slot is not distinguished by colour alone

- **WHEN** a slot is chosen
- **THEN** its chosen state is conveyed to assistive technology as well as visually

#### Scenario: Adjusting the search does not displace the results

- **WHEN** a patient changes the date window or the professional while results are on screen
- **THEN** the search controls and the results remain visible together

#### Scenario: Any professional is an explicit choice, not an entry in a list

- **WHEN** the professional choice is presented
- **THEN** searching a named professional and searching any qualified professional are shown as two labelled options, and the list of named professionals is subordinate to the first

#### Scenario: The surface states what it checked

- **WHEN** the search is shown
- **THEN** it names the three things checked for every offered time: the professional's working hours, blocks from their external calendar, and a free room of the required type

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

### Requirement: The patient portal lets a patient see and change their appointments

The patient portal SHALL present a patient's own upcoming and past appointments, each showing the professional, the appointment type and its time in clinic wall clock, with the option to reschedule or cancel. An appointment the server reports as unchangeable SHALL be presented with its change actions **disabled and explained**, directing the patient to telephone reception, rather than offering an action that will be refused. Rescheduling SHALL reuse the booking search, scoped to the appointment's own professional and appointment type, and SHALL present the time being moved alongside the times available.

A patient rescheduling SHALL choose a new time the same way they choose one when booking: without leaving the screen, exactly one at a time, with the choice restated and committed as a separate act. Cancelling SHALL require an explicit confirmation. Every string SHALL be translated in pt-BR and en, and refusals SHALL be shown as translated messages from their codes.

#### Scenario: A patient reschedules from the portal end to end

- **WHEN** a patient opens their appointments, chooses to reschedule one, picks a new time from the search and confirms
- **THEN** the appointment shows at its new time and the original is no longer listed as upcoming

#### Scenario: Choosing a new time does not commit it

- **WHEN** a patient rescheduling chooses an offered time
- **THEN** the time is shown as chosen beside the time being moved, and the appointment is unchanged until the patient commits

#### Scenario: Only one new time is chosen at a time

- **WHEN** a patient rescheduling chooses one time and then another
- **THEN** the second is chosen and the first is not

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
