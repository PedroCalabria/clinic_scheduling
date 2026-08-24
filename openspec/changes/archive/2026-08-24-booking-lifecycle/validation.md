# Validation guide — booking-lifecycle

Manual checks a human runs against the locally-running app (`00-context.md` §9). Everything here is
deliberately **outside** what the test suite can assert: browser interaction, both-locale rendering,
visual and interaction judgement, and what only exists once a request has crossed Caddy into a real
container.

**Revised after implementation, as 5a's guide was.** Four things moved. The draft assumed the seeded
data would need arranging by hand to see a locked appointment — the seed now carries one **inside** the
cutoff, placed relative to *now*, so check 4 works on a fresh stack with no setup. P6 turned out to land
back on the list rather than on its own success screen, so check 6 asks about that directly. A check on
the already-cancelled case was added because implementation gave it its own error code
(`booking.appointment_not_changeable`) and that code is **flagged for review** — a human opinion on its
wording is now worth having. And check 12 gained a sentence: the calendar seam is confirmed by looking,
not assumed.

**One thing found during implementation that no manual check can see, recorded so it is not re-derived.**
The reschedule's statement ordering — the decision this whole increment was most careful about — turned
out to be invisible from the handler's own tests: reversing the two statements left the entire suite
green, because EF orders its command batch itself. The rule is now asserted against raw SQL
(`RescheduleOrderingTests`) and the handler pins the order explicitly. Nothing below tests it, and
nothing below can.

**What this guide is really for.** The automated tiers can prove that a cancelled slot comes back and
that a near reschedule succeeds. They cannot prove the two things this increment is actually about:
that a patient **understands** why an appointment cannot be changed before they try, and that a
reschedule feels like moving one appointment rather than destroying one and making another. Checks 4
and 6 are the ones worth a written opinion.

## Setup

```bash
cp .env.example .env
# fill in the Google client id and secret — see docs/08-google-setup.md
docker compose -f infra/docker-compose.yml --env-file .env up -d --build
```

- Patient portal at `http://localhost:8080/`, staff console at `/staff`.
- Set `Clinic__SeedDevelopmentData=true`. The demo clinic carries specialties, rooms, appointment
  types, **Dra. Helena** (`dra.helena@clinic.local`) with specialties, durations and working hours, two
  blocked periods, a seeded patient, and — new in this change — a **third appointment a few hours from
  now**, which is inside the 24-hour cutoff while the other two are outside it. So P5 shows both its
  changeable and its locked state on the first load of a fresh stack, with no arranging.
  **Note:** those appointments belong to the seeded patient (`paciente@clinic.local`), not to the
  Google account you sign in with. To see your own, book one on P2 first.
- **No new required environment variable.** `Scheduling__CancellationCutoffHours` defaults to 24. It is
  in `.env.example` as an optional line because two checks below want to shorten it.
- **Two real Google accounts** as before (`00-context.md` §9): one professional, one patient. The
  seeded `dra.helena@clinic.local` is fixture data on a domain that does not exist.
- **Prerequisite, inherited and still open.** 5a's guide asked for
  `2026-08-23-availability-read/validation.md` to be run first and recorded that whether it was run is
  unknown. Task 0.2 asks for that to be settled. If it still has not been run, say so here rather than
  leaving a third guide silently assuming S3 works.

---

## 1 — A patient can find their appointments at all

| | |
|---|---|
| Role | Patient |
| Route | `/` → `/appointments` |

**Action.** Sign in as the patient. Reach the appointment list from the header, then book something on
P2/P3 and follow P4's onward link.

**Expected.** The list is reachable from the header on every screen, and **P4's onward link now lands
on it** rather than on the profile. That link was a named temporary destination in 5a's guide (its
check 15); this is the check that closes it.

The list shows the seeded appointments plus the one you just booked, split into what is still to come
and what is past, with times in clinic wall clock.

## 2 — Cancel, and watch the slot come back

| | |
|---|---|
| Role | Patient |
| Route | `/appointments` → `/book` |

**Action.** Cancel an upcoming appointment. Then search P2 for the same professional, appointment type
and window.

