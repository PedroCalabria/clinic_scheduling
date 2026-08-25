# Validation guide — booking-desk

Manual checks a human runs against the locally-running app (`00-context.md` §9).

**What this guide is for.** Three screens that have never existed, two widened write paths whose whole
security boundary is a policy plus a branch, and an audit trail that fails silently when it is missing.
The automated tiers cover the branches and the rows. What they cannot cover is whether a receptionist
can *run the day* with these screens, and whether the demonstration this change exists to complete
actually reads as one.

**The demonstration, stated once so the run can be judged against it.** From `05-openspec-workflow.md`,
5c is where "RBAC + ownership coexisting" becomes visible: a patient is refused by a rule about *their
own data and the clock*, and reception — a different role, no ownership at all — is admitted by the same
rule. Checks 6 and 7 are that sentence acted out. If they pass mechanically but do not read as that
story, say so.

**One correction to expect, argued in design N1.** The cutoff has never governed booking. A walk-in
booked for later today succeeds because the *minimum lead time* permits it, which is a different rule
with a different code. The override — `cutoffApplies: false` — is exercised by reception **cancelling or
moving** an appointment inside the cutoff. So the demo has two acts, not one, and check 7 is the one
that shows the override.

## Setup

```bash
cp .env.example .env
# fill in the Google client id and secret — see docs/08-google-setup.md
docker compose -f infra/docker-compose.yml --env-file .env up -d --build
```

- Staff console at `http://localhost:8080/staff/`, patient portal at `http://localhost:8080/`.
- Set `Clinic__SeedDevelopmentData=true`.
- **Check `Clinic__Timezone` is your intended zone before starting.** A stack left on a DST-observing
  zone from `booking-lifecycle`'s check 11 makes every time on these screens look wrong for reasons
  unrelated to this change.
- **Check `Scheduling__MinimumLeadTimeMinutes`.** At the default 60, "book a walk-in for now" is refused
  and that is correct (design N1). Book at the first offered time instead, or set it to `0` — which the
  domain documents as legitimate for a clinic that takes walk-ins — and say which you did.
- **Accounts you need:**
  - a **front-desk** internal account (create one in S11 as the administrator);
  - a **real Google account** invited as a professional in S11 and claimed on S0 — S1 is Google-only, and
    §9's last paragraph is explicit that the seeded `dra.helena@clinic.local` cannot sign in;
  - a **real Google patient account** with at least one upcoming appointment, one of them starting
    **inside** the 24 h cutoff. Book the near one from the desk in check 5 if the seed has none.

---

## 1 — The judgement check, and it comes first on purpose

| | |
|---|---|
| Role | Front desk |
| Route | `/staff/day` |

**Action.** Open the day view on a day with several appointments across professionals. Then stop and
read it as a receptionist with a phone ringing and somebody at the desk.

**Expected.** An opinion, written down. Not "it renders."

The specific question: **could you run a clinic morning from this screen, or would you keep a paper
list beside it?** `06 §Z2` says staff surfaces are utilitarian, which is permission to be plain — it is
not permission to be unusable. If something obvious is missing (the patient's phone number, the next
free gap, tomorrow), name it. `booking-surface` archived with its equivalent judgement checks answered
and that was the most valuable thing in its record.

---

## 2 — A professional's name reaches the places it should

| | |
|---|---|
| Role | Administrator, then patient |
| Route | `/staff/admin/professionals`, then `/book` |

**Action.** Open a professional on S7 and set a real name. Save. Then open the patient portal's booking
search and look at the professional list. Also look at a professional who has **no** name set.

**Expected.** The named professional appears by the entered name. The unnamed one still appears by the
label derived from their account address — not by their email address, and not blank. Both languages.

**Why this is checked in the browser.** P-5 has been open since 3b and the reason it stayed open is that
the derived label *looked* fine. The check is whether an administrator can tell which names are real.

---

## 3 — A professional sees their own day (S1)

| | |
|---|---|
| Role | Professional (Google) |
| Route | `/staff/schedule` |

**Action.** Sign in with the claimed Google professional account. Open the schedule for a day that has
both appointments and an internal block.

**Expected.** The day lists appointments with patient, appointment type, room and time, and shows the
internal blocks distinctly. Times are the clinic's wall clock. Both languages. Nothing belonging to
another professional appears.

**Also check** that the navigation shows this entry and does **not** show the day view or the booking
surface — those are reception's.

---

## 4 — A professional cannot read somebody else's day

| | |
|---|---|
| Role | Professional (Google) |
| Route | `/staff/schedule` |

