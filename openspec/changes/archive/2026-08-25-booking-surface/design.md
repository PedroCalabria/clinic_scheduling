## Context

Every previous change in this project added a capability. This one adds none. It is the first change
whose entire output is *how an existing thing presents itself*, which makes it the first change where
"is this better?" cannot be settled by a test.

That is worth saying out loud, because it changes what the artifacts are for. There is no invariant to
protect and no race to close. What there is instead is a **source of truth that already existed and was
not used** — the P2 artboard, dated three days before P2 was written — and a set of decisions about how
faithfully to follow it now that its claims can be checked against a system that exists.

Three of the artboard's elements turn out to be things the system cannot honestly say yet. Two are
excluded and one is kept. Those three decisions (D5, D6, D7) are the substance of this document; the
rest is layout.

Decision ids are `D1…`. The namespace is shared with `walking-skeleton`, `clinic-catalog` and
`staff-google-guard`, which also used `D`; nothing in this project references another change's decision
ids, and the document letters (`02` A–G, `03` G–J, `04` K–V, `05` W–Y) are always named with their
document.

## Goals / Non-Goals

**Goals:**

- Make P2 read as the showcase `06 §Z2` claims it is, and give the next validation run something
  specific to hold an opinion about.
- Close the gap between the design system's stated rules and the portal's actual use of them — the two
  cases where the console obeys a rule the portal ignores.
- Let a patient compare before committing, on both P2 and P6.
- Change **nothing** below the API line, so that a restyle cannot break a guarantee.

**Non-Goals:**

- Any change to what availability offers, what booking accepts, or what either refuses.
- The response-size mitigations. F8 was measured and re-armed at ~10 professionals; this change adds no
  consumer and does not move that number.
- Staff surfaces. The console already honours the rules this change restores to the portal.
- A general design-system component library. One primitive is added, for one control (D4).

## Decisions

### D1 — The search sits beside the results, not above them

The search moves from a full-width card stacked above the results into a persistent left column.

*Why this is not merely arrangement.* Availability is a **read you adjust**. A patient widens the
window, switches to "any professional", tries the next fortnight — and today each of those pushes the
answer down the page and out of view, so the loop is *change → scroll → read → scroll back*. Beside it,
the loop is *change → read*. The screen stops being a form with output underneath and becomes an
instrument.

*The tension worth naming.* `03`'s design-system readme says the portal *"centers its single booking
column — the one place centering is correct"* and *"one decision per screen."* A two-column workspace is
not obviously that. Resolved in favour of the artboard: the readme's rule is about the portal's
*narrative* screens — P1, P3, P4, P7, each of which asks for exactly one thing — and P2 is the one
portal screen that is not narrative. The whole page still sits in one centred, bounded container, so
"the portal centers its column" survives at the level it was written about.

*Below the `sm` breakpoint the columns stack*, search first. The DS mandates a single column under
768px, and on a phone the search genuinely is the first decision.

*Alternatives.* (a) Keep it stacked and restyle in place — cheapest, and leaves the scroll loop, which
is the actual complaint. (b) Collapse the search into a summary bar that expands — fashionable, hides
the controls a patient is meant to fiddle with, and adds a state to every one of P2's five.

### D2 — `primary-subtle` starts filling the only thing it was reserved for

`#DEF4F0` fills a bookable slot, and stops filling anything else on this surface.

The design system's own words are *"`#DEF4F0` fills nothing but a bookable slot"* and *"if two things on
a screen look clickable in green, one is wrong."* Today it fills the P4 success panel, the `success`
Alert and the `active` Badge; slots are `surface`, indistinguishable from the card they sit on. The
consequence is not subtle — it is why the built screen reads as a list of buttons.

*Scope of the correction.* The slot gets the fill. The **selected** slot gets solid `primary` with
`on-primary` text — the artboard's dark tile — which is also the only place on the page where full
primary appears as a fill, so what "chosen" means is unambiguous.

*What is deliberately left alone.* The `success` Alert and `active` Badge keep it. Those are in
`packages/shared` and used by the staff console, and re-pigmenting shared primitives to satisfy a rule
about *this* surface would be a much larger change wearing this one's clothes. Recorded as a known
partial: the rule is now true on the surface that matters and not yet true globally. **Revisit trigger:**
a change that touches the shared primitives for their own reasons.

*State is never carried by colour alone* (the DS is explicit). The selected slot also carries
`aria-pressed`, a check affordance, and its appearance in the selection bar.

### D3 — Times, counts and durations become mono with `tnum`

Every number a machine measured is set in IBM Plex Mono with tabular figures: slot times, the free-time
count, durations, the date range.

The DS says *"Numbers are literal and always mono"* with `'tnum' 1` always on, and `theme.css` already
defines `--font-mono`. The staff console applies it on every duration, buffer and time. The booking flow
applies it nowhere.

*Why it matters more here than in the console, despite the console being the one that obeys it.* A grid
of proportional times does not line up: `11:00` is narrower than `08:45`, so a column of slots is
visibly ragged and the eye cannot scan it. Tabular figures are what turn the grid into a schedule. This
is the single cheapest change in the document and probably the most visible.

### D4 — One new primitive: a radio group

`packages/shared` has `Field`, `Input`, `Select`, `Label`, `Button`, `Alert`, `Badge`, `Card`, `Dialog`,
`Table`. It has no radio. The specific-versus-any choice is a genuine two-option, mutually-exclusive
selection where both options must be visible — which is what a radio group is and what a select is not.

*Why not keep the select.* Today "any professional" is the first `<option>`. That makes the mode `02 §4`
calls **primary** — the union across everyone qualified, the thing that makes this a scheduler rather
than a directory — look like an escape hatch at the top of a list. The artboard promotes it to a peer.

*The bar this clears.* `booking-core`'s task 10.3 set it: *"add only genuinely required primitives."*
One control, needed by the design, absent from the library, reusable by the console later.

*Amended during implementation, twice, and both corrections make it simpler.*

**It does not go through `Field`.** `Field` renders `<label htmlFor={id}>`, which is only a valid
labelling mechanism for a single labellable control; a group of radios is labelled by its `<legend>`.
Wiring it through `Field` would have produced an association that reads correctly in the markup and
does nothing in a screen reader — the exact failure `Field`'s own doc comment exists to prevent. So the
component mirrors `Field`'s *visual* contract (label, hint, spacing) and uses a real `<fieldset>` for
the semantics.

**The keyboard pattern is not implemented, it is inherited.** Native `<input type="radio">` elements
sharing a `name` inside a fieldset already give one tab stop and arrow-key movement between options,
from the browser. This decision originally said the primitive would *implement* the WAI-ARIA pattern,
which assumed hand-rolled elements. It does not need to, and the reason it does not is the same
argument `Input`'s comment already makes: Radix earns its place on widgets the platform does not
provide accessibly, and this is not one of them. What a hand-rolled pair of styled divs gets wrong is
exactly what the platform gets right for free.

### D5 — The trust panel keeps the artboard's wording, and the forward-claim is recorded

The panel reads *Checked for every time — the professional's working hours · blocks from their external
calendar · a free room of the required type.*

**The middle line is not true yet.** External blocks arrive with `calendar-inbound` (change 7). Today
every `TimeBlock` is `source = Internal`: a professional blocking their own time on S3. The solver
subtracts blocks without caring about their source, so the sentence becomes true the moment change 7
lands, with no edit to this screen.

*Kept by explicit decision.* The alternative — wording it as "time the professional has blocked" now and
rewording it later — means a screen whose copy has to be revisited, and a claim that gets *weaker*
exactly when the system gets stronger. The panel states the tri-constraint promise the product is built
around; `02 §4` defines that promise as including external blocks, and `01` makes UC-2 an integration
star.

*What makes this acceptable rather than a lie.* A professional's own blocks are subtracted today, so the
line is not describing a check that does not happen — it is naming a source that does not exist yet. It
is a **forward-claim with a named owner and a date**: change 7. It is recorded here, in the spec delta,
and in the validation guide, so nobody discovers it.

*What would make it unacceptable.* Shipping this to a real clinic while telling them their
professionals' Google calendars are consulted. This is a portfolio deployment with no connected
calendars at all. **Revisit trigger:** the first real clinic, or change 7 slipping out of the plan —
either one turns this from a forward-claim into a false one.

