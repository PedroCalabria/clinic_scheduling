# Validation guide — professional-configuration

Manual checks a human runs against the locally-running app (`00-context.md` §9). Everything here
is deliberately **outside** what the test suite can assert: browser interaction, both-locale
rendering, whether a refusal lands where the eye is looking, and whether the screen is legible
enough to trust. If a check below turns out to be automatable, it belongs in the test suite
instead — move it and delete the item.

Rewritten after implementation to match what was actually built. The draft assumed a grid of
duration inputs; the section became a table plus a dialog, which changed checks 4 and 5.

## Setup

```bash
# Clinic__Timezone is required now — the API will not start without it.
# Clinic__SeedDevelopmentData=true is what provisions the demo clinic.
cp .env.example .env
docker compose -f infra/docker-compose.yml --env-file .env up -d --build
```

- App at `http://localhost:8080`, staff console at `/staff`.
- Sign in as the bootstrap administrator from `.env`; replace the password when prompted.
- With `Clinic__SeedDevelopmentData=true`, a demo clinic exists already: two specialties, three
  kinds of visit, three rooms, and **Dra. Helena** (`dra.helena@clinic.local`) qualified in
  cardiology only, with two durations and a five-day split week.

---

## 1 — The seed produced a clinic worth demonstrating

| | |
|---|---|
| Role | Administrator |
| Route | `/staff/admin/professionals` |

**Action.** Open the professionals screen on a freshly created stack (`down -v` then `up`).

**Expected.** Dra. Helena is listed, marked **Configured**, with a summary reading roughly
*"1 specialties · 2 durations · 10 hour blocks"*. Nothing reads as placeholder text (no "Test",
no "foo"). Open her and confirm the four sections are populated.

## 2 — An unconfigured professional is visible and openable

| | |
|---|---|
| Role | Administrator |
| Route | `/staff/users`, then `/staff/admin/professionals` |

**Action.** Invite a new professional in S11 (register a professional by email). Return to the
professionals screen.

**Expected.** They appear, marked **Not configured** and *"has not signed in yet"*. Open them —
the detail view is editable, not blocked. This is the requirement that ruled out creating the
record at first sign-in, so it is worth seeing rather than trusting.

## 3 — The gate explains itself before it is usable

| | |
|---|---|
| Role | Administrator |
| Route | the professional from check 2 |

**Action.** Look at **Visit durations** before assigning any specialty.

**Expected.** Instead of an empty panel or a dead button, an explanation:
*"Assign a specialty above first — the kinds of visit offered here are the ones its specialties
allow."* Then assign one specialty and look again.

**Expected.** The **Set duration** button appears, and its chooser offers only kinds of visit
belonging to that specialty. If you find yourself guessing why a visit type is missing, this
check has failed even though the API is correct.

## 4 — The gate's refusal names the count and is survivable

| | |
|---|---|
| Role | Administrator |
| Route | same |

**Action.** Set a duration for the assigned specialty's visit type. Then remove that specialty.

**Expected.** Refused, with the translated message naming how many durations depend on it, shown
**beside the specialties section** rather than at the top of the page. The specialty is still
there afterwards, and so is the duration. Clear the duration, then remove the specialty again —
now it succeeds.

## 5 — Two professionals hold genuinely different durations

| | |
|---|---|
| Role | Administrator |
| Route | `/staff/admin/professionals` |

**Action.** Give your new professional the same specialty Dra. Helena holds, then set a
*different* duration for the same kind of visit.

**Expected.** Both durations persist independently. Re-open each professional and confirm neither
overwrote the other. This is the entire reason `AppointmentType` carries no duration of its own,
and it is worth seeing side by side once.

## 6 — The hours table is legible enough to audit

| | |
|---|---|
| Role | Administrator |
| Route | same → Working hours |

**Action.** Add Monday 08:00–12:00 and Monday 13:00–17:00, both applying from any past date with
no end date.

**Expected.** Both accepted. Then, without re-reading the form, answer from the screen alone:
*"does this person work Monday afternoon?"* and *"is that schedule still in force?"*

If either takes more than a glance, say so in the Outcome — the design chose a table over a week
grid on the grounds that a grid has nowhere to put the effective period, and this check is where
that trade-off is judged rather than assumed.

## 7 — Each working-hours refusal lands beside its cause

| | |
|---|---|
| Role | Administrator |
| Route | same |

**Action.** Three attempts, one at a time, on the same professional:

