# Validation guide — booking-core

Manual checks a human runs against the locally-running app (`00-context.md` §9). Everything here is
deliberately **outside** what the test suite can assert: browser interaction, both-locale rendering,
visual and interaction judgement, and what only exists once a request has crossed Caddy into a real
container.

**This guide is not short, and that is the point.** Every previous change's guide could argue that its
surface was administrative and utilitarian. This one delivers **P2 — the flagship**, the public,
recruiter-facing screen that is the visible form of the project's whole thesis, plus the first
patient-facing flow that writes anything. Three of the checks below are judgements a test can confirm
are *implemented* but cannot confirm are *right*, which is exactly the category §9 exists for.

**Rewritten after implementation to match what was built.** Four things moved. The draft assumed a
signed-in patient would still land on their profile — they now land on the search, so check 1 got
simpler. It assumed P3 would *capture* consent; it turned out to *gate* on it, which made check 7 the
more interesting one rather than a formality. Two checks were added that the draft could not have
predicted: the professional's display name is derived from their account address (a seam, not a
finished feature) and deserves a human opinion, and the search now has a keyboard path through a
bespoke slot grid that Radix does not provide for free. The draft's check on the `no-store` header
was dropped — the integration tier asserts it, so it was padding.

**A prerequisite that has changed.** `availability-read` archived with its guide **unexecuted**, and
named its trigger explicitly: the first deployment with a configured Google OAuth client. That trigger
has now fired — the client is configured, which is why P2–P4 are validatable at all. So
**run `openspec/changes/archive/2026-08-23-availability-read/validation.md` first**, or at least its
checks 1–5, and record the result there. This guide assumes S3 works; that assumption has never been
seen by a person.

## Setup

```bash
cp .env.example .env
# fill in the Google client id and secret — see docs/08-google-setup.md
docker compose -f infra/docker-compose.yml --env-file .env up -d --build
```

- Patient portal at `http://localhost:8080/`, staff console at `/staff`.
- Set `Clinic__SeedDevelopmentData=true`. The demo clinic carries specialties, rooms, appointment
  types, **Dra. Helena** (`dra.helena@clinic.local`) with specialties, durations and working hours, two
  blocked periods, and — new in this change — a seeded patient with an appointment or two.
- No new environment variable. `Scheduling__*` and `Auth__ConsentVersion` already exist with defaults.
- **Two real Google accounts you control are needed** (`00-context.md` §9): one to invite as a
  professional in S11 and claim on S0, and a second to exercise the patient path on P1. The seeded
  `dra.helena@clinic.local` is fixture data on a domain that does not exist — it drives the API and the
  solver, never the browser sign-in.
- Before starting, note which of the two Google accounts is the patient. Several checks below turn on
  it being a *first-time* patient, and that is a one-shot state.

---

## 1 — A first-time patient reaches the search at all

| | |
|---|---|
| Role | Patient (second Google account, never signed in before) |
| Route | `/` then `/book` |

**Action.** Sign in on P1 with the patient Google account for the first time.

**Expected.** Sign-in succeeds and lands you **directly on the booking search** — not on your
profile. That changed with this increment: P1's stated purpose is to explain the clinic and start
booking, and sending a signed-in patient to their own record was only right while booking did not
exist. The header carries a link to the search from every screen.

The search asks for a specialty, a kind of visit and a professional. Nothing on screen mentions a
room, and nothing asks you to pick one — the patient never chooses a resource, and the request does
not even carry a field for it.

**Also note.** This is the just-in-time provisioning path from change 2 doing its job for a real
Google identity for the first time in a booking context. If it refuses, stop and record it: everything
below depends on it.

## 2 — The search finds real free time, and it looks like a product

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Pick a specialty, an appointment type, "any professional", and the default date window.
Read the results as a stranger would.

**Expected.** Slots appear, grouped by day, in clinic wall clock. Each result says enough to choose
from — the time, the professional, how long the visit is. Loading was visible rather than a frozen
page.

