---
version: alpha
name: Consultório
description: Design system for a clinic scheduling platform — a warm public patient portal and a dense all-day staff console, built from one language.

colors:
  primary: "#006965"
  primary-strong: "#05504F"
  primary-subtle: "#DEF4F0"
  secondary: "#536064"
  tertiary: "#CA9246"
  tertiary-subtle: "#FAE9CF"
  neutral: "#F0F5F4"
  surface: "#FAFCFC"
  on-surface: "#1B2324"
  border: "#D9DFDF"
  error: "#A44033"

typography:
  display:
    fontFamily: IBM Plex Sans
    fontSize: 40px
    fontWeight: 600
    lineHeight: 1.1
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: IBM Plex Sans
    fontSize: 28px
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: -0.015em
  headline-md:
    fontFamily: IBM Plex Sans
    fontSize: 20px
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: -0.01em
  body-lg:
    fontFamily: IBM Plex Sans
    fontSize: 17px
    fontWeight: 400
    lineHeight: 1.6
  body-md:
    fontFamily: IBM Plex Sans
    fontSize: 16px
    fontWeight: 400
    lineHeight: 1.55
  body-sm:
    fontFamily: IBM Plex Sans
    fontSize: 14px
    fontWeight: 400
    lineHeight: 1.5
  label-md:
    fontFamily: IBM Plex Sans
    fontSize: 13px
    fontWeight: 600
    lineHeight: 1.4
  label-caps:
    fontFamily: IBM Plex Sans
    fontSize: 11px
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: 0.08em
  data-lg:
    fontFamily: IBM Plex Mono
    fontSize: 15px
    fontWeight: 400
    lineHeight: 1.4
    fontFeature: "'tnum' 1"
  data-md:
    fontFamily: IBM Plex Mono
    fontSize: 13px
    fontWeight: 400
    lineHeight: 1.45
    fontFeature: "'tnum' 1"
  data-sm:
    fontFamily: IBM Plex Mono
    fontSize: 11px
    fontWeight: 400
    lineHeight: 1.4
    letterSpacing: 0.02em
    fontFeature: "'tnum' 1"

rounded:
  none: 0px
  sm: 3px
  md: 6px
  full: 9999px

spacing:
  xs: 4px
  sm: 8px
  md: 12px
  lg: 20px
  xl: 32px
  gutter: 24px
  margin: 32px

components:
  page:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface}"
    typography: "{typography.body-md}"
  divider:
    backgroundColor: "{colors.border}"
    height: 1px
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.surface}"
    typography: "{typography.label-caps}"
    rounded: "{rounded.sm}"
    padding: "{spacing.md}"
  button-primary-hover:
    backgroundColor: "{colors.primary-strong}"
    textColor: "{colors.surface}"
  button-secondary:
    backgroundColor: "{colors.neutral}"
    textColor: "{colors.primary}"
    typography: "{typography.label-caps}"
    rounded: "{rounded.sm}"
    padding: "{spacing.md}"
  button-danger:
    backgroundColor: "{colors.error}"
    textColor: "{colors.surface}"
    typography: "{typography.label-caps}"
    rounded: "{rounded.sm}"
    padding: "{spacing.md}"
  slot-available:
    backgroundColor: "{colors.primary-subtle}"
    textColor: "{colors.primary-strong}"
    typography: "{typography.data-lg}"
    rounded: "{rounded.sm}"
    padding: "{spacing.sm}"
  status-scheduled:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.surface}"
    typography: "{typography.data-sm}"
    rounded: "{rounded.sm}"
    padding: "{spacing.xs}"
  status-conflict:
    backgroundColor: "{colors.tertiary}"
    textColor: "{colors.on-surface}"
    typography: "{typography.data-sm}"
    rounded: "{rounded.sm}"
    padding: "{spacing.xs}"
  banner-conflict:
    backgroundColor: "{colors.tertiary-subtle}"
    textColor: "{colors.on-surface}"
    typography: "{typography.body-sm}"
    rounded: "{rounded.md}"
    padding: "{spacing.md}"
  text-meta:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.secondary}"
    typography: "{typography.body-sm}"
  card:
    backgroundColor: "{colors.neutral}"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.md}"
    padding: "{spacing.lg}"
  input:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface}"
    typography: "{typography.body-md}"
    rounded: "{rounded.sm}"
    padding: "{spacing.sm}"
  input-error:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.error}"
    typography: "{typography.body-sm}"
  tooltip:
    backgroundColor: "{colors.on-surface}"
    textColor: "{colors.surface}"
    typography: "{typography.body-sm}"
    rounded: "{rounded.sm}"
    padding: "{spacing.sm}"
