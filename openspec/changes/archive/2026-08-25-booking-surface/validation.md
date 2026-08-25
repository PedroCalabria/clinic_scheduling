# Validation guide — booking-surface

Manual checks a human runs against the locally-running app (`00-context.md` §9).

**This guide matters more than any before it, and the reason is uncomfortable.** Every previous change
could lean on its automated tiers: an invariant held, a constraint refused, a race resolved. This change
alters **nothing below the API line**. A green suite proves only that nothing broke. Whether the work
succeeded is a question no test in this repository can answer, and this guide is the entire answer.

**So a tick is not a result here.** `booking-core`'s guide asked, in its check 2, whether P2 *"looks
like the showcase `06 §Z2` promises, or like a form with a list under it."* Its Outcome records that the
question came back unanswered — and that unanswered question is why this change exists. Repeating the
outcome would waste the change.

**What to compare against.** Open the P2 artboard in `design/Clinic Scheduler Wireframes.dc.html` beside
the running app. Three of its elements are deliberately absent, and their absence is not a defect:

| Absent | Why | Where argued |
|---|---|---|
| `ROOM 2` on day headers and in the selection bar | A patient is never told which room | design D7 |
| `COMPUTED 12:04:51` | Needs a field the availability response does not carry | design D6 |
| `3 TIMES HIDDEN · 2 EXTERNAL BLOCK · 1 NO ROOM` | Same, and blocked on change 7 besides | design D6 |

## Setup

```bash
cp .env.example .env
# fill in the Google client id and secret — see docs/08-google-setup.md
docker compose -f infra/docker-compose.yml --env-file .env up -d --build
```

- Patient portal at `http://localhost:8080/`.
- Set `Clinic__SeedDevelopmentData=true`. The seeded clinic carries two professionals, three appointment
  types, three rooms, blocked periods, and appointments both inside and outside the cancellation cutoff.
- **Check `Clinic__Timezone` is your intended zone before starting.** `booking-lifecycle`'s check 11
  asks for it to be temporarily set to a DST-observing zone and restored afterwards; a stack left on
  `America/New_York` will make every time on these screens look wrong for reasons that have nothing to
  do with this change.
- One real Google patient account. A patient with **no** appointments and one with several are both
  worth looking at — the seeded appointments belong to the seeded patient, not to your Google account.

---

## 1 — The judgement check, and it comes first on purpose

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Search something real. Then stop and read the screen as a stranger — someone deciding
whether this clinic looks competent.

**Expected.** An opinion, written down. Not "it renders."

**This is the check the change exists for.** `06 §Z2` calls P2 the visible form of the project's whole
thesis. The specific question, unchanged from `booking-core`'s check 2 so the two answers can be
compared: **does this look like the showcase, or like a form with a list under it?** If the honest answer
is still the second, say so — that is a finding worth more than a pass, and it is recoverable.

Worth naming separately if you notice it: whether the **trust panel** — the three things checked for
every time — does any work, or reads as decoration. It is the single element most meant to make the
screen argue for itself.

## 2 — The grid aligns

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Look at a day with several slots. Look down the column of times.

**Expected.** The times line up. `08:45` and `11:00` occupy the same width, because the figures are
tabular.

**Why this small thing gets its own check.** It is the cheapest change in the whole set and probably the
most visible: proportional digits make a column of times visibly ragged, and a ragged column cannot be
scanned. The design system has said *"numbers are always mono"* since before any of this was built; the
staff console obeyed it and the portal did not. If the grid still looks unsettled, that rule is not
actually being applied somewhere.

## 3 — Choosing, and changing your mind

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Choose a slot. Then choose a different one. Then a third. Then continue.

**Expected.** Each choice replaces the last — exactly one is ever chosen, and you never have to clear
one before picking another. The chosen slot is obvious at a glance. A summary names what you have chosen
— professional, kind of visit, how long, when — and the control that proceeds is the only way off the
page. Before you choose anything, that control is unavailable.

**Judgement, alongside behaviour.** Choosing now takes two actions where it used to take one. Say whether
the summary earns that: does restating the choice before committing feel like care, or like an extra
click between you and an appointment? Either answer is useful, and the second one is a real finding.

## 4 — Adjusting the search while looking at the answer

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** With results on screen, change the date window. Then switch between a named professional and
any professional. Watch what happens to the results while you do it.

**Expected.** The controls and the results stay visible together. You are not scrolling back and forth
between the thing you changed and the thing that changed.

**This is the whole argument for the layout** (design D1). If you find yourself scrolling anyway, the
layout has not done its job and that is the finding.

## 5 — Any professional stops being an afterthought

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Look at how the professional choice is presented, before touching it.

**Expected.** Two labelled options — a specific professional, or any professional of the specialty —
with the list of names belonging to the first. Not one entry at the top of a dropdown.

**Also.** Reach and change it **using only the keyboard**: the group should be one tab stop, with arrow
keys moving between the two options. That is the WAI-ARIA radio pattern and the main reason this is a
shared primitive rather than two loose inputs.

## 6 — Rescheduling gets the same treatment

| | |
|---|---|
| Role | Patient |
| Route | `/appointments` → `/appointments/:id/reschedule` |

**Action.** Reschedule something. Choose a time, change your mind, choose another, then commit.

**Expected.** It behaves exactly like P2 — single selection, a summary, an explicit commit — and the time
being **moved** is on screen beside the times available, so you can see both ends of the change.

**Judgement.** A patient rescheduling is by definition unhappy with a time. Say whether seeing the old
and new together makes that easier, or just makes the screen busier.

## 7 — Nothing behind the screen moved

| | |
|---|---|
| Role | Patient |
| Route | `/book` → `/book/confirm` → `/book/success` |

**Action.** Book something end to end. Then reload the search, use the browser's back button from the
confirmation, and paste the search URL into another window.

**Expected.** Everything behaves as it did before this change. The same search comes back, the URL is
still shareable, and the booking still works.

**This is the regression check.** The change is presentation only; the P2→P3 URL contract was explicitly
not touched. Anything different here is a bug introduced by a restyle, which is the worst kind.

## 8 — The slot just taken

| | |
|---|---|
| Role | Patient, in two windows |
| Route | `/book` → `/book/confirm` |

**Action.** Load the same slot in two windows. Book in one; submit the stale confirmation in the other.

**Expected.** The translated "just taken" message, that slot gone from the list, the search still on
screen — and **nothing left selected** pointing at a slot that no longer exists.

**Why it is here.** The selection step is new state on a screen that already had a recovery path. New
state is exactly what gets forgotten in a recovery path.

## 9 — A narrow laptop, not just a breakpoint

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Resize the window slowly from full width down to a phone, and watch the slot grid.

**Expected.** It stays readable throughout. Below the mobile breakpoint the columns stack with the search
first.

**The specific risk** (design D1). Two columns on a narrow laptop can squeeze the grid to two tiles per
row, which would be **worse than the full-width layout it replaced**. That is a real possible outcome,
and it needs a real browser at a real width — a media query chosen from a spec cannot answer it. Report
the width at which it stops being comfortable.

**Also worth an opinion** (design Open Question 1): once stacked, the trust panel sits between the
controls and the results. Is that the promise stated before the answer, or an obstacle a thumb scrolls
past every time?

## 10 — Sticky or not

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Search a wide window so the day list is long. Choose a slot near the top, then scroll down.

**Expected.** Whatever was implemented, say whether it works. The selection bar pinned to the bottom
keeps the commit reachable and costs vertical space; inline keeps the space and can scroll the choice out
of reach.

**Design Open Question 2 — this check is how it gets answered.** The working answer is sticky on desktop
and inline on mobile; nobody has seen it.

## 11 — Both languages, everywhere new

| | |
|---|---|
| Role | Patient |
| Route | `/book`, `/appointments/:id/reschedule` |

**Action.** Switch pt-BR ↔ en on: the trust panel, the two professional-choice labels, the results
header, a day header, the selection bar with something chosen, and the empty and error states.

**Expected.** Every string changes and no raw key appears. The pt-BR copy uses *horário*, not "slot".

## 12 — Accessibility, at the level this project claims

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Complete a booking using only the keyboard. Then run the browser's accessibility audit.

**Expected.** Focus is visible throughout; the radio group is one tab stop with arrow-key movement; the
chosen slot announces that it is chosen, not merely looking different; the selection bar is reachable.
Nothing WCAG 2.1 AA would fail.

**The new risk this change introduces.** The design system forbids state carried by colour alone, and a
chosen slot is a new state whose most obvious expression is a colour. If a screen reader cannot tell
which slot is chosen, that rule has been broken by the very change that was restoring two others.

## 13 — What the screen claims about external calendars

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Read the trust panel's middle line: *blocks from their external calendar*.

**Expected.** It says exactly that, and **it is not true yet** — every block today is internal, made by
a professional on S3. External blocks arrive with `calendar-inbound` (change 7).

**This check exists to make sure a decision stays a decision.** It was kept deliberately (design D5): a
professional's blocks *are* subtracted, so the line names a source that does not exist rather than a
check that does not happen, and it becomes true when change 7 lands with no edit to this screen.

**What you are confirming is that the reasoning still holds** — that this is a portfolio deployment with
no connected calendars and no clinic being told otherwise. If either of those changes, this line becomes
a false claim rather than a forward one, and it is the first thing that should be reworded.

---

## Outcome

- **Run on:** 2026-08-25
- **Run by:** the maintainer, in a browser against the running app
- **Result:** **passed** — all five judgement checks came back with opinions, and all five were
  favourable. The question `booking-core` left unanswered is now answered.
- **Notes:** one design Open Question is closed by this run and one is left open. The accessibility
  check was outstanding when this record was first written and **was closed shortly after, on the same
  day** — see the head of the gaps section. Everything is recorded below rather than left to be inferred.

### The opinions, which are the deliverable

**Check 1 — showcase, or a form with a list under it? → the showcase.** With the corrections it is
*"more intuitive and organized."* **This is the answer `booking-core`'s check 2 asked for and did not
get**, and getting it is why this change existed. The two records can now be set side by side: 5a asked
and recorded silence; this change asked the same question, unchanged in wording, and recorded yes.

The answer also credits something this guide did not think to ask about. Because selection happens in
place, a patient who mis-clicks *"can change their selection easier than having to go back to the screen
multiple times."* The design argued the selection step as **restatement before committing** (D8, and the
extra-click trade in Risks); the run says recovery from a wrong click is worth as much. That is a benefit
the change delivers and never claimed.

**Check 3 — does the selection step earn its extra click? → yes.** *"It's better now."* Brief, and it
settles the trade `design.md` listed as accepted but unverified: booking now costs two actions where it
cost one, and the summary is judged worth it. That Risks entry stands as a finding rather than a bet.

**Check 6 — do both ends of a reschedule help, or crowd? → they help.** The visibility *"helps the user
know what happened with a previous appointment."* Worth recording that **this question was asked twice**:
`booking-lifecycle`'s check 4 put it first and its Outcome records that no opinion came back, naming it
*"the one that matters most, because `02 §5` rests an argument on it."* That debt is paid one change
later, against a screen that had meanwhile gained P2's selection model.

**Check 9 — at what width does the two-column layout stop being comfortable? → it does not.** Observed:
by roughly **966 px** the layout has already collapsed to a single column, and it is comfortable there.

**D1's stated risk did not materialise, and the reason deserves naming.** The risk was that a persistent
search column squeezes the slot grid to two tiles per row on a narrow laptop — *worse than the full-width
layout it replaced*. It cannot happen, because the column collapses at Tailwind's `lg` (1024 px). The
grid is never asked to be narrow and two-column at the same time.