**This is the judgement check.** Say plainly whether this screen looks like the showcase `06 §Z2`
promises, or like a form with a list under it. An opinion here is worth more than a pass.

## 3 — The date window is realistic, and the response is not absurd

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Widen the window to something a person would genuinely ask for — a month, or whatever the
maximum permits — with "any professional". Open the browser's network panel and look at the
availability response: its size, and how long it took.

**Expected.** The page stays usable and the response is a size you would be willing to serve.

**This closes a named revisit trigger.** Change 4's design F8 recorded that the response grows with
window × professionals ÷ step and that nobody would feel it until P2 existed. This is that moment.
**Record the actual numbers** — payload size and time — in the Outcome, whatever they are. If it is
uncomfortable, that is a finding, not a failure; the mitigations (a narrower default, day-level
pagination, coarser steps at distance) are all cheap and none of them is in this change.

## 4 — Empty is a success, not a failure

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Search a window with nothing free — a weekend, or a specialty and professional
combination with no hours.

**Expected.** The screen says nothing is free in that window and invites another one. It does not look
like an error, and it does not look like a bug. No spinner is left running.

## 5 — A failing search explains itself, in both languages

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Provoke the error state. The reliable way is the rate limit: repeat the search faster than
the configured per-minute budget until it refuses. Then switch language with the message on screen.

**Expected.** A translated message, not a raw code and not a blank list. Retrying works once the
budget recovers. Both pt-BR and en read as sentences a patient would understand — check specifically
that the rate-limit message does not read as though the patient did something wrong.

## 6 — Booking works, end to end, and the confirmation confirms

| | |
|---|---|
| Role | Patient (first-time) |
| Route | `/book` → `/book/confirm` → `/book/success` |

**Action.** Pick a slot. On the confirm screen, read what is asked of you before filling it in. Submit.

**Expected.** The confirm screen summarises the professional, the appointment type, and the time in
clinic wall clock. It asks for a **contact phone** and nothing more — no birth date, no document
number. The name is shown and correctable. Submitting lands on a confirmation that states the
appointment was created and repeats its details.

**Also note.** Whether the confirm screen asked for anything the appointment does not need. LGPD
minimization (02 §8) is a claim this screen either keeps or quietly breaks.

## 7 — Consent is visible, and withdrawing it actually stops a booking

| | |
|---|---|
| Role | Patient |
| Route | `/profile` then `/book/confirm` |

**Action.** On P7, revoke the data-processing consent. Then search and try to book.

**Expected.** The booking is refused with a translated message about consent, **on the confirm screen
where it happened**, with a way to grant the consent there. Granting it and submitting again succeeds
without a trip back to the profile.

**Why this matters more than it looks.** Change 2 made revocation possible and nothing checked it, so
until this change a patient could withdraw consent and keep transacting. This check is the only
human-visible proof that the loop is closed — and the reason `06 §P3`'s description of this screen was
corrected: there was never a consent to *capture*, because provisioning already granted it. What P3
owns is the gate, and the grant-in-place is the way back from a dead end this increment would
otherwise have created.

## 8 — The slot you just booked is gone

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Search the same window again after booking. Look for the slot you took, and for the slots
either side of it.

**Expected.** Your slot is absent. So are the overlapping neighbours the finer step had offered — a
40-minute visit booked at 09:00 removes 08:45 and 09:15 as well, not just 09:00. The slot that ends
exactly when yours begins is **still offered**, because touching is not overlapping.

**This is change 4's seam, seen by a person for the first time.** The subtraction it shipped with an
empty appointment producer now has one.

## 9 — Two people cannot take the same slot, and the loser is treated well

| | |
|---|---|
| Role | Patient, in two browser sessions |
| Route | `/book` → `/book/confirm` |

**Action.** In two browsers (or a normal and a private window), sign in as the same patient in one and
as another patient in the other. Load the same slot on both confirm screens. Book in one, then book in
the other.

**Expected.** The second one is refused with a translated "just taken" message, the offending slot
disappears from the search, and **the search the patient had made is still on screen** — they are not
returned to an empty form.

**If the race is hard to reproduce by hand**, say so and record it: the concurrency itself is covered
by integration tests, and what this check is really for is the *experience* of losing, which you can
reach by booking the slot in one window and then submitting the stale confirm screen in the other.

## 10 — Times are the clinic's, not the browser's

| | |
|---|---|
| Role | Patient |
| Route | `/book`, `/book/confirm`, `/book/success` |

**Action.** Change your operating system's timezone to something far away (a several-hour offset) and
reload. Compare the times shown against what they were.

**Expected.** Identical. Every time on all three screens is clinic wall clock, converted from the
instants using the timezone the response carries.

**This is the check no test in the repository can fail.** The whole suite runs in one process with one
notion of local time; an offset bug here would pass all of it.

## 11 — The two-slots-at-one-local-time case

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** This needs a clinic timezone that observes daylight saving, so temporarily set
`Clinic__Timezone` to a DST-observing zone (`America/New_York` or `Europe/London`), restart the API, and
search a window containing that zone's autumn fall-back date. Note that the seeded working hours must
cover that weekday — adjust or add hours in S7 if not.

**Expected.** Two slots reading the same local time appear, an hour apart in real time, and the screen
tells them apart rather than hiding one.

**Judgement, not behaviour.** This answers change 4's design open question 4. Say whether the
disambiguation reads as sensible to a patient or as a glitch. Either answer is useful; "it renders" is
not. Restore `Clinic__Timezone` afterwards.

## 12 — The search survives a reload and a back button

| | |
|---|---|
| Role | Patient |
| Route | `/book` → `/book/confirm` → back |

**Action.** Search, reload the page, then go to a confirm screen and press the browser's back button.

**Expected.** The same search and the same results, both times, without re-entering anything. The URL
is shareable — paste it into the other browser and confirm it opens the same search.

## 13 — A professional can no longer block over a booked appointment

| | |
|---|---|
| Role | Professional (the invited Google account) |
| Route | `/staff/blocks` |

**Action.** Sign in on S0 as the professional the patient just booked with. Try to add a block covering
that appointment's time. Then try one that begins at the exact instant the appointment ends. Then edit
an existing block so it would land on the appointment.

**Expected.** The overlapping block is refused with a translated message **inside the dialog**, and the
list is unchanged. The abutting block is accepted. The edit is refused and the block keeps the range it
had — the list still shows the truth.

**This is the retrofit, in a browser.** Change 4 shipped this path deliberately unchecked because
nothing could race it; this check is the only human-visible evidence that the plan was carried out.

## 14 — Both languages, everywhere new

| | |
|---|---|
| Role | Patient, then professional |
| Route | Every route this change added |

**Action.** Switch pt-BR ↔ en on: the search with results, the empty state, the error state, the taken
state, the confirm screen (including with the consent prompt and with a refusal showing), the success
screen, and the S3 dialog showing the new refusal.

**Expected.** Every string changes. No raw translation key is ever visible, in any state, including the
transient ones.

## 15 — What P4 points at

| | |
|---|---|
| Role | Patient |
| Route | `/book/success` |

**Action.** Click the onward link.

**Expected.** It goes to the profile, not to a 404. **This is a known temporary destination**: "My
appointments" (P5) is `booking-lifecycle`'s screen. Confirm it is a working link and record that its
final destination is 5b's, so nobody demonstrating this build is surprised by it.

## 16 — Whose name is on the button

| | |
|---|---|
| Role | Patient |
| Route | `/book` |

**Action.** Open the professional dropdown, and look at the names on the slot buttons in
any-professional mode.

**Expected.** Readable names — for the seeded professional, "Dra Helena".

**This is a seam, and it wants an opinion rather than a pass.** The `Professional` record carries no
name: 3b created it with only a user reference, and S7 lists professionals by email because an
administrator already knows their own staff. A patient does not, and showing them an internal email
address reads badly and hands out staff addresses for no reason — so **the server derives a label
from the account's local part**. It reads correctly for an address a clinic would actually issue
(`maria.silva@…` → "Maria Silva") and oddly for a generated one.