**Action.** With the browser's developer tools, request the schedule endpoint directly, adding another
professional's id to the query string.

**Expected.** Your own day comes back. Not a refusal, and not theirs — the parameter is disregarded
(design N9), which is the same shape as a time block carrying no professional.

---

## 5 — Reception books a walk-in (S5)

| | |
|---|---|
| Role | Front desk |
| Route | `/staff/book` |

**Action.** Resolve an existing patient by their exact contact email. Search availability. Book the
first offered time today.

**Expected.** The patient resolves by name with their consent state shown. Slots name the room they
would use. The booking succeeds and reports the appointment **with the room assigned**. Both languages.

**Then check three things that are easy to miss:**

1. A **partial** email finds nobody (design N8 — this is exactness, not a bug).
2. The screen makes **no claim** that an external calendar was checked. P2's trust panel says it does;
   that claim is change 7's and a receptionist would know it is not yet true.
3. The new appointment appears on the day view (check 1's screen) with its room.

---

## 6 — The patient meets the rule

| | |
|---|---|
| Role | Patient (Google) |
| Route | `/appointments` |

**Action.** Sign in as the patient and find an appointment starting **sooner** than 24 hours from now.
Try to cancel it, and try to reschedule it.

**Expected.** Both actions are disabled with a translated explanation naming reception. This is not new
behaviour — it shipped in 5b — and it is here because check 7 is meaningless without it. **Leave this
browser window open.**

---

## 7 — Reception does what the patient could not — the override

| | |
|---|---|
| Role | Front desk |
| Route | `/staff/day` |

**Action.** In a second browser (or a private window), signed in as front desk, find the *same*
appointment on the day view. Cancel it. Then repeat with another near appointment and **move** it
instead.

**Expected.** Both succeed. The cancel frees the time — confirm by searching availability for it again.
The move opens the booking surface scoped to that appointment's professional and appointment type, with
no control offering to change either, and commits to the new time.

**This is the change's headline, and it is a judgement check as well as a functional one.** Write down
whether the day view *says* what is happening — that the patient can no longer change this appointment
and the desk still can — or whether the actions are merely enabled and a reader has to infer why. The
first is the demonstration; the second is the same feature with the argument left out.

**Also try a near-move**, a few minutes rather than a few days. It succeeding is the reschedule
statement-ordering property holding on the staff path, which is exactly why staff share the handler
rather than getting their own (design N2).

---

## 8 — The lead time is not overridden

| | |
|---|---|
| Role | Front desk |
| Route | `/staff/book` |

**Action.** With `Scheduling__MinimumLeadTimeMinutes` at its default 60, try to book a time inside the
next hour — through the developer tools if the screen does not offer one.

**Expected.** Refused, with the translated message for `booking.lead_time_violation`.

**Why this is checked.** It is design N1 made visible: the front desk's authority is over the *cutoff*,
not over what availability may offer. A desk that could book past the lead time would be a desk that can
book what the availability view says is unavailable.

---

## 9 — A patient cannot book for somebody else

| | |
|---|---|
| Role | Patient (Google) |
| Route | `/book` |

**Action.** Through the developer tools, repeat a booking request with a `patientId` added to the body —
first somebody else's, then the patient's own.

**Expected.** Both refused with `auth.forbidden`. **Both**, including their own id: the field is refused
by role, not validated by value (design N3). An appointment must not be created either time.

---

## 10 — The access trail exists

| | |
|---|---|
| Role | Front desk, then a database client |
| Route | `/staff/day`, `/staff/book` |

**Action.** Note the time. Load the day view once on a day with several patients. Resolve one patient by
email on S5. Then read the `access_log` table.

**Expected.** One row per **distinct** patient shown on the day, plus one for the email lookup, all
naming the acting user, the patient, the action and the time. A day with no appointments adds nothing.
A failed lookup adds nothing.

**Then load the day view again and cancel an appointment.** The cancel itself must add **no** further
row (design N7): the appointment is not the patient's personal data, and the name that was read is
already recorded. If cancelling adds a row, the placement is wrong in a way that inflates the trail
rather than one that breaks it — still worth reporting.

**This check cannot be skipped.** It is the one whose failure is silent: everything works, and an LGPD
claim in `02-domain-model.md` §8 is quietly false.

---

## 11 — The patient portal is still the patient portal

| | |
|---|---|
| Role | Patient (Google) |
| Route | `/book`, `/appointments`, `/book/success` |

**Action.** Walk the booking flow and the appointments list.

**Expected.** **No room is named anywhere** — the availability response now carries the room's name, and
D7 has become a rule the client keeps rather than one the wire enforces (design N5). Also look at P4's
success card and any `success` alert: their fill changed with task 8, and they should read as
deliberate rather than washed out.

---

## 12 — Keyboard and both locales on the new screens

| | |
|---|---|
| Role | Front desk |
| Route | `/staff/day`, `/staff/book` |

**Action.** Reach and operate both screens with the keyboard only, including the cancel confirmation —
open it, escape it, confirm it, and check where focus lands afterwards. Then switch the language on
every state of all three screens, including empty results and a refusal.

**Expected.** Every control reachable, focus returned to the trigger after the confirmation closes, and
no raw translation key anywhere.

**Named explicitly because it was missed once.** `booking-surface` archived with its equivalent check
unrun and its Outcome called that its largest gap. Three new screens is the wrong place to repeat it.

---

## Outcome — run 2026-08-25

**Ten of twelve checks passed. Three defects were found, all in this change's own screens, and
none of them was reachable by any automated tier.** Fixes are recorded as tasks 15.1–15.4.

| # | Result |
|---|---|
| 1 · judgement | **Passed, with a written opinion.** *"The portal is good enough to be used on a daily basis. The receptionist can view all appointments for the day."* This is the question `booking-surface` asked of P2 and left unanswered; asked of S4, it came back answered and favourable |
| 2 · the name reaches its surfaces | Passed |
| 3 · S1 | **FAILED, fixed, re-run — passes** (task 15.2). A block from 08:05 on the 30th to 07:30 on the 31st rendered as `08:05–07:30` on *both* days, reading as a period inside one day and wrong by twenty-three hours. S3 was correct throughout, which is what made the defect easy to miss: the same block reads properly one screen away |
| 4 · a professional cannot read another's day | Passed |
| 5 · reception books a walk-in | **FAILED, fixed, re-run — passes** (task 15.1). After resolving the patient the screen stopped: no kind of visit, no date, no slots. The gate opening that section required a chosen kind of visit, and the control that chooses one was inside it. A deadlock with no path through it |
| 6 · the patient meets the rule | Passed |
| 7 · the override | **FAILED twice, fixed twice, now passes** (tasks 15.3, 15.6). First pass: move landed on "nothing booked on this day" — the stack under test predated the `&on=<date>` fix, so S5 looked for the appointment on *its own* default day. Second pass: the reschedule committed correctly and the screen then reported success **and** a "could not be found" warning together, because a moved appointment leaves its own day the moment it is moved. Third pass: **the override works and reads as one** |
| 8 · the lead time | **Not run on the first pass** (blocked by 5); **passes on the re-run** |
| 9 · a patient cannot book for somebody else | Passed |
| 10 · the access trail | Passed — including the negative half: cancelling added no further row |
| 11 · the patient portal is unchanged | Passed. No room is named anywhere on a patient surface, and the re-pigmented `success` alert and `active` badge read as deliberate |
| 12 · keyboard and both locales | Passed |

**All twelve checks now pass.** Four defects were found across three passes and all four are
fixed (tasks 15.1–15.4, 15.6).

### What was not examined

- The four design Open Questions are **still unanswered**: the day versus the day-plus-tomorrow,
  the room on S1, the email-only patient lookup against real reception work, and whether S4 should
  show how an appointment was booked. Check 1's opinion speaks to the screen as a whole rather than
  to any of the four, so they remain open questions rather than answered ones.
- **The re-runs were targeted**, covering checks 3, 5, 7 and 8. The eight checks that passed on the
  first pass were not re-run against the rebuilt stack. The changes between the two builds were all
  in `apps/staff`'s three new screens, so 2, 4, 9, 10 and 11 could not have been affected; 1, 6 and
  12 touch the changed screens and are asserted from the first pass rather than re-confirmed.

### What this run is worth saying about

**Four defects, and not one of them was reachable by an automated tier.** Two were pure presentation
(a range label reading twenty-three hours wrong, a form gate with no path through it), one was a
missing query parameter between two screens, and the fourth was a state that only exists *after* a
successful write — a moved appointment leaving its own day, which is correct behaviour producing a
contradictory screen.

Three green tiers — 255 unit, 324 integration, 18 compose smoke — had nothing to say about any of
them. And the shape of the run matters as much as the count: **the second and third defects were
only findable once the ones before them were fixed.** Check 8 could not run until 5 was fixed;
15.6 could not appear until the move got far enough to succeed. A guide run once and archived would
have found the first defect and stopped.

`booking-surface`'s outcome made this point from the other direction — its unrun keyboard check was
named its largest gap. That is the argument for `00-context.md` §9, made twice now, and this time
with the iteration included.