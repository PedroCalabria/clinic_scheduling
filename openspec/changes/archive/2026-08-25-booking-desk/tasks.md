## 1. The professional's name (P-5, open since 3b)

- [x] 1.1 Add `FullName` to `Professional` with a setter that trims and rejects blank-but-present, and one EF migration adding `professionals.full_name` nullable. Nullable is a decision, not laziness (design N10): the record does not exist until first configuration and S7 lists invited-but-unconfigured professionals
- [x] 1.2 Let S7's save path set the name, including on the save that **creates** the record for a professional who had none — the existing "created on first save" behaviour, with one more thing it can be created by
- [x] 1.3 Switch `BookingOptionsEndpoints.DisplayName` to the stored name, keeping the local-part derivation as the fallback. Do **not** rename the wire field: `booking-core` called it `displayName` rather than `email` precisely so this change is server-only, and no client should need editing
- [x] 1.4 Add the name field to S7 in `apps/staff`, with pt-BR and en keys, and confirm the professionals list shows the stored name where there is one
- [x] 1.5 Close P-5 in `02-domain-model.md` §10, and update the `DisplayName` remarks in `BookingOptionsEndpoints.cs` — they currently say 5b owns this and that it is unimplemented. Both become false here
- [x] 1.6 Confirm no backfill: an administrator must be able to tell an entered name from a derived label, which a migration writing derived labels into the column would destroy

## 2. The ownership rule learns about professionals

- [x] 2.1 Add the relationship parameter to `PatientDataAccess.Evaluate` — `actorIsThisPatientsProfessional` — and admit `Role.Professional` only when it is true (design N6). The existing comment promising this arrives "with the scoping that makes the access defensible" is what is being kept; replace it with what was actually built
- [x] 2.2 Unit-test the new arm in `PatientDataAccessTests`: professional with the fact true is allowed **and recorded**; professional with it false is denied; every existing arm unchanged
- [x] 2.3 Confirm `Domain` still has no way to answer the question itself — the fact is supplied, never derived. The boundary test already fails the build on an infrastructure reference; this is the softer half of the same rule and belongs in a comment at the call site
- [x] 2.4 Give `PatientDataGuard` a set-shaped entry point that evaluates the rule per patient and saves **once** (design N7). The existing single-patient method keeps its behaviour and its callers

## 3. Booking on behalf (the role-gated patient)

- [x] 3.1 Add `PatientId` to `BookAppointmentRequest` and rewrite its remarks: the paragraph currently explains why there is no such field and names 5b as the change that adds it. Say instead what gates it
- [x] 3.2 Widen `POST /api/appointments` from the patient policy to patient-or-clinic-staff, and add one shared helper resolving the acting patient from role plus request (design N2, N3): a patient supplying any `patientId` is `auth.forbidden`; staff omitting it is `validation.required`; staff naming an unresolvable patient is `patient.not_found`
- [x] 3.3 Set `AppointmentSource.FrontDesk` at the staff call site — the first write of a value shipped unused in 5a — and keep `SelfService` on the patient path
- [x] 3.4 Confirm the professional role is refused on this path, and record why in the endpoint: booking is reception's work, and a professional who could book on behalf would be a second, unaudited route to the same write
- [x] 3.5 Return a staff-shaped response naming the assigned room, leaving `AppointmentResponse` untouched so 5a's "no room" comment stays true where it was written (design N5)
- [x] 3.6 Confirm the consent gate still runs for a staff booking, and that it reads the **patient's** consent rather than the actor's. Dropping it would let the clinic route around a patient's own withdrawal by telephoning reception

## 4. The override — cancel and reschedule from the desk