*Alternatives.* (a) Reword now, reword back later — two edits and a weaker screen in between. (b) Drop
the line and list two checks — the panel is the tri-constraint argument, and a tri-constraint argument
missing a constraint is not worth making. (c) Ship it with a footnote — a caveat about a capability
under a trust panel undoes the panel.

### D6 — `COMPUTED 12:04:51` and the withheld-slot counts are excluded, and it costs something to exclude them

The artboard carries two elements this change does not build:

- a **computed-at timestamp**, and
- **`3 TIMES HIDDEN · 2 EXTERNAL BLOCK · 1 NO ROOM`** under each day.

Both need data `AvailabilityResponse` does not carry — it holds `appointmentTypeId`, `from`, `to`,
`timezone`, `slots` and nothing else. So both are API changes, and this change is scoped to alter
nothing below the line.

*The second one is more interesting than it looks, and is worth writing down so it is not rediscovered.*
The solver **already computes exactly that taxonomy**. `AvailabilitySolver.Explain` returns a typed
reason per candidate start — outside candidate hours, inside lead time, beyond horizon, overlapping a
block, overlapping an appointment, no free resource — because `booking-core` needed refusals to be
legible on the *write* path. `Solve` walks the same steps for the read and discards the reasons.
Surfacing them is not new logic; it is not throwing away an answer that already exists.

*Why it is still excluded.* It would be the most substantial trust feature on the flagship screen, and
it deserves to be argued on its own rather than smuggled in under "styling". It also has a real cost:
F8 was just measured at ≈170 bytes per slot with the worst realistic case at 614 KiB, and a per-day
withheld-reason breakdown adds to that. And a patient told *"2 external block"* today would be told
something about a source that does not exist (see D5) — so this element is genuinely blocked on change
7 in a way the trust panel's wording is not.

*Recorded as a candidate.* The computed-at timestamp is the cheaper half and nearly free: it is one
field, it makes `Decision S`'s never-cached posture visible instead of merely true, and it is the honest
answer to "how old is this?" on a screen whose whole claim is freshness.

### D7 — The room stays invisible, and this is the one place the artboard is not followed

The artboard puts `ROOM 2` on each day header and `Room 2` in the selection bar. This change does
neither.

`booking-core` decided a patient is never told which room, twice over and in writing: the booking
response *"carries no room — the server assigned one and a patient does not need to know which"*, and
`AvailabilitySlotResponse.resourceId` is documented as *"an explanation, not a reservation… do NOT send
this back expecting it to be honoured."* Promoting the room to a day-level headline would also be
actively wrong: a day's slots are not served by one room, so `ROOM 2` on a day header states something
the solver does not guarantee.

*Confirmed by the maintainer before this was drafted.* Recorded here rather than silently dropped,
because a reader comparing the artboard to the screen will notice, and "we followed the design except
where we didn't" is only acceptable if the exception is written down.

### D8 — Selection is a step, single, and shared by both surfaces

Clicking a slot selects it. Selecting another deselects the first. A summary bar states what is chosen —
professional, kind of visit, duration, time in clinic wall clock — and carries the only control that
leaves the page.

*Why a step at all.* Today a click navigates. That is fine for a patient who has already decided and bad
for one who is comparing Tuesday against Wednesday, which is most of them. It also makes the taken-state
recovery gentler: a refusal returns to a page that still knows what was being attempted.

*Single selection, and the deselect behaviour is the point.* Multi-select would imply booking several,
which no endpoint accepts. Requiring an explicit deselect before choosing another would be a mode a
patient has to learn. Picking another slot simply moving the selection is the behaviour of a radio
group, which is what this is — and it is stated as a requirement so nobody implements a toggle.

*Both surfaces, because `SlotGrid` is already shared.* `booking-lifecycle` extracted it precisely so P6
would not fork P2's grid, and a selection model in only one of them would fork it after all. P6 is also
where comparison matters most: a patient rescheduling is by definition dissatisfied with a time.

*The URL contract is unchanged.* P2 still carries the whole search in the query string, and still hands
P3 `type`, `professional`, `start`, `end`, `search`. Selection is component state, not URL state — a
selected-but-uncommitted slot is not worth restoring, and putting it in the address would make the back
button from P3 ambiguous.