**Expected.** Cancelling asks for confirmation before doing anything. Afterwards the appointment is
shown as cancelled rather than vanishing — it moves out of the upcoming list and is still findable with
its status. Back on P2, **the slot is offered again**, and so are the neighbouring slots the booking had
removed.

**This is the round trip the whole increment exists for.** 5a wrote the exclusion constraints partial
on the live state and could only prove the freeing behaviour by writing a terminal row directly,
bypassing the handler. This is the first time a person can see it happen through the product.

## 3 — Reschedule by a few minutes

| | |
|---|---|
| Role | Patient |
| Route | `/appointments` → `/appointments/:id/reschedule` |

**Action.** Reschedule an appointment to a start **very close** to its current one — the next slot, a
few minutes later. Then check the list, then check P2.

**Expected.** It succeeds. The appointment shows at its new time; the old time is offered again on P2
and the new one is not.

**Why the guide insists on a near move rather than "any new time".** A move to next week succeeds even
when the busy-set filter is wrong (design C7) — the fault only appears when the old and new ranges
touch, because that is the only case where the appointment being moved could be mistaken for an
obstacle to its own replacement. Please do the near move specifically. A far move is a fine second
check, not a substitute.

Also worth a look: does the screen show you **what is being moved** alongside what you are moving it
to? Being able to see both ends of the change before committing is the difference between rescheduling
and gambling.

## 4 — The cutoff is visible before it is hit

| | |
|---|---|
| Role | Patient |
| Route | `/appointments` |

**Action.** Look at the seeded appointment that starts inside the cutoff. Try to act on it.

**Expected.** Its reschedule and cancel actions are **disabled, with an explanation** — a translated
message telling you to telephone reception. Not an enabled button that fails. Not a silently missing
button either: a patient should be able to tell that the action exists and that the rule, not a bug, is
why they cannot use it.

**This is the judgement check.** Say plainly whether the locked state reads as *"the clinic has a
policy"* or as *"this app is broken"*. `02 §5` calls this rule the concrete demonstration of RBAC and
ownership coexisting; that argument only lands if a patient can see the boundary rather than bump into
it. An opinion here is worth more than a pass.

## 5 — The cutoff closing while you watch

| | |
|---|---|
| Role | Patient |
| Route | `/appointments` |

**Action.** Set `Scheduling__CancellationCutoffHours` low enough to be reachable (or place an
appointment just outside a short cutoff), open the list with the action available, wait for the window
to close, then press it.

**Expected.** A translated message about the cutoff, and the list refreshes to show the appointment as
no longer changeable. Not a raw code, not a silent no-op, and not a stale enabled button.

**Why this exists.** The screen is told by the server whether an appointment can be changed (design
C10), so it cannot recompute the answer as time passes. That is the right trade — a browser clock is
not the clinic's — and its cost is exactly this window, which is therefore designed for rather than
prevented. Restore the setting afterwards.

## 6 — Whether a reschedule feels like one appointment or two

| | |
|---|---|
| Role | Patient |
| Route | `/appointments` |

**Action.** After rescheduling, read the list as a stranger would. Look for the appointment you moved.

**Expected.** Whatever the implementation does, it should be comprehensible. Underneath, a reschedule
is two rows — the original terminated and a new one created — because the audit trail requires it
(design C5). The screen should not make the patient learn that.

**Judgement, not behaviour.** Say whether the past list showing a `Rescheduled` entry alongside the new
appointment reads as helpful history or as clutter. **This answers design Open Question 1** — where
terminal appointments belong in the list — and the working answer (two lists split by time, terminal
ones annotated in the past list) was chosen without a human having seen it. If a third "cancelled"
section, or hiding rescheduled originals entirely, reads better, that is a finding and it is welcome.

## 7 — A cancelled appointment cannot be cancelled twice, and the wording is worth an opinion

| | |
|---|---|
| Role | Patient, in two browser windows |
| Route | `/appointments` |

**Action.** Open the list in two windows as the same patient. Cancel an appointment in one. Then press
cancel on the stale screen in the other.

**Expected.** A translated message saying the appointment is no longer in a state that can be changed,
and the list corrects itself. Exactly one cancellation exists.

**If the true race is hard to hit by hand**, say so — the concurrency is covered by the integration
tier (including the cancel-versus-reschedule race, which is the one that could otherwise leave a patient
cancelled *and* booked). What this check is for is the *experience* of acting on a stale screen, which
the stale-window route reaches reliably.

**And this one needs an opinion, because the code behind it is flagged.** The message comes from
`booking.appointment_not_changeable`, a code this change **added against its own stated brief** — the
catalogue had no honest answer for "already terminal", and the three candidates were each wrong in a
different way (see design C12). Read the message as a patient: does it explain what happened, in both
languages? If a reviewer would rather overload an existing code, that is one mapping line and one i18n
pair, and this is the check where the cost of getting it wrong is visible.

## 8 — Consent stops a reschedule and does not stop a cancel

| | |
|---|---|
| Role | Patient |
| Route | `/profile` → `/appointments` |

**Action.** On P7, revoke the data-processing consent. Then try to reschedule an appointment. Then try
to cancel one.

**Expected.** The **reschedule is refused** with a translated consent message and a way to grant it,
as P3 does. The **cancel succeeds**.

**Why the asymmetry is the point.** A reschedule creates an appointment, so it goes through the gate 5a
built. A cancel reduces what the clinic holds, and refusing to let somebody leave because they withdrew
consent would trap them as a consequence of exercising a right (design C11). Confirm the cancel really
does go through — this is the check that would catch the gate being applied by reflex to both paths.

## 9 — Only your own appointments

| | |
|---|---|
| Role | Patient |
| Route | `/appointments` |

**Action.** With two patient accounts, note an appointment id belonging to one and try to reach it from
the other — by URL on P6, and by the API directly if convenient. Then try an id that does not exist at
all.

**Expected.** Both refuse, and **both refuse the same way** — the response for someone else's
appointment is indistinguishable from the response for an id that was never real, so the endpoint
cannot be used to discover which appointments exist (design C6). The message shown is translated, not a
raw code.

## 10 — Times are the clinic's, not the browser's

| | |
|---|---|
| Role | Patient |
| Route | `/appointments`, `/appointments/:id/reschedule` |

**Action.** Change your operating system's timezone to something several hours away and reload. Compare
the times shown, **and whether the cutoff-disabled state is still on the same appointment**.

**Expected.** Identical, both times. Every displayed time is clinic wall clock converted from the
response's instants, and the disabled state does not move, because the server decided it (C10).

**This is the check no test in the repository can fail** — the whole suite runs in one process with one
notion of local time. 5a's version of this check was run once, on one machine, in one timezone; this
extends it to the one new thing that could plausibly have been computed locally.

## 11 — Both languages, everywhere new

| | |
|---|---|
| Role | Patient |
| Route | Every route this change added |

**Action.** Switch pt-BR ↔ en on: the list with upcoming and past appointments, the cutoff-disabled
state and its explanation, the cancel confirmation, the reschedule screen with the search showing, a
cutoff refusal, a consent refusal, an already-terminal refusal, and an ownership refusal.

**Expected.** Every string changes. No raw translation key is visible in any state, including the
transient ones.

## 12 — Nothing left the building

| | |
|---|---|
| Role | Patient, then professional |
| Route | `/appointments`, then `/staff/calendar` |

**Action.** Cancel an appointment, then look at S2 and at the API logs.

**Expected.** **Nothing is propagated anywhere.** No outbound call, no error about a missing calendar,
no log line implying a sync was attempted.

**This confirms a seam rather than a feature.** `06 §P6` says cancelling "propagates to the external
calendar" and `02 §5` says the Google event is removed — both describe change 6, not this one (design
C9). What is being validated is that the absence is *clean*: this increment delivers the internal
release and does not half-attempt the rest. Record that you looked.

## 13 — Accessibility, at the level this project claims

| | |
|---|---|
| Role | Patient |
| Route | `/appointments` → `/appointments/:id/reschedule` |

**Action.** Cancel an appointment and complete a reschedule using **only the keyboard**. Then run the
browser's accessibility audit on the list.