**Answered, ahead of the run: good enough to ship, because the replacement is scheduled.** The
maintainer's condition was that a change already exist to add the real field — it did not, so one now
owns it: **`booking-lifecycle` (5b) adds `Professional.fullName` with a field on S7**, recorded as
**P-5** in `02-domain-model.md` §10 and in the build order. It lands there because 5b needs a
professional's name three times over (S1, S4, S5), not because booking asked for it.

So this check is no longer a decision. What is still worth a sentence when you run it: whether the
derived labels read acceptably **for the accounts you actually signed in with**. The derivation is
right for an address a clinic issues (`maria.silva@…` → "Maria Silva") and odd for a generated one,
and it is the odd case a demo would expose.

## 17 — Accessibility, at the level this project claims

| | |
|---|---|
| Role | Patient |
| Route | `/book` → `/book/confirm` |

**Action.** Complete a booking using **only the keyboard**. Then run the browser's built-in
accessibility audit on P2.

**Expected.** Every control is reachable and its focus is visible; the slot list is navigable without a
mouse; the refusal messages are announced rather than only shown. The audit reports nothing that WCAG
2.1 AA would fail.

**Why this check is here and not in a test.** `06 §2` names elderly users as part of P2's audience and
sets AA as the target for this surface specifically. Radix under shadcn gives a good default and does
not give a keyboard path through a bespoke slot grid.

---

## Outcome

- **Run on:** 2026-08-24
- **Run by:** the maintainer, in a browser against the local stack
- **Result:** **passed** — the guide was executed and the maintainer confirmed it
- **Notes:** see below, including what is deliberately *not* in this record

### What was decided during the run

**Check 16 — the derived professional name: accepted.** The condition was that a change already own
the real field. It did not, so one was given it during this change: **`booking-lifecycle` (5b) adds
`Professional.fullName` with a field on S7**, recorded as **P-5** in `02-domain-model.md` §10 and in
the build order. The derived label ships as an explicitly scheduled debt rather than as a fix.

### What is NOT in this record, stated plainly

Per the standard 3b set — a blank or vague Outcome is indistinguishable from an overlooked one, so
the gaps are named rather than left to be inferred:

- **Check 3's measurement was not captured.** The maintainer confirmed the guide passed but did not
  relay the payload size and duration for a realistic window. **Change 4's design F8 response-size
  revisit trigger therefore stays OPEN**: the check was performed, but no figure was written down, so
  nothing here can be compared against later. Closing it needs one number, and the cheapest moment to
  record it was this one.
- **The three remaining judgement checks (2, 11, 17) came back without recorded opinions.** Silence is
  not approval, so this says so: nobody has written down whether P2 reads as the showcase `06 §Z2`
  promises, whether the fall-back-day disambiguation reads sensibly to a patient, or how the keyboard
  path through the slot grid felt. The screens work; whether they are *good* is unrecorded.
- **Check 9's race reproducibility was not reported** — whether the just-taken state was reached by a
  genuine concurrent race or by submitting a stale confirm screen. The concurrency itself is covered
  by the integration tier; what is unrecorded is which route a human took to see it.
- **`availability-read`'s deferred guide** — this guide opens by asking for it to be run first, since
  checks 8 and 13 assume S3 works. Whether it was run, and its result, is not recorded here. That
  guide's own Outcome section remains the place for it.

### What was never examined by anybody, and is not claimed to be

Everything below the API line is covered by the automated tiers, which are green: **225 domain unit
tests and 262 integration tests** against a real PostgreSQL, plus the i18n key check and both SPA
builds. The compose-smoke tier gained three checks for this change's routes; those run only against a
live stack and their result is not recorded here either.

**Check 10 remains the one worth worrying about.** An offset bug — times rendered in the browser's
zone rather than the clinic's — passes every test in this repository, because the whole suite runs in
one process with one notion of local time. It was checked by a person once, on one machine, in one
timezone.