### D9 — Each day scrolls inside itself past two rows

A day's slot grid is capped at **two rows** and scrolls internally beyond that. Added after the first
look at the running screen, which is what the cap is a response to.

*The problem it solves is not tidiness.* A wide window on a working professional produces days of
fifteen or twenty slots. Stacked, the first busy day pushes every later day off the screen — so the
thing a patient is actually scanning for, *the shape of the week*, is destroyed by whichever day
happens to be busiest. Capping each day keeps every day reachable by scrolling the page once, and
turns "this one has a lot" into a scrollbar rather than a wall.

*Why the tile height had to become fixed.* The cap is only honest if two rows is an exact number.
A content-sized tile is 54px or 72px depending on whether a professional's name is shown, and taller
again if a time wraps — and a `max-height` guessed against that shows a **sliver of a third row**,
which reads as a rendering fault rather than as a scroll region. So the tile carries an explicit
height, the two heights are declared as constants beside the cap that consumes them, and the time is
`whitespace-nowrap` so a fall-back day's appended offset cannot silently break the arithmetic.

*Accessibility.* No `tabIndex` on the scroll region: every child is a focusable button, so keyboard
users reach the hidden rows by tabbing and the browser scrolls to them. Adding one would insert a
redundant tab stop before every day. The scroll box carries `p-1` with a matching negative margin,
because `overflow-y-auto` would otherwise clip the `outline-offset-2` focus ring on the first and
last rows — a focus indicator that is present and invisible is the same as one that is absent.

*Alternatives.* (a) Cap the whole results area and scroll all days together — restores exactly the
problem it was meant to fix, since one long day still buries the rest. (b) A "show more" toggle per
day — a click to reveal what a scroll reveals for free, and a new state on a screen that already has
five. (c) Leave it — which is what the first build did, and which is why this decision exists.

**Revisit trigger:** two rows is a judgement made against the seeded clinic at desktop width. If the
validation run finds two rows too mean on a wide screen or still too tall on a phone, `MAX_ROWS` is
one constant.

### D10 — The selection bar stands off the bottom edge

`sticky bottom-4`, not `bottom-0`.

Flush against the viewport edge, the bar read as welded to the window rather than floating above the
page — and it clipped its own shadow, which is the single elevation level this design system grants a
floating layer. A raised surface with its shadow cut off looks like a defect, not a raised surface.

Design Open Question 2 asked whether the bar should be sticky at all. It stays sticky, and this is
half an answer to that question: the remaining half — whether sticky is right on a phone, where the
bar costs vertical space that matters more — is still for the validation run.

### D11 — A refusal speaks in its own code's words, with no fixed preamble

The alert that greets a patient returning from a refused confirmation renders **only** the translated
message for the code. The fixed "that time is no longer free." above it is deleted.

*Found in ordinary use, not by review.* A patient with several appointments picked a slot, continued,
confirmed, and got *"Esse horário não está mais livre. Você já tem uma consulta nesse horário."* —
two sentences that contradict each other. The refusal was `booking.patient_busy`: the slot **is**
free, and the patient simply cannot be in two places at once.

*Why this is more than a wording slip.* `booking-core` went to real trouble to make refusals name
their own cause — splitting `slot_blocked` from `slot_taken` precisely because *"a patient told
'someone was faster' when their professional had declared themselves unavailable goes looking for a
race that did not happen."* Prepending a fixed race-flavoured title above every one of those codes
undid that work at the last step, for every cause except the one it describes.

*Why deletion rather than a per-code title.* Every `booking.*` entry in the catalogue is already a
complete sentence that states the fact and its consequence — which is exactly what the design
system asks copy to do, and it also forbids the "Success!"-style preamble this had become. A
conditional title would be a second place for the same meaning to drift from the catalogue.

### D12 — Availability offers slots the asking patient is already busy in, and this change does not fix it

Recorded because it was hit during this change's own review, and because the temptation to fix it
here should be refused explicitly rather than quietly.

The solver answers *"when is this professional free"* and is deliberately blind to who is asking —
`booking-core`'s reasoning was that folding a patient's own appointments into it *"would make an
availability read depend on who is asking."* So I6 is enforced only at write time, and a patient who
already has a full diary is shown times they cannot take, discovers it two clicks later, and — since
only the single slot they tried is withdrawn from the list — can walk straight into it again.

