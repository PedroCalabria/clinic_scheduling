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

- **Run on:** _(date)_
- **Run by:** _(who, and against what)_
- **Result:** _(pass / pass with findings / fail)_
- **Notes:**

### Opinions, not ticks — this section is the deliverable

A presentation change validated with ticks has not been validated. The five that need words:

- **Check 1** — showcase, or a form with a list under it? (the question `booking-core` left unanswered)
- **Check 3** — does the selection step earn its extra click?
- **Check 6** — does seeing both ends of a reschedule help, or crowd?
- **Check 9** — at what width does the two-column layout stop being comfortable?
- **Check 10** — sticky or inline? (design Open Question 2)

### What was NOT examined, stated plainly

_(A blank or vague Outcome is indistinguishable from an overlooked one.)_

- Whether the P2 artboard was actually opened beside the running app, or only remembered.
- The three deliberately-absent artboard elements — confirm they were checked as absent rather than
  simply unnoticed.
- Anything below the API line: unchanged by this change, and the automated tiers should be green in
  exactly the numbers they were before. **State those numbers here**, because "unchanged" is a claim.