---

# Consultório

## Overview

Consultório is one design system serving two audiences who never overlap and want opposite things.

The **patient portal** is public, seen occasionally, and used by people who are anxious, sometimes elderly, often on a phone. Its whole job in UC-1 is to answer one question calmly: *when can I be seen?* — and to make booking feel safe and finished. The **staff console** is the opposite: front desk, professionals, and admins live in it for eight hours, scanning a day of appointments, resolving reconciliation conflicts, reading times and durations and counts at a glance. Density is a feature there, not a flaw.

The register is **Clinical Precision crossed with Soft Technical**. Clinical Precision supplies the discipline this domain demands — maximum legibility, exact structure, state never carried by color alone, redundant encoding for anything that matters. Soft Technical supplies the warmth that keeps a healthcare product from reading as sterile hospital equipment: tinted neutrals instead of clinical grey, comfortable body sizes, one confident color rather than none.

What this direction gives up, on purpose: **drama and novelty**. Consultório will never look surprising, and that is the point — in scheduling for a clinic, surprise costs trust, and a booking screen that tries to be clever is a booking screen a nervous patient distrusts. The system spends its one expressive gesture on a single sourced color and otherwise gets out of the way. It is boring and credible *by decision*, executed deliberately, not defaulted into.

There is no pure white and no pure black anywhere in the system, and there is exactly one warm color, reserved for exactly one situation.

## Colors

The primary is sourced from a real, specific place: **the green of a surgical suite**. Operating-room scrubs and drapes were deliberately changed from white to a blue-green in the early 20th century because it is the afterimage complement of oxygenated blood — it rests the surgeon's eye and raises contrast sensitivity during long procedures. That is the exact brief for this product: a color a clinic trusts, that survives an eight-hour shift without fatiguing the person staring at it. No color picker suggests `#006965`; the operating room does.

