## Why

P2 is the flagship. `06 §Z2` makes it the public, recruiter-facing expression of the project's whole
thesis, and `booking-core`'s own validation guide asked a human to say *"whether this screen looks like
the showcase, or like a form with a list under it."* That check came back with **no recorded opinion** —
its Outcome says so plainly.

Meanwhile the answer was already sitting in the repository. The Claude Design canvas has carried a P2
artboard — `Find a time`, the specific-versus-any professional choice, the trust panel, a selected slot
and a commit bar — since **2026-08-21**, three days before P2 was built. `booking-core`'s task 10.1
("extend the canvas with P2/P3/P4 artboards") is the single unchecked box in that change, and 10.2
("treat the canvas as **input**") was checked. In practice the canvas was archived rather than used, and
the screen was built from the requirements instead.

So this is **drift, not a missing design**. The palette is not the problem: the Consultório tokens are
already wired into `theme.css` in full. What drifted is structure, hierarchy, and two of the design
system's own stated rules.

## What Changes

**The layout inverts.** Today the search is a card stacked *above* the results, so choosing a different
window pushes the answer down the page. The design puts the search in a persistent left column beside
the results — the shape of a tool you adjust while looking at what it produced, rather than a form you
submit.

**Two design-system rules the portal currently breaks, and the staff console does not:**

- **`#DEF4F0` — `primary-subtle` — "fills nothing but a bookable slot"** (the DS readme's words). Today
  it fills the P4 success card, the `success` Alert and the `active` Badge, and **not one slot**. Slots
  are plain `surface`. The colour reserved for the single most important affordance in the product is
  used everywhere except there.
- **"Numbers are literal and always mono"**, `tnum` on. The staff console honours this on every
  duration, time and buffer. The booking flow uses it **nowhere** — including on slot times, which are
  the most number-like thing a patient ever reads here.

Both are the reason the built screen reads as a list of buttons and the artboard reads as a schedule.

**Selection becomes a step.** Clicking a slot currently navigates straight to P3. The design selects in
place and commits with an explicit **CONTINUE**, so a patient can compare two times before leaving the
page. Selection is **single**: choosing another slot deselects the first. A summary bar names what is
selected — professional, kind of visit, duration, time — and carries the only button that leaves.

**P6 gets the same treatment**, because it already shares `SlotGrid` with P2 and because a reschedule is
exactly the case where comparing before committing matters most. Its left column carries what is being
moved and the date window instead of a search.

**The professional choice becomes explicit.** Today "any professional" is the first option of a select,
which makes the primary booking mode (`02 §4`) look like an afterthought at the top of a list. The
design makes it a labelled choice of two — *a specific professional* / *any professional of the
specialty* — with the select subordinate to the first.

**A trust panel under the search**: *Checked for every time — the professional's working hours · blocks
from their external calendar · a free room of the required type.* This is the tri-constraint promise
stated where a patient can see it, and it is the single element that most makes the screen argue for
itself.

**One forward-claim, made deliberately rather than discovered.** "Blocks from their external calendar"
describes `calendar-inbound` (change 7); every block today is internal. The wireframe's wording is kept
**by decision**, and the decision — with the change that makes it true — is recorded in `design.md`
rather than left for a reader to trip over. See Impact.

## Capabilities

### New Capabilities

None. This change alters how an existing capability presents itself.

### Modified Capabilities

- `booking`: two requirements describe the patient portal's surfaces, and both change. *"The patient
  portal lets a patient search real availability and book"* gains the selection step (a chosen slot is
  shown as chosen, only one at a time, and committing is a separate act), the explicit
  specific-versus-any choice, and the statement of what was checked for every offered time. *"The
  patient portal lets a patient see and change their appointments"* gains the same selection step on
  the reschedule surface. **No behaviour behind the API changes** — no new endpoint, no new field, no
  new refusal, no change to what is offered or to what booking accepts.

`availability` is not modified. `identity-session` is not modified.

## Impact

| Area | Change |
|---|---|
| `apps/api` | **Nothing.** No endpoint, no contract, no migration, no new code |
| `packages/shared` | One new primitive — a radio group — because the specific/any choice is the one control the design needs and the library does not have. i18n keys for the trust panel, the selection bar and the new labels |
| `apps/patient-portal` | P2 relaid out; `SlotGrid` gains a selected state and a `BOOK` affordance; a selection bar shared by P2 and P6; P6 relaid out to match. P3, P4, P5, P7 untouched except where a token fix reaches them |
| Design tokens | None added. `primary-subtle` and `font-mono` start being used where the design system already said they belong |
| `docs/06-ui-surfaces.md` | P2's and P6's descriptions gain the selection step |
| Dependencies | None |
| **Deliberately not done** | The wireframe's `COMPUTED 12:04:51` and `3 TIMES HIDDEN · 2 EXTERNAL BLOCK · 1 NO ROOM` both need data the availability response does not carry — a computed-at instant, and the solver's refusal taxonomy surfaced on the read path. **Excluded by decision**, not oversight; `Explain` already computes the second one and throws it away, which makes it a genuinely cheap future change and a real one. See design |
| **Deliberately not done** | The wireframe's `ROOM 2` on day headers and in the selection bar. `booking-core` decided a patient is never told which room, and that decision stands — so the design is followed everywhere except here, and the exception is named |
| Not touched | Any staff surface; S1/S4/S5 and `Professional.fullName` (`booking-desk`); the availability response shape; the P2→P3 URL contract, which keeps carrying the search so a reload and the back button still work |
