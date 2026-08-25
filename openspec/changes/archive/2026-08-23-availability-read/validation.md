# Validation guide — availability-read

Manual checks a human runs against the locally-running app (`00-context.md` §9). Everything here
is deliberately **outside** what the test suite can assert: browser interaction, both-locale
rendering, and what only exists once a request has crossed Caddy into a real container.

**Rewritten after implementation to match what was built.** The draft assumed a check on the
duplicate-local-time case would need a browser; it does not — the unit tier asserts it, and what
is left for a person is a judgement about whether the output is fit to hand to P2. Check 7 was
rewritten on that basis, and the draft's separate "no-store through the proxy" note folded into
check 6.

Short on purpose. Most of this change is test-assertable — 45 unit tests for the solver and the
block, 22 integration tests for the loading step and the authorization matrix — so padding this
list to look thorough would be the anti-pattern `00-context.md` §9 exists to prevent.

The list is dominated by one thing: **S3 is the first professional-role screen this console has
ever rendered.** Every screen before it was an administrator's. That branch of the shell, and the
Google sign-in a professional needs to reach it, have no coverage at all.

## Setup

```bash
cp .env.example .env
docker compose -f infra/docker-compose.yml --env-file .env up -d --build
```

- App at `http://localhost:8080`, staff console at `/staff`.
- Set `Clinic__SeedDevelopmentData=true`. The demo clinic from 3b now also carries **two blocked
  periods** for **Dra. Helena** (`dra.helena@clinic.local`), placed on the coming Monday and
  Tuesday relative to whenever the stack first started.
- The scheduling parameters (`Scheduling__*`) are commented out in `.env.example` and have code
  defaults — a 15-minute step, a one-hour lead time, a 60-day horizon, a 31-day maximum window.
  Nothing needs setting.
- **Signing in as a professional needs a configured Google client** (`08-google-setup.md`).
  Without one, checks 1–5 cannot be run as written. Record that rather than substituting an
  administrator — the whole point of those checks is the role nobody has exercised.

---

## 1 — The professional's navigation exists, and nobody else's shows it

| | |
|---|---|
| Role | Professional, then administrator, then front desk |
| Route | `/staff`, then `/staff/blocks` directly |

**Action.** Sign in as Dra. Helena and look at the navigation before clicking anything. Then sign
in as the administrator and as a front-desk user, and type `/staff/blocks` directly.

**Expected.** Blocked time appears for the professional and for neither of the others. The direct
route renders no protected data for them — not a flash of the table followed by a redirect, and
not an empty table that reads as "you have no blocked time".

## 2 — The seed shows blocked time on a fresh stack

| | |
|---|---|
| Role | Professional |
| Route | `/staff/blocks` |

**Action.** On a freshly created stack (`down -v` then `up`), open blocked time as Dra. Helena.

**Expected.** Two periods listed, both **In force**, on dates in the coming week rather than in
the past. They are placed relative to first start precisely so this never becomes a fixture that
silently stops demonstrating its own feature — if they read as historical, say so.

## 3 — What the professional typed is what they read back

| | |
|---|---|
| Role | Professional |
| Route | same |

**Action.** Add a block for tomorrow 12:00–13:00. Reload the page. Edit it to 12:30–13:30 and
reload again.

**Expected.** The times read back **exactly as entered**, both times, with the line beneath the
table naming the clinic's timezone. An offset bug between the browser's zone, the clinic's zone
and UTC surfaces here and almost nowhere else — the tests assert the conversion, but only a person
can confirm the round trip through a real browser in a real timezone does not shift by an hour.

## 4 — The refusal lands inside the form

| | |
|---|---|
| Role | Professional |
| Route | same |

**Action.** Two attempts, one at a time: a block ending before it starts, then one whose end
equals its start.

**Expected.** Each shows the translated `block.invalid_range` message **inside the dialog**, above
the buttons, with the stored list unchanged behind it. Both map to one code, so confirm the wording
reads sensibly for both cases without going vague — that is a judgement about the sentence, not
about the API.

## 5 — Both locales, every new surface

| | |
|---|---|
| Role | Professional |
| Route | `/staff/blocks` |

**Action.** Switch pt-BR ↔ en on the list, with the dialog open, and with a refusal on screen.

**Expected.** Every string changes and no raw key (`blocks.…`) is visible. Check the timezone note
and the **In force / Retired** labels too, not only the headings.

## 6 — Blocking time visibly changes availability, through the real stack

| | |
|---|---|
| Role | Professional, then any signed-in user |
| Route | `/staff/blocks`, then `/api/availability` |

**Action.** Find Dra. Helena's professional id and one of her appointment type ids (the API's
config endpoints as an administrator, or the database). Request

```
/api/availability?appointmentTypeId=<id>&from=<next Monday>&to=<next Monday>
```

and note the slots. Then block a period inside her 08:00–12:00 or 13:00–17:00 hours in S3, and
request the same window again.

**Expected.** Exactly the overlapping slots are gone. A slot ending at the instant the block
begins is **still offered** — touching is not overlapping.