- **Primary (#006965):** *Surgical green, deep end.* The single interaction color — primary buttons, links, active navigation, focus, and the **Scheduled** appointment state. It is deliberately dark enough to be text-safe (6.4:1 on surface), so interaction never needs a second lighter variant. This is emphatically *not* Tailwind blue or teal-500.
- **Primary-strong (#05504F):** The pressed/hover value and the text color inside available-slot chips. Same hue, driven darker.
- **Primary-subtle (#DEF4F0):** A near-white tint of the green, used only as the fill of a **bookable slot** — the thing the patient is hunting for. A free slot glows faintly green in a field of neutral; a taken one does not.
- **Secondary (#536064):** *Instrument slate.* A cool grey with a trace of blue, for metadata — timestamps, durations, patient IDs, "booked by," counts. Recessive without vanishing (6.3:1).
- **Tertiary (#CA9246):** *Sodium signal.* A warm amber, the only warm color in an otherwise cool system, and it has exactly one job: **something needs a human**. It marks the reconciliation conflict — when a professional's external calendar collides with an existing appointment (UC-2's payoff, the human-in-the-loop case). Because it is the sole warm note against the green-and-slate field, a conflict is findable peripherally, across a busy console, without reading a word. Nothing else may use it.
- **Tertiary-subtle (#FAE9CF):** The amber at banner strength — the fill behind a conflict notice, carrying dark ink.
- **Neutral (#F0F5F4) and Surface (#FAFCFC):** *Clean-room paper.* Two near-whites a half-step apart, both tinted with a trace of the surgical-green hue (OKLCH hue ≈ 188). Almost the entire interface is built from the gap between these two. They read clean and exact — not the warm paper of a craft product, not the cold blue-slate of generic SaaS.
- **On-surface (#1B2324):** *Charcoal, cooled.* The near-black ink, carrying a faint trace of the same hue so text looks settled into the paper rather than stamped on top (15.5:1).
- **Error (#A44033):** *Brick.* A warm, muted red pulled toward the system rather than imported — a clinical stock red would look alien beside this green. Carries destructive actions (cancel), the **No-show** state, and input errors (6.1:1).

Every neutral is tinted (OKLCH hue 188–215, chroma 0.005–0.018); no value in the system has R = G = B. Ramps were built in OKLCH with chroma peaking mid-range and the hue bending slightly across each ramp, so the green behaves like a dyed material rather than a flat fill.

**Appointment-status mapping** (used across the console; every one is paired with an icon and a text label, never color alone): **Scheduled** → `primary` fill; **Completed** → `secondary`, recessive, with a check; **No-show** → `error`; **Cancelled** → `secondary` with strikethrough; **Rescheduled** → `secondary` with an arrow; **Conflict (needs resolution)** → `tertiary`.

## Typography

One superfamily, split by job: **IBM Plex Sans** for everything a human reads, **IBM Plex Mono** for everything a machine measured.

**IBM Plex Sans** was drawn by IBM for exactly this register — engineered legibility with a little warmth, no personality games — which is why it is the recommended face for clinical interfaces and the right call here over Inter (whose ubiquity would make the choice read as no choice). It carries display, headings, body, and labels. Patient-portal body runs a full **17px** (`body-lg`) because the real audience includes elderly users and the portal must clear WCAG 2.1 AA comfortably, not barely. Fallback: `"IBM Plex Sans", "Segoe UI", system-ui, sans-serif`. SIL Open Font License.

**IBM Plex Mono** carries measured data — appointment times, durations, patient and appointment IDs, occupancy counts, sync timestamps. Tabular figures are on everywhere (`'tnum' 1`) so numbers align in columns and a changing time doesn't shift the layout beside it. A time set in the mono against a label set in the sans is the console's core rhythm: the apparatus is visibly apparatus. Fallback: `"IBM Plex Mono", ui-monospace, "Cascadia Code", monospace`. SIL Open Font License.

The scale runs 11 → 40px on a ≈1.2 ratio (dense UI wants a tight scale, and this product is not a hero-driven marketing site — `display` at 40px is as loud as it gets). Tracking is optical: −0.02em at display, easing to neutral through body, +0.08em on uppercase labels. Line height moves inversely with size, 1.1 at display to 1.6 at body-lg. **Only two weights** are used — 400 and 600 — and no third is permitted; hierarchy comes from size, space, and the sans/mono split, not from a weight ladder. All faces are open-licensed and self-hosted; no third-party font CDN.

## Layout

A **12-column grid**, 24px gutters, 32px outer margins on desktop, collapsing to a single column below 768px. Body measure is capped at **68 characters** on the patient portal, where instructions and confirmations are read as sentences; the console is table-forward and not measure-bound.

Spacing runs on a **4px base**. The tight base is what lets the console pack a day of appointments into a screen without feeling cramped; the portal simply uses the larger steps (`lg`, `xl`) of the same scale. Nothing sits off the scale.

**Density is the primary wayfinding cue, and it is intentionally uneven between the two surfaces.** The patient portal breathes — generous vertical rhythm, one decision per screen, large touch targets (min 44px), the available-slot grid given room. The staff console tightens — 8px row padding, `data-md` in tables, persistent chrome (sidebar + day header + status bar). A user knows which world they are in before reading a word, from the density alone.

Layout is flush-left and asymmetric on the console (content left, a right rail for the day's conflicts and reminders); the portal centers its single booking column within generous margins, which is the one place centering is correct here — a lone focused task on a wide screen. Everything aligns to the grid without exception; a scheduling tool that looks even slightly misaligned reads as untrustworthy with time.

## Elevation & Depth

Consultório is nearly flat, because motion and float obscure the state changes that matter most in this domain (a slot going from free to taken, a conflict appearing). Depth is carried by three devices:

1. **Tonal layering** — the half-step between Surface (`#FAFCFC`) and Neutral (`#F0F5F4`). A card is a slightly different paper laid on the page, not a floating object.
2. **Hairline rules** — 1px in `border` (`#D9DFDF`), to divide rows and sections. Rules separate; they do not wrap content into boxes for decoration.
3. **Space** — the main grouping tool, per the 4px scale. Most apparent depth problems here are grouping problems; solve them with spacing first.

Genuinely floating layers — dropdowns, the reschedule dialog, tooltips, the date-picker popover — are the **only** things that get a shadow, and it is a single soft level: `0 4px 12px` in the on-surface hue at ~12% (`rgba(27,35,36,0.12)`), light coming from directly above, never neutral black. A resting card never carries it. Reserve elevation for things actually above other things.

## Shapes

The shape language is **near-square and hierarchical**. Radii are small and differ by element class rather than being uniform: inputs, buttons, chips, and status pills at `3px` (`sm`); cards, panels, dialogs, and banners at `6px` (`md`); and `9999px` (`full`) reserved strictly for avatars and the small status dot — never for containers. The restraint reads as precise and engineered, which is the trust posture the domain needs; a `rounded-2xl` everywhere would read as a consumer app playing doctor.

Borders are 1px solid `border` (`#D9DFDF`) at rest and 1px solid `primary` on an active/focused control. **Focus rings are a 2px outline in `primary` offset by 2px**, which clears 3:1 against both Surface and Neutral — focus visibility is non-negotiable for a keyboard-navigable AA portal. Border color and focus treatment are specified here in prose because the component schema has no `borderColor` sub-token; these values are normative regardless.

## Components

**Buttons.** Primary is a solid `primary` fill with Surface text, `label-caps` (uppercase, tracked, small). Secondary is a `neutral` fill with `primary` text. Danger is a solid `error` fill, used only for cancel/destructive confirmation. There is no ghost or tertiary button — a screen that needs a fourth action level has too many actions. Hover darkens the fill to `primary-strong`; no lift, no scale, no shadow change.

**Slot chips (`slot-available`).** The heart of UC-1. A bookable slot is a `primary-subtle` fill with `primary-strong` `data-lg` time text — the free slots read as a faint green field the patient scans. Taken/blocked times are plain neutral text with no fill; they are shown but visibly inert. Never render a slot's availability with color alone — free slots also carry the word/affordance "Reservar / Book," unavailable ones are non-interactive and labeled.

**Status tags (`status-scheduled`, `status-conflict`).** Small, `data-sm`, `sm` radius. `status-conflict` (amber) is the only element permitted to use `tertiary`, and every status tag pairs its color with an icon and a text label — redundant encoding is mandatory, since ~8% of men cannot rely on a red/green (or amber) distinction and this is a healthcare audience.

**Conflict banner (`banner-conflict`).** `tertiary-subtle` fill, dark ink, `md` radius, shown at the top of the front-desk queue when a reconciliation conflict is open. It states the collision plainly (appointment vs. external block) and offers the two human resolutions (reschedule / cancel) — the system supports the decision, it does not make it.

**Cards.** `neutral` fill, 6px radius, 20px padding, no border and no shadow — the tonal step alone separates them. A card means one discrete record (one appointment, one patient, one professional's day). Lists of fields inside a card are plain rows with hairline rules, not nested cards.

**Inputs.** Surface fill, 1px `border` rule, 3px radius, `body-md` at full size (inputs are often filled by an anxious patient on a phone — no shrunken UI type). Every field has a visible `label-md` label, never a placeholder standing in for one. Error state switches text and rule to `error` and is *always* accompanied by an inline message and an icon — color never carries the error alone.

**Data tables (console).** `data-md` with tabular figures, 8px row padding, hairline rules between rows, no zebra striping, no vertical rules. Times and durations right-align; names and identifiers left-align. `text-meta` (`secondary`) carries secondary column data.

**Tooltips.** `on-surface` fill, Surface text — the one deliberate inversion. 150ms delay in, none out. Never the sole carrier of essential information (AA), only supplementary.

## Do's and Don'ts

- **Do** keep `tertiary` (#CA9246) exclusively on the reconciliation-conflict signal. It is the only warm color in the system and its findability across a busy console depends entirely on scarcity — a second use anywhere destroys the one signal that matters most.
- **Don't** encode any state — availability, appointment status, error, conflict — with color alone. Always pair with an icon, a label, or a shape. This is a healthcare audience and an AA requirement, not a nicety.
- **Do** use `primary` (#006965) as the single interaction color. If a second thing on screen looks clickable in green, one of them is wrong.
- **Don't** introduce a resting shadow. Shadow is reserved for genuinely floating layers (dropdowns, dialogs, tooltips). A card, a slot, a table row never floats — depth comes from the Surface→Neutral tonal step and hairline rules.
- **Do** set every time, duration, ID, count, and timestamp in the mono (`data-*`) with tabular figures. Numbers in the sans will not align in columns and will jitter when they change — unacceptable in a schedule the front desk scans all day.
- **Don't** add a third font weight. Only 400 and 600 exist. If a heading isn't reading as a heading, give it more space above it, not more weight.
- **Do** keep the patient portal spacious (large targets, one decision per screen, 17px body) and let the staff console be dense. The density difference between the two surfaces is a feature — it is how a user knows which they are in.
- **Don't** center text on the console; it is flush-left throughout. Centering is allowed only for the patient portal's single booking column.
- **Do** cap portal body copy at 68 characters and give inputs real labels. Confirmations and instructions are read by anxious people; clarity is the product.
- **Don't** use pure `#FFFFFF` or `#000000`, including chart backgrounds, email templates, and PDF exports. Every neutral here is tinted toward the surgical-green hue; an untinted value beside them reads as a defect.
- **Do** treat the reconciliation conflict as a decision the system *presents*, never one it makes silently. The amber flag exists precisely because canceling a patient's appointment is a human call.