1. Monday 10:00–14:00 over the same period → expect the **overlap** message
2. Tuesday 22:00–02:00 → expect the **invalid** message
3. Tuesday 09:00–09:00 → expect the **invalid** message

**Expected.** Each message appears **inside the dialog**, above the buttons, and the previously
stored hours are untouched. Confirm cases 2 and 3 read sensibly from the same message — they map
to one code, so the wording has to cover both without being vague.

## 8 — Both legitimate non-overlaps are actually accepted

| | |
|---|---|
| Role | Administrator |
| Route | same |

**Action.** Two attempts that must **succeed**:

1. Wednesday 08:00–12:00 applying Jan–Mar, then Wednesday 08:00–12:00 applying Apr–Dec
2. Thursday 08:00–12:00 and Thursday 13:00–17:00 over the same period

**Expected.** Both accepted. This is the half of the overlap rule that a naive implementation
breaks — and the half a person is least likely to try by accident.

## 9 — An exception overrides one person only

| | |
|---|---|
| Role | Administrator |
| Route | same → Exceptions |

**Action.** Mark this professional unavailable on a date. Choose *"Works different hours"* on a
second date and give it hours. Then open Dra. Helena.

**Expected.** Both exceptions show on the first professional — one as *"Unavailable all day"*,
one as a time range — and Dra. Helena has none. Try the same date twice on one professional and
confirm the second is refused.

While here, judge how tedious a clinic-wide holiday would be at, say, twelve professionals, and
record it — that is the recorded trade-off of making exceptions per-professional.

## 10 — Both locales, every new surface

| | |
|---|---|
| Role | Administrator |
| Route | `/staff/admin/professionals`, list and all four sections |

**Action.** Switch pt-BR ↔ en with the top-bar control, on the list and inside each section, with
at least one refusal message on screen and at least one dialog open.

**Expected.** Every string changes. No raw key (`professionals.…`, `weekdays.…`) is ever visible.
**Weekday names follow the active language** — those are looked up by a computed key, so the
static i18n scan cannot cover them and this is the only thing that will.

## 11 — Reception and the professionals themselves see none of it

| | |
|---|---|
| Role | Front desk, then a professional |
| Route | `/staff`, then `/staff/admin/professionals` directly |

**Action.** Sign in as front desk: confirm no Professionals entry in the navigation, then type the
URL. Repeat as a professional (sign in with Google if configured, or check the API refusal
directly).

**Expected.** Neither renders protected data. Qualification is an administrative decision, so a
professional is refused their own configuration too.

## 12 — A missing timezone stops the app

| | |
|---|---|
| Role | Operator |
| Route | n/a — Compose |

**Action.** Comment out `Clinic__Timezone` in `.env`, set the compose default aside if needed,
then `docker compose up -d` and read the API logs.

**Expected.** The API fails to start, and the message **names the setting**. Not a silent default,
not the host's zone. Restore it and confirm it starts again.

This is the one check whose failure mode is invisible in normal use, which is exactly why a human
runs it once.

## 13 — The seed is genuinely opt-in

| | |
|---|---|
| Role | Operator |
| Route | n/a — Compose |

**Action.** `down -v`, set `Clinic__SeedDevelopmentData=false`, `up -d`, and open the catalog
screens.

**Expected.** Empty catalog, no professionals configured, and a log line saying the seed is off.
A demo fixture that cannot be turned off is a demo fixture that will eventually run somewhere it
should not.

---

## Outcome

- **Run on:** 2026-08-22
- **Run by:** the maintainer, against the local Compose stack
- **Result:** **pass** — all 13 checks confirmed
- **Notes:** none recorded.

Two of the checks exist to test a judgement rather than a behaviour, and neither came back with an
objection, so both design trade-offs stand as chosen:

- **Check 6 — the hours table over a week grid.** No legibility complaint was raised, so the
  table stays. Design open question 2 is closed on that basis rather than on argument. If the
  table later proves hard to audit at more segments than one professional's week, that is the
  trigger to revisit — a grid would then need somewhere to put the effective period.
- **Check 9 — exceptions per professional.** No objection to the repetition, so the clinic-wide
  calendar stays out of scope (E4). The recorded revisit trigger is unchanged: the first real
  complaint about entering a holiday N times, or the first feature that must close the clinic
  without enumerating people.

Worth stating plainly: "no notes" means nothing was reported back, not that the two questions were
examined and found perfect. Both keep their revisit triggers for exactly that reason.
