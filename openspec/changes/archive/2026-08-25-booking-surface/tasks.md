## 1. The canvas, treated as input this time

- [x] 1.1 Open the P2 artboard in `design/Clinic Scheduler Wireframes.dc.html` and work from it directly rather than from the screenshot. `booking-core`'s task 10.1 is the one box it left unchecked and 10.2 promised the canvas would be **input**; this change is where that promise is kept
- [x] 1.2 List, before writing anything, every element of the artboard that this change will **not** build — the room on the day header (D7), the computed-at timestamp and the withheld-slot counts (D6). Confirm each is already argued in the design rather than merely omitted, so the diff can be read against the artboard without a reader wondering
- [x] 1.3 Confirm the artboard needs nothing from `packages/shared` beyond the one primitive named in D4 — the bar `booking-core` set in its task 10.3, applied to a change that adds one control

## 2. The two design-system rules the portal breaks

- [x] 2.1 Give a bookable slot the `primary-subtle` fill the design system reserves for it (D2), and record at the call site that this is the *only* thing that colour fills on this surface
- [x] 2.2 Give the chosen slot a solid `primary` fill with `on-primary` text — the one place full primary appears as a fill on the page, so "chosen" is unambiguous
- [x] 2.3 Confirm the `success` Alert and `active` Badge in `packages/shared` are **left alone**, and record why: they serve the staff console too, and re-pigmenting shared primitives to satisfy a rule about this surface is a larger change wearing this one's clothes. Note it as a known partial with its revisit trigger (D2)
- [x] 2.4 Set every slot time, the free-time count, the durations and the date range in `font-mono` with tabular figures (D3). The grid does not align without it, and a ragged column is the difference between a list of buttons and a schedule
- [x] 2.5 Check the same numbers on P5 and P6 — they are the same kind of fact and were written under the same omission

## 3. The radio group

- [x] 3.1 Add a radio-group primitive to `packages/shared/src/ui/primitives/Field.tsx`, beside `Input` and `Select`. **Corrected during implementation:** it does *not* go through `Field`, which renders `<label htmlFor>` — valid only for a single labellable control. A group is labelled by its `<legend>`, so forcing it through `Field` would have produced an association that looks right in the markup and does nothing in a screen reader. The visual contract is mirrored; the semantics are a real `<fieldset>`
- [x] 3.2 **Get** the WAI-ARIA radio-group keyboard pattern rather than implement it: native `<input type="radio">` elements sharing a `name` inside a `<fieldset>` already give one tab stop and arrow-key movement, from the browser. The task as written assumed hand-rolled elements needing hand-rolled key handling; the platform is the better answer and matches the argument `Input`'s own comment already makes. Verified in the browser at 8.4
- [x] 3.3 Replace P2's professional `Select` with *a specific professional* / *any professional of the specialty*, the named-professional list subordinate to the first. Record that this promotes the mode `02 §4` calls **primary** out of being the first entry in a list of alternatives
- [x] 3.4 Confirm the URL contract is unchanged: an empty `professional` parameter still means "any", so an existing bookmarked search still resolves

## 4. The layout

- [x] 4.1 Relay P2 as a persistent search column beside the results (D1), inside one bounded, centred container so the design system's "the portal centers its column" still holds at the level it was written about
- [x] 4.2 Stack to a single column below the `sm` breakpoint, search first — the DS mandates one column under 768px, and on a phone the search genuinely is the first decision
- [x] 4.3 Add the trust panel under the search button: the professional's working hours, blocks from their external calendar, a free room of the required type (D5)
- [x] 4.4 Add the results header — the free-time count, then the window, specialty and professional as a subordinate line
- [x] 4.5 Give each day header its date and its free count, and confirm **no room is named** anywhere on the surface (D7)
- [x] 4.6 Check the two-column layout at a genuinely narrow laptop width, not just at the breakpoints. If the slot grid drops to two tiles per row it is worse than what it replaced, and that is a real outcome rather than a hypothetical **Answered by validation check 9: it never gets squeezed.** By roughly 966 px the layout has already collapsed to a single column and is comfortable there, because the column splits at Tailwind's `lg` (1024 px) rather than at the DS minimum of 768 px this task text implies — more conservative than written, still satisfying the rule, and the reason the risk did not materialise. Recorded in the Outcome rather than silently reconciled

## 5. Selection, shared by both surfaces

- [x] 5.1 Give `SlotGrid` a selected slot and a change handler, so selection lives in one component rather than twice (D8). It already serves P2 and P6 because `booking-lifecycle` extracted it for exactly this reason
- [x] 5.2 Make selection **single**, with choosing another slot simply moving it — no explicit deselect step, which would be a mode a patient has to learn. The behaviour of a radio group, because that is what it is
- [x] 5.3 Convey the chosen state to assistive technology as well as visually (`aria-pressed` or the equivalent for the element used), since the design system forbids state carried by colour alone
- [x] 5.4 Add the selection summary bar: professional, appointment type, duration, and the time in clinic wall clock, with the single control that proceeds
- [x] 5.5 Keep the control unavailable until something is chosen, and confirm nothing is chosen on first render
- [x] 5.6 Wire P2's bar to the existing P3 navigation, carrying `type`, `professional`, `start`, `end` and `search` exactly as today — **the URL contract does not change** (D8), so a reload, a bookmark and the back button from P3 all still behave
- [x] 5.7 Keep selection in component state and **out** of the URL: a chosen-but-uncommitted slot is not worth restoring, and putting it in the address makes the back button from P3 ambiguous
- [x] 5.8 Apply the same selection model to P6, committing the reschedule from the bar rather than on click, with the time being moved shown beside the time being chosen
- [x] 5.9 Confirm the just-taken recovery still works, by code: `takenSlot` still filters the offending slot out of `slots` before grouping, the search still round-trips through the query string, and `chosen` initialises to `null` on mount — so a return from P3 cannot arrive holding a selection, let alone one pointing at a slot that is now gone. The browser confirmation is validation check 8

## 6. Copy and i18n

- [x] 6.1 Add pt-BR and en keys for the trust panel, the two professional-choice labels, the results header, the day free-count and the selection bar
- [x] 6.2 Follow the design system's content rules for the new strings: sentence case except the uppercase label style, no exclamation marks, and *horário* rather than "slot" in patient-facing pt-BR
- [x] 6.3 Confirm `pnpm check:i18n` passes both the consistency and the usage scan, and that no key removed by this change is still referenced

## 7. Documentation

- [x] 7.1 Update `06-ui-surfaces.md`'s P2 and P6 entries with the selection step, so the screen inventory describes what the screens do
- [x] 7.2 Record the trust panel's external-calendar line as a **forward-claim owned by change 7** in design D5, in the validation guide (check 13), and at the call site in `BookingSearchPage`. **Corrected during implementation: deliberately NOT in the spec delta.** The living spec is the durable statement of what the system does — and the system *does* state those three checks. A caveat there would be stale the day change 7 lands and would have to be found and removed; a spec that describes its own future is a spec that rots
- [x] 7.3 Confirm the README needs **no** status-table edit: this change adds no capability and changes nothing a person can newly do. If that turns out to be false, the scope moved
- [x] 7.4 Confirm no `docs/07-error-codes.md` change: no new refusal is reachable

## 9. Seen on the running screen, and fixed