**A discrepancy, recorded rather than smoothed over.** Task 4.2 describes stacking *"below the `sm`
breakpoint"* and cites the design system's mandate of one column under 768 px. The shipped code stacks at
`lg` — `BookingSearchPage.tsx:250`, `lg:grid-cols-[minmax(0,20rem)_minmax(0,1fr)]`. That is **more
conservative than the task text and still satisfies the DS rule** (under 768 px is single column, as
mandated), so nothing is violated. But the number a reader would carry away from task 4.2 is not the
number in the code, and the gap between them is precisely what made this check pass. The observed ~966 px
sits just under the 1024 px query, consistent with a window width read including its scrollbar.

**Check 10 — sticky or inline? → the implemented version is good enough.** Sticky on desktop, inline on
mobile, seen against a long day list. **Design Open Question 2 is closed.** The half D10 had already
argued from the clipped `shadow-float` now has its other half: the phone, where the bar costs vertical
space that matters more, was looked at and the trade was accepted.

### What was NOT examined, stated plainly

A blank or vague Outcome is indistinguishable from an overlooked one, so the gaps are named.

**Check 12 was closed on 2026-08-25, after the archive commit.** The keyboard path through P2 was walked
end to end and the browser's accessibility audit came back clean. **Task 8.4 is done and design Open
Question 4 is closed.** So the largest hole in this record is filled, and what filled it was the browser
session the bullet below said it needed.

*The bullet is left exactly as it was recorded*, on `booking-lifecycle`'s precedent for a debt closed
after its Outcome was written: what it says about **this change** stays true. It did archive with 8.4
open — commit `ce999d4` folded the delta into the living spec at 46 of 47 tasks — and a reader comparing
the archive against the spec should find that, not a tidied version of it.

- **Check 12 was not run. The keyboard path and the accessibility audit are unexamined, task 8.4 stays
  unchecked, and this change archives with it open.** This is the largest hole in the record. `06 §2`
  names elderly users as part of this audience and sets AA for this surface specifically — and this
  change introduced a new state, a chosen slot, whose most obvious expression is a colour, on a surface
  whose design system forbids state carried by colour alone. The code was written to answer that:
  `aria-pressed` (task 5.3), a real `<fieldset>` rather than a `Field`-wrapped fake (task 3.1), and
  native radios so arrow-key movement comes from the platform (task 3.2). But **written to answer it is
  not the same as seen to answer it**, and no screen reader has been near this screen. Task 3.2 says in
  as many words *"Verified in the browser at 8.4"*; 8.4 did not happen, so that verification is
  outstanding too. It is a browser session, not a code change, and it does not need a change to carry it.
- **Design Open Question 1 is still open, and this run made it more pressing rather than less.** Check 9
  carried a second question: once stacked, the trust panel sits between the controls and the results — is
  that the promise stated before the answer, or an obstacle a thumb scrolls past on every search? The
  width half was answered and this half was not. It matters more now that the stack point is known to be
  1024 px: the stacked layout is what a large share of laptop users see, not only phones.
- **The automated tiers were not re-run for this record.** Task 8.1 asserts 248 domain unit and 292
  integration, unchanged. Those numbers are **not confirmed here.** What was re-run green on 2026-08-25
  is `openspec validate booking-surface --strict`, `pnpm typecheck` (3 of 3 projects), `pnpm check:i18n`
  (337 keys consistent, 380 references resolving) and `pnpm check:readme`. The .NET suites were not run.
  The claim rests on this change touching nothing below the API line — structurally true, and not the
  same thing as measured.
- **Not stated either way:** whether the P2 artboard was opened beside the running app or worked from
  memory, and whether the three deliberately-absent artboard elements (D6, D7) were confirmed absent
  rather than simply unnoticed.