An integration test asserts this in process. What this check adds is that it holds **through Caddy
against the real container and the seeded clinic**, which is the gap the smoke tier exists for.
While here, confirm the response carries `Cache-Control: no-store` **as the browser receives it**:
a proxy is entirely capable of adding or stripping that header, and no in-process test can see it.

## 7 — Judge the duplicate-local-time output

| | |
|---|---|
| Role | Operator |
| Route | n/a — configuration, then `/api/availability` |

**Action.** Set `Clinic__Timezone=America/New_York`, restart, give a professional working hours
that span 00:00–05:00 on **1 November 2026**, and request availability for that date.

**Expected.** Six hours of real slots from five wall-clock hours, and two distinct slots whose
local time reads the same, an hour apart in UTC. This is correct per design F2 and is asserted by
the unit tests.

**What is being judged, not verified:** whether that output is defensible to hand to P2 in change
5, or whether the read owes the client something distinguishing the two. Design open question 4.
Record the opinion either way — this is the one decision in the change a test can confirm is
*implemented* but cannot confirm is *right*.

---

## Outcome

- **Run on:** deferred 2026-08-23; **resolved 2026-08-24**
- **Run by:** not run as its own pass — discharged by two later guides that were (see below)
- **Result:** **closed** — the blocking condition is gone and the substance was covered elsewhere
- **Notes:** the original deferral and its reasoning are kept below, unedited, because the trigger
  they named is what fired

### Closed — 2026-08-24, and how

**The trigger fired.** This guide named it precisely: *"the first deployment with a configured Google
OAuth client."* That happened before `booking-core`, whose own guide opens by asking for this one to
be run first. It was not run then, and `booking-lifecycle`'s guide asked again and recorded a second
time that the answer was unknown. Rather than ask a third time, it is settled here.

**Discharged rather than executed, and the distinction matters.** This guide was never run as its own
pass. What removed its blocker — no way to reach S3 as a professional, because the seeded
`dra.helena@clinic.local` is on a domain that does not exist — is that a **real Google professional
account now exists**, and two later guides used it. Reading the record rather than asserting from
memory:

- `booking-core`'s Outcome records **passed**, executed by the maintainer with the real Google client.
- Its **check 13** required signing in on S0 as an invited professional and using **S3** to create,
  edit and be refused a block — which is checks 1, 2, 4 and 5 of this guide in substance, on the same
  surface, as the same role.
- Its **check 14** required switching pt-BR ↔ en on *"the S3 dialog showing the new refusal"* — which
  is this guide's both-locales requirement on the surface this change introduced.

**What is still not covered, stated plainly.** This guide's **check 3** — the wall-clock round trip on
**S3 specifically**, in a browser in a shifted timezone — has still never been performed. Later guides
covered the equivalent on P2/P3/P4 (`booking-core` check 10) and on P5/P6 (`booking-lifecycle` check
10), and both passed, so the conversion code they share is exercised. S3 writes wall clock rather than
reading instants, which makes it the one surface where the shared evidence is weakest.

**That residual risk is not closed and is not claimed to be.** It is the same class the original note
called *"the one worth worrying about": an offset bug there would pass every test in this repository.*
It now has one fewer place to hide and one place left. Whoever next touches S3 should shift their
machine's timezone once and look.

**This change was archived with its guide unexecuted, by the maintainer's decision.** Recorded here
rather than left blank, because a blank Outcome is indistinguishable from an overlooked one — and
that is the exact failure `00-context.md` §9 was written to stop after `identity-session` and
`clinic-catalog` archived with unchecked human-verification boxes.

**Why:** checks 1–5 need a real professional in the browser, and a professional signs in with
Google. A Google client is not configured for this deployment yet, and the seeded
`dra.helena@clinic.local` is on a domain that does not exist, so there is no way to reach S3 as its
own role today. Running the guide against an administrator instead would be a false green for the
only role this change introduces to the console.

**The trigger to come back:** the first deployment with a configured Google OAuth client — which
change 6 (`calendar-outbound`) needs anyway, since it requests the Calendar scope by incremental
authorization on top of the same client. When that lands, run this guide before change 6's own.

**What is and is not covered in the meantime.** Everything asserted below the API line is covered by
the automated tiers, which passed: 51 unit tests, 23 integration tests, and the 18-check smoke tier
through Caddy. What has **never been seen by a person** is precisely this list — S3 rendering, the
professional-role navigation branch, both locales on the new surface, the refusal landing inside the
dialog, and the wall-clock round trip through a real browser in a real timezone. Check 3 is the one
worth worrying about: an offset bug there would pass every test in this repository.

Record explicitly whether checks 1–5 were run as a real professional or blocked on Google
configuration. "Passed" against an administrator would be a false green for the only role this
change introduces to the console.

Two of the checks test a judgement rather than a behaviour — check 3's round trip and check 7's
output — and if neither comes back with an objection, say that plainly rather than letting silence
read as approval. Design open question 4 (fall-back display) keeps its revisit
trigger either way. Open question 1 is closed — a slot names its resource — and what it left behind
is a constraint on change 5 rather than a check here: the booking path must assign the room itself
and must not trust a resource id a caller sends back.