**Expected.** Every control reachable with visible focus; the confirmation dialog traps focus and is
dismissible; **a disabled action's explanation is available to a screen reader**, not conveyed only by
the button looking grey; refusal messages are announced rather than only shown. The audit reports
nothing WCAG 2.1 AA would fail.

**The disabled-with-explanation pattern is the new risk here.** `06 §2` names elderly users as part of
this audience. A control that is disabled for a reason the assistive tree does not carry is exactly the
failure this check exists to find, and it is new in this change.

---

## Outcome

- **Run on:** 2026-08-24
- **Run by:** the maintainer, in a browser against the local Compose stack with the real Google client
- **Result:** **passed** — the guide was executed and the maintainer confirmed it
- **Notes:** two decisions were taken during the run; see below, along with what is deliberately
  *not* in this record

### What was decided during the run

**Check 7 — `booking.appointment_not_changeable` stays.** The code was added against this change's own
stated brief and flagged for review on 5a's `patient_busy` terms (design C12). The maintainer kept it:
the catalogue had no honest answer for an already-terminal appointment, and each existing candidate was
wrong in a different way — `appointment_not_found` denies a row the patient can see on P5,
`auth.ownership_denied` is about *who* is asking when the patient does own it, and `cutoff_passed`
gives a time-based reason for a state-based refusal. **The flag is now resolved**, and the proposal's
"almost no new error codes" paragraph stands as the record of why there is one.

**Check 6 / design Open Question 1 — the shipped answer is confirmed.** P5 splits by **time**, with
terminal appointments annotated where they fall rather than gathered into a third section or hidden.
"What happened to my 3pm?" is answerable where a patient would look for it. The two alternatives — a
separate cancelled section, and hiding rescheduled originals — were considered and declined. **Open
Question 1 is closed.**

### What is NOT in this record, stated plainly

Per the standard 3b set, and 5a's: a blank or vague Outcome is indistinguishable from an overlooked
one, so the gaps are named rather than left to be inferred.

- **The two remaining judgement checks (3 and 4) came back without recorded opinions.** The guide
  asked whether the cutoff-locked appointment reads as a policy or as a bug, and whether the reschedule
  screen shows both ends of the change convincingly. The maintainer confirmed the guide passed but did
  not relay a written opinion on either. Silence is not approval: the screens work; whether they are
  *good* is unrecorded. Check 4 is the one that matters most, because `02 §5` rests an argument on it.
- **The two inherited debts are still open.** Change 4's **F8 response-size number** (task 0.1) was not
  captured, and whether `availability-read`'s validation guide was run (task 0.2) is still unrecorded
  in that guide's own Outcome. Both were open when this change started and both are open now — and P6
  has since become a second consumer of the availability response, so a figure captured later can no
  longer be attributed to one screen. This is the **second** change to leave 0.1 open; `booking-desk`
  inherits it as a third.
- **Check 7's stale-screen route was not reported** — whether the already-terminal refusal was reached
  by a genuine race or by the two-window route. The concurrency itself is covered by the integration
  tier, including the cancel-versus-reschedule race that could otherwise leave a patient cancelled
  *and* booked.

### What was never examined by anybody, and is not claimed to be

Everything below the API line is covered by the automated tiers, which are green: **248 domain unit
tests and 292 integration tests** against a real PostgreSQL via Testcontainers, plus the i18n key check
(321 keys, both locales, usage scan clean) and both SPA builds with `tsc --noEmit`. The compose-smoke
tier gained the two new portal routes and the three new API routes; those run only against a live
stack and their result is not recorded here.

**One risk no manual check in this guide can reach.** The reschedule's statement ordering — the
decision this increment was most careful about — is invisible from the product. Reversing the two
statements leaves the entire suite green, because EF Core orders its own command batch. It is asserted
against raw SQL in `RescheduleOrderingTests` and pinned explicitly in the handler; no amount of
clicking would ever have found it, and none of the checks above tried.

**And check 10 remains the one worth worrying about**, inherited from 5a: times rendered in the
browser's zone rather than the clinic's pass every test in this repository, because the whole suite
runs in one process with one notion of local time. It was checked by a person, on one machine.