- [x] 4.1 Widen the two lifecycle routes the same way, through the same helper, and skip the ownership filter for a staff caller while keeping it exactly as it is for a patient (design N2)
- [x] 4.2 Pass `cutoffApplies: false` from the staff path and `true` from the patient path. This is the **only** functional change to the cutoff, and it is the second caller of the parameter 5b built — the behaviour is already specified at `AppointmentLifecycleTests.cs:258`, so nothing in the domain or in that test changes
- [x] 4.3 Make the staff branch return `booking.appointment_not_found` (404) for an unknown id while the patient branch keeps `auth.ownership_denied` (403). The catalogue reserved that code for exactly this branch and calls it "deliberately unreachable" from the patient file — that sentence needs updating too
- [x] 4.4 Confirm the reschedule's statement ordering is untouched and shared: the UPDATE-then-INSERT comment block is a correctness property with a silent failure mode, and the whole reason staff reuse this handler rather than getting their own (design N2)
- [x] 4.5 Carry the original appointment's `Source` onto a replacement — it already does; add the assertion, because "the reschedule preserved where it came from" is now a real question with two possible answers
- [x] 4.6 Confirm a staff reschedule still cannot change the professional or the appointment type: the request carries neither, so this is structural. Assert it rather than assume it

## 5. The day read (S1 and S4 behind one endpoint)

- [x] 5.1 Add `GET /api/schedule?date=&professionalId=`, authorized for professional-or-clinic-staff, returning the day's live appointments and active internal blocks with the professional's name, the patient's name, the appointment type, the room, the instants, the status, and the patient's `CanChange`
- [x] 5.2 Make the professional's scope **structural** (design N9): their own id from the session, any `professionalId` in the request disregarded rather than refused — the same shape as a `TimeBlock` carrying no professional. Test that a professional naming somebody else still gets their own day
- [x] 5.3 Compute `CanChange` server-side from the cutoff, for the **patient** — S4 needs to say "the patient cannot change this, you can", and a browser computing the cutoff is 5b's C10 all over again
- [x] 5.4 Write the `AccessLog` rows through the guard's set-shaped entry point, once per distinct patient in the response, before the payload is produced (design N7). A day with no appointments writes nothing
- [x] 5.5 Exclude terminal appointments from the day, and include blocks for both audiences — a receptionist needs to see declared unavailability rather than infer it from a gap
- [x] 5.6 Refuse a patient with `auth.forbidden`; confirm an anonymous caller gets `auth.session_expired` from the default policy rather than from a check written here

## 6. Reception resolves a patient

- [x] 6.1 Add `GET /api/patients/by-email`, clinic-staff only, exact match on the patient's contact email, returning at most one patient — id, name, and whether a data-processing consent is active at the configured version (design N8)
- [x] 6.2 Route it through `PatientLookup`'s existing non-disclosure shape so staff get `patient.not_found` (404) and the two answers stay in one place
- [x] 6.3 Write one `AccessLog` row when a patient is returned, and none when nothing matched
- [x] 6.4 Record at the call site why this is exact rather than a search: a name-substring search over patients is an enumeration surface, and logging every result would bury the entries that matter. The revisit trigger is in design N8 — do not add a "convenience" partial match under a screen

## 7. The room on the wire

- [x] 7.1 Add `ResourceName` to `AvailabilitySlotResponse` beside the `ResourceId` it already carries, sourced from the resource the solver named
- [x] 7.2 Record in the contract's remarks that D7 is a **patient-surface** rule and is rescoped, not weakened (design N5) — the wire has carried the room's identity since change 4, and this adds its label
- [x] 7.3 Confirm the patient portal renders no room anywhere, and add that as a check rather than an assumption. The rule now depends on a client choice, so it needs to be checked like one. **Checked:** the only room-shaped strings on any patient surface are `slots.ts`'s `resourceId` field, which nothing renders, and the trust panel's `checkedRoom` line — which names *that a free room was checked for*, not which one. D7 holds. The browser half is validation check 11, because a grep proves what is not written rather than what is not shown
- [x] 7.4 Confirm `GET /api/resources` stays administrator-only: moving a security boundary to obtain a label was the alternative this rejected

## 8. The two shared primitives (`booking-surface`'s deferral, closed)

- [x] 8.1 Take `primary-subtle` out of the `success` Alert — keep `border-primary` and `text-primary-strong`, background `surface-raised`. The border already carried the semantic; the fill was the borrowed part
- [x] 8.2 Make the `active` Badge an outline — `border border-primary`, `text-primary-strong` — rather than a fill, so it does not become distinguishable from `neutral` by text colour alone, which the badge's own comment forbids
- [x] 8.3 Touch nothing else in the primitives, and confirm P4's success card and the patient portal still read correctly after the change — they are the surfaces that inherit it. **Recorded rather than quietly widened:** two `primary-subtle` fills survive this change, both app-level rather than in the primitives — P4's own success card (`BookingSuccessPage`) and the staff shell's active navigation item. `booking-surface` named all three sites and deferred only the two shared ones, and this change's proposal says `apps/patient-portal` changes nothing beyond the two variants. So the design-system rule is now honoured in the primitives and broken in two screens; that is a smaller, visible gap rather than the scope creep of fixing it here. The browser judgement is validation check 11
- [x] 8.4 Record in `booking-surface`'s terms that its task 2.3 revisit trigger fired and how it was answered, at the call site rather than only in this change's design

## 9. S1 — a professional's own schedule

- [x] 9.1 Add the route, the guard and the navigation entry for `/staff/schedule`, professional-only in both places, following the pattern S3 established
- [x] 9.2 Build the day as a table: time, patient, appointment type, room, status — `font-mono` with tabular figures on every time and duration, the rule the staff console already honours everywhere
- [x] 9.3 Show the professional's internal blocks in the same day, visually distinct from appointments
- [x] 9.4 Handle empty, loading and error states, and render times in clinic wall clock from the instants and the timezone the response carries — never the browser's own zone
- [x] 9.5 pt-BR and en keys for every string

## 10. S4 — the day across professionals

- [x] 10.1 Add the route, the guard and the navigation entry for `/staff/day`, front-desk and administrator
- [x] 10.2 Build the day grouped by professional, showing the room, with a date picker. Design Open Question 1 asks whether one day is enough — build one day and collect the opinion rather than guessing at two
- [x] 10.3 Add the cancel action: an explicit confirmation, then the refusal or the result reported inline where it happened
- [x] 10.4 Add the move action, navigating to S5's view scoped to that appointment's professional and appointment type (design N11) — one staff availability view doing both jobs, exactly as P6 reuses P2
- [x] 10.5 Show, for an appointment inside the cutoff, that **the patient** can no longer change it while the desk still can. This sentence is the demo: it is where RBAC and ownership are visibly coexisting rather than merely implemented
- [x] 10.6 pt-BR and en keys for every string, including every `booking.*` refusal reachable from the two actions

## 11. S5 — book on behalf

- [x] 11.1 Add the route, the guard and the navigation entry for `/staff/book`, front-desk and administrator
- [x] 11.2 Resolve the patient first, by exact contact email, and show their name and consent state before any search. A receptionist should not discover `auth.consent_required` after taking a walk-in's time
- [x] 11.3 Build the **utilitarian** availability view (design N4, `06` Z2): dense rows, the room named, no trust panel, no external-calendar claim. Do **not** import the patient `SlotGrid` and do **not** move it to `packages/shared` — the revisit trigger is a third surface, and change 7 is the candidate
- [x] 11.4 Label the room as the one the slot *would* use — change 4's own words, "an explanation, not a reservation" — and show the **assigned** room from the booking result afterwards
- [x] 11.5 Book through the widened endpoint with the explicit `patientId`, and report the created appointment including its room
- [x] 11.6 Handle the scoped mode S4's move action arrives in: the professional and appointment type fixed, with no control offering to change either
- [x] 11.7 pt-BR and en keys, following the design system's content rules — sentence case, no exclamation marks, *horário* rather than "slot" where the string is patient-facing

## 12. Tests

- [x] 12.1 Integration: every role × every widened route. The two that must **fail** are the point — a patient supplying `patientId`, and a professional attempting to book or cancel on behalf
- [x] 12.2 Integration: the front-desk override end to end — a patient refused with `booking.cutoff_passed` on an appointment inside the cutoff, then the same appointment cancelled by the desk, then rescheduled by the desk with the near-move delta that catches the statement-ordering bug
- [x] 12.3 Integration: a staff booking records `FrontDesk` and a patient booking records `SelfService`; a rescheduled staff appointment keeps `FrontDesk`
- [x] 12.4 Integration: what availability will not offer, the desk cannot book either — design N1 asserted rather than argued. **Reframed during implementation:** the task asked for `booking.lead_time_violation` by name, and which near-now rule the solver reaches first — the lead time or the working hours — depends on the clinic's configured hours and on what time of day the suite runs. Naming one code would have been a flaky test asserting the solver's walk order. What is asserted instead is the claim N1 actually makes: the desk gets the **same** refusal a patient gets for the same request, and specifically not `cutoff_passed`
- [x] 12.5 Integration: `AccessLog` rows. One per distinct patient on a day read; one on a successful email lookup; none on an empty day, none on a failed lookup, none on the cancel that follows a read
- [x] 12.6 Integration: the day read's scoping — a professional naming another professional still receives their own day
- [x] 12.7 Integration: the professional's arm of the ownership rule — a professional reaching a patient who holds no appointment with them is refused
- [x] 12.8 Integration: the availability slot names its room, and the name follows a rename
- [x] 12.9 Integration: `full_name` — the migration applies, the stored name reaches `booking/options`, and a professional with no name still gets the derived label
- [x] 12.10 Unit: `PatientDataAccessTests` gains the professional arms (task 2.2). No new domain unit test is needed for the cutoff — `AppointmentLifecycleTests.cs:258` already specifies it, and adding a second would be duplicating a specification rather than testing a change

## 13. Documentation

- [x] 13.1 Update `README.md`'s status table — one cell, this change's own, made in this change's feature commit so the claim and the code reach `main` together. No new section, no rewrite
- [x] 13.2 Confirm the local-run section needs no edit: this change adds no prerequisite and no environment variable
- [x] 13.3 Confirm `07-error-codes.md` needs no new code, and update the two entries whose *notes* this change falsifies — `booking.appointment_not_found` ("deliberately unreachable" from a patient path is still true, but the staff path that reaches it now exists) and `booking.cutoff_passed` ("the front-desk override that passes it is `booking-desk`'s, not this one's")
- [x] 13.4 Update `06-ui-surfaces.md`'s S1, S4 and S5 entries to describe what was built, including S4's move action reusing S5

## 15. Seen on the running screen, and fixed

The validation guide was run on 2026-08-25 and found three defects. Recorded here rather than
folded silently into the earlier groups, because "what the browser found that the suite did not" is
the most useful thing this record can carry — 293 integration tests and three green gates had
nothing to say about any of them.

- [x] 15.1 **S5 stopped dead after naming the patient** (check 5). The gate opening the
  choose-a-time section required a chosen kind of visit — and the select that chooses one is
  *inside* that section. A bootstrap deadlock: no path existed to satisfy the condition. The gate
  is now the resolved patient alone, and the kind of visit gates the **search** instead, stated at
  the submit handler as well as by `required` on the control. **Not reachable by any test in this
  change**: the integration tier books through the API, where no such gate exists, and there is no
  component test tier. This is exactly the class of defect `00-context.md` §9 says the guide exists
  for
- [x] 15.2 **A block spanning midnight read as a block inside one day** (check 3). A period from
  08:05 on the 30th to 07:30 on the 31st rendered as `08:05–07:30` on both days — wrong by
  twenty-three hours, and the failure is a receptionist offering 15:00 on a day the professional is
  away. Now one `DayRange` component shows the date of whichever end is not the day on screen, so
  an ordinary same-day period is unchanged. Applied to appointments too: one call, and no second
  place to get it wrong. S3 was never affected, because it lists whole dates rather than a day's
  times
- [x] 15.3 **Move opened on the wrong day** (check 7). Found and fixed at the end of the previous
  session — S4 now sends `&on=<date>` beside the id, because the day read is by date and an
  appointment is only on one of them. **The stack the guide ran against predated that fix**, which
  is why the check failed; the mechanism matches the symptom exactly and the rebuilt stack carries
  the fix, but *the check has not been re-run and this line is not evidence that it passes*