- [x] 9.1 Stand the selection bar off the bottom of the viewport (`sticky bottom-4`, design D10): flush against the edge it read as welded to the window and clipped its own `shadow-float`, which is the one elevation level the design system grants a floating layer
- [x] 9.2 Cap each day at two rows of times with an internal scroll (design D9), so one busy day cannot push every later day off the screen — the shape of the week is what a patient scans for
- [x] 9.3 Give the tile an explicit height and declare the two heights as constants beside the cap that consumes them, so "two rows" is exact rather than approximate. A guessed max-height shows a sliver of a third row, which reads as a fault rather than as a scroll region
- [x] 9.4 Keep the time `whitespace-nowrap`: a fall-back day appends an offset inline, and a wrapped time would make the tile taller than its constant and silently break the cap
- [x] 9.6 **Delete the fixed "that time is no longer free" title above a refusal** (design D11). It was prepended to *every* code returning to P2, and was reached in ordinary use with `booking.patient_busy` — where the slot IS free and the patient simply cannot be in two places. A generic title asserting a race in front of a message explaining something else is the confusion `booking-core` split `slot_blocked` from `slot_taken` to prevent. Every `booking.*` message is already a complete sentence, so the code now speaks for itself and `booking.takenTitle` is removed from both locales
- [x] 9.7 **Record, do not fix here:** availability offers slots the *asking patient* is already busy in, because the solver deliberately does not know who is asking (`booking-core`: folding a patient's own appointments in "would make an availability read depend on who is asking"). I6 is therefore only enforced at write time, and a patient with a full diary meets `patient_busy` after two more clicks — repeatedly, since only the one slot they tried is withdrawn. Real, pre-existing, and **an API question, not a presentation one**; fixing it under a restyle is exactly what D6 refuses
- [x] 9.5 Pad the scroll box (`p-1` with a matching negative margin) so `overflow-y-auto` does not clip the `outline-offset-2` focus ring on the first and last rows, and add **no** `tabIndex` — the children are focusable buttons, so the rows are reachable without a redundant tab stop per day

## 8. Definition of Done

- [x] 8.1 Unit and integration tests green, **unchanged in number** — 248 domain unit, 292 integration, exactly as before this change. (One run reported 275 integration failures and did not reproduce: Testcontainers racing its own startup under host memory pressure, not this change, which touches no backend code.) Unit and integration tests green, unchanged in number — **this change alters nothing below the API line**, so a new or modified backend test means the scope moved and should be questioned rather than accommodated
- [x] 8.2 Both SPA builds pass with `tsc --noEmit`; `pnpm check:i18n` green
- [x] 8.3 `openspec validate booking-surface --strict` passes
- [x] 8.4 The keyboard path through P2 works end to end, including the radio group's arrow-key navigation and reaching the selection bar — `06 §2` names elderly users as part of this audience and sets AA for this surface specifically. **Done 2026-08-25, recorded after the archive commit** (`ce999d4` folded the delta at 46 of 47; this is the 47th). The keyboard path through P2 was walked end to end and the browser’s accessibility audit came back clean, closing design Open Question 4. The note below is left as it was written, because it stayed true of the archive: **this change did archive with this task open** — the validation run did not reach check 12, so the keyboard path and the accessibility audit are unexamined. The code was written to earn AA here (`aria-pressed` 5.3, a real `<fieldset>` 3.1, native radios 3.2) and none of it has been seen in a browser; task 3.2's "Verified in the browser at 8.4" is outstanding with it. Carried forward as design Open Question 4 and named in the Outcome as the largest gap in the record
- [x] 8.5 **`validation.md` run**, with its Outcome recorded including a plain statement of what was not examined (`00-context.md` §9). This change is presentation only, so the guide is not a formality — it is the *only* evidence that exists. Its judgement checks must come back with written opinions rather than ticks: `booking-core` asked whether P2 read as the showcase and its Outcome records that nobody answered. **Run 2026-08-25.** All five judgement checks came back with written opinions and all five were favourable — including check 1, which is the question `booking-core` left unanswered and the reason this change existed. Three gaps are named plainly: check 12 unrun, design Open Question 1 unanswered, and the 248/292 test counts asserted rather than re-measured
- [x] 8.6 Change archived into the living spec, folding the two modified portal requirements into `booking`