**Not fixed here, on the same grounds as D6.** It needs the availability read to become
caller-dependent, or the response to carry the patient's own busy set, or the client to subtract it
from a second request. All three are API-shaped, all three have a cost F8 has just been measured
against, and all three deserve to be argued on their own rather than smuggled in under a restyle.

**What this change does do** is stop the refusal lying about its cause when it happens (D11).

## Risks / Trade-offs

- **This change cannot be proven by its tests.** Nothing here alters behaviour, so a green suite says
  only that nothing broke. → The validation guide carries the weight, and it asks for opinions rather
  than passes. The specific failure to avoid is `booking-core`'s: its check 2 asked exactly this
  question and its Outcome records that nobody answered.

- **The forward-claim in the trust panel** (D5) is a sentence about a capability that does not exist. →
  Bounded by context: a portfolio deployment with no connected calendars, an owner (change 7), and three
  places recording it. **Revisit trigger:** a real clinic, or change 7 leaving the plan.

- **`primary-subtle` is corrected on this surface and not globally** (D2). → The DS rule is now true
  where it matters and still false in `packages/shared`. Named as partial rather than claimed as done.

- **The selection step adds a click** to the fastest path — a patient who knows exactly what they want
  now taps twice. → Accepted, and it is the trade the artboard makes deliberately: the selection bar
  restates the choice before committing, which is worth more on a medical appointment than one saved
  tap. Worth an opinion in validation.

- **Two-column on a narrow laptop** could squeeze the slot grid into two tiles per row and look worse
  than today's full-width. → The breakpoint is a judgement that needs a real browser at a real width,
  not a media query chosen from a spec. Explicitly in the validation guide.

- **Regression risk concentrates in `SlotGrid`**, which now serves P2 and P6 with two selection states. →
  It is one component with one new prop-pair; the existing DST-disambiguation logic is untouched and is
  the part that would be expensive to get wrong.

## Migration Plan

None. No schema, no configuration, no data, no API version. Deploying this is deploying two SPA bundles.

Rollback is reverting the commit; nothing persists that a previous build cannot read, because nothing
persists at all.

## Open Questions

*Answered by the validation run of 2026-08-25 where marked; see `validation.md`'s Outcome for the
wording the answers were given in.*

1. **Does the trust panel belong under the search on mobile, where it lands between the controls and the
   results?** — **STILL OPEN.** The validation run answered check 9's width question and did not reach
   this one. It is now the more interesting half: the column collapses at `lg` (1024 px), so the stacked
   layout with the trust panel between the controls and the results is what a large share of *laptop*
   users see, not only phones. It may be exactly right — the promise stated before the answer — or an
   obstacle a thumb scrolls past every search.

2. **Should the selection bar be sticky?** — **CLOSED: yes, as shipped.** Sticky on desktop, inline on
   mobile, confirmed against a long day list; the run's verdict was that the implemented version is good
   enough. D10 had already argued the desktop half from the clipped `shadow-float`; the phone half — where
   the bar costs vertical space that matters more — has now been looked at and the trade accepted.

3. **Is `COMPUTED` worth its own small change?** (D6.) — **STILL OPEN.** One field, and it makes the
   never-cached posture visible. Untouched by the run. If the answer is yes, it should be argued on its
   own, not appended here.

4. **Does the keyboard path actually work?** — **CLOSED 2026-08-25: yes.** Opened when the validation run
   did not reach check 12 and task 8.4 archived unchecked; closed shortly after by the browser session it
   asked for. The keyboard path through P2 was walked end to end and the accessibility audit came back
   clean. What the code was written to earn on this surface — `aria-pressed` on a chosen slot, a real
   `<fieldset>` for the radio group, native radios so arrow-key movement comes from the platform — has
   now been seen rather than only argued, and task 3.2's *"Verified in the browser at 8.4"* is discharged
   with it. The question is kept here rather than deleted: it was a real gap for the length of an
   archive, and the design system's rule that state is never carried by colour alone (cited in D2) is the
   one this change was most at risk of breaking while restoring two others.