- [x] 15.4 The message shown when the appointment cannot be found was `schedule.empty` — "nothing
  is booked on this day", a sentence about a day, used to mean "this appointment was not found". It
  sent the reader looking for a booking problem instead of a navigation one. Now its own key in
  both locales
- [x] 15.5 **Re-run checks 3, 5, 7 and 8 against the rebuilt stack.** 8 was blocked by 5, so it had
  never been exercised at all. **Re-run 2026-08-25: 3, 5 and 8 pass. 7 works — the reschedule
  commits — and surfaced one more presentation defect, below**
- [x] 15.6 **The move surface reported success and a "could not be found" warning at the same
  time** (check 7, second pass). `target === null` had come to mean two different things: the
  appointment was never on the day opened, *or* it had just been moved successfully — because a
  successful move takes the original to `Rescheduled`, and the day read excludes terminal
  appointments, correctly. So the appointment being moved leaves its own day at the instant the move
  succeeds. A `moved` flag now separates the two cases, and after a move the surface offers only the
  way back — which also closes a trap, since `movingId` still names the now-terminal original and a
  second click would have been refused with `booking.appointment_not_changeable`. **Worth noting
  what found this: the first pass could not, because the move never got far enough to succeed**

## 14. Definition of Done

- [x] 14.1 Unit and integration tests green in CI, and state the counts before and after rather than asserting they are unchanged — this change adds backend behaviour, so the numbers must move. **Measured locally: 248 → 255 domain unit, 292 → 324 integration, all green.** The domain gained the professional arm of the ownership rule (4) and the `Professional` name (3, one of which narrowed the shipped assertion that the record holds *no* name — see task 1.1). The integration tier gained 31 in `BookingDeskTests` plus one net from splitting the two shipped role tests this change falsifies. CI is the authority; these are the numbers the change was finished against
- [x] 14.2 Both SPA builds pass `tsc --noEmit`; `pnpm check:i18n` green on both the consistency and the usage scan
- [x] 14.3 `openspec validate booking-desk --strict` passes
- [x] 14.4 The keyboard path through S4 and S5 works, including the cancel confirmation's focus handling — `booking-surface` archived with this check unrun on P2 and named it its largest gap; three new screens is the wrong place to repeat that
- [x] 14.5 **`validation.md` run**, against a local stack, in both locales, with an Outcome recorded that states plainly what was *not* examined (`00-context.md` §9). The Google-only surfaces (S1) need a real Google client and a real account, per §9's last paragraph — the seeded professional is on a non-existent domain and cannot sign in
- [x] 14.6 The four design Open Questions have written answers or an explicit "unanswered", not
  ticks. **All four answered 2026-08-25, after the guide was run** — so each is a judgement about a
  screen somebody had used. One day on S4 (the `AccessLog` argument decided it), the room kept on
  S1, the email-only lookup kept with the telephone deliberately deferred, and the source column
  kept. Two errors in the document were corrected in the process: N8 named a remedy that does not
  exist (no screen in this product lists patients), and question 4 said "not built" about a column
  that shipped. A fifth question raised in review — a cross-date view of one professional's
  appointments — was **declined** and recorded under "Decided against" rather than left implied
- [x] 14.7 Change archived into the living spec, folding four modified and four added `booking`
  requirements, the two `identity-session` requirements, the `clinic-configuration` pair, and the
  `availability` slot requirement. **Done 2026-08-25.** All 13 operations resolved cleanly — 8
  MODIFIED found their target header, 5 ADDED were genuinely new, no REMOVED and no RENAMED.
  Counts after the fold: booking 18 → 22 requirements, clinic-configuration 11 → 12,
  identity-session 14 (modified in place), availability 12 (modified in place); no duplicate and no
  dropped header in any of the four. `openspec validate --specs --strict` passes on all five
  capabilities. The README status cell was flipped in this change's own feature commit rather than
  here, per `00-context.md` §8
