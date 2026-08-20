# Consultório — Design System

**Version:** alpha · **Domain:** clinic scheduling

Consultório is one design language serving two audiences who never overlap and want opposite things:

- **Patient portal** — public, used occasionally, by people who are anxious, sometimes elderly, often on a phone. Its job is to answer one question calmly: *when can I be seen?*
- **Staff console** — front desk, professionals and admins live in it for eight hours: scanning a day of appointments, resolving reconciliation conflicts, reading times, durations and counts at a glance. Density is a feature here.

The register is **Clinical Precision crossed with Soft Technical**: maximum legibility, exact structure, state never carried by color alone — with tinted neutrals and comfortable body sizes so it never reads as sterile hospital equipment. What it gives up on purpose is drama and novelty. In clinic scheduling, surprise costs trust.

Two absolutes: **there is no pure white and no pure black anywhere**, and **there is exactly one warm color, reserved for exactly one situation** (the reconciliation conflict).

## Sources

- `uploads/DESIGN.md` — the complete brand and token specification this system was built from (token schema in front-matter, prose guidance below). It is the ground truth; where this readme and DESIGN.md differ, DESIGN.md wins.
- No codebase, Figma file, screenshots, deck, logo files or font binaries were provided. See **Gaps & substitutions**.

## Index

| Path | What it is |
| --- | --- |
| `styles.css` | Global CSS entry — `@import` list only. Consumers link this one file. |
| `tokens/` | `fonts.css`, `colors.css`, `typography.css`, `spacing.css`, `shape.css`, `elevation.css`, `base.css` |
| `components/` | React primitives, grouped by concern (`core`, `forms`, `scheduling`, `data`, `feedback`) |
| `guidelines/` | Foundation specimen cards (colors, type, spacing, brand) |
| `ui_kits/patient-portal/` | Click-through booking flow recreation |
| `ui_kits/staff-console/` | Click-through day-view console recreation |
| `assets/` | Brand assets — currently empty; no logo was supplied |
| `SKILL.md` | Agent Skills entry point |
| `thumbnail.html` | Homepage tile |

## Components

Inventory follows the `components:` schema in DESIGN.md — nothing beyond it.

| Component | Group | Source token entry |
| --- | --- | --- |
| `Button` | core | `button-primary`, `button-secondary`, `button-danger` |
| `Card` | core | `card` |
| `Divider` | core | `divider` |
| `MetaText` | core | `text-meta` |
| `Icon` | core | *intentional addition* |
| `Input` | forms | `input`, `input-error` |
| `SlotChip` | scheduling | `slot-available` |
| `StatusTag` | scheduling | `status-scheduled`, `status-conflict` (+ four prose states) |
| `ConflictBanner` | scheduling | `banner-conflict` |
| `DataTable` | data | "Data tables (console)" prose section |
| `Tooltip` | feedback | `tooltip` |

Each directory holds `<Name>.jsx`, `<Name>.d.ts`, `<Name>.prompt.md` and one `@dsCard` HTML showing its states.

**Intentional additions**
- `Icon` — the guide mandates icon + label redundancy on every state but ships no icon set. `Icon` wraps Lucide (loaded from CDN) so that rule is actually enforceable. No other primitive was invented: there is no Toast, Avatar, Tabs, Select, Switch or Dialog component, because the source defines none.

## Content fundamentals

Copy is **Brazilian Portuguese**, plain and second-person, and it is written to be read by someone slightly nervous.

- **Voice: "you", never "we-as-institution."** "Escolha um horário disponível." / "Enviamos a confirmação por SMS." The clinic speaks only when it is doing something for the patient.
- **Sentence case everywhere.** The single exception is `label-caps` — button labels and column headers set in uppercase with +0.08em tracking ("RESERVAR HORÁRIO", "HORÁRIO", "PACIENTE"). Never uppercase a sentence.
- **State the fact, then the consequence.** "Horário reservado. Enviamos a confirmação por SMS." Not "Success!" and not "Your appointment has been successfully created."
- **Errors name the fix, not the failure.** "Informe um CPF válido, com 11 dígitos." Never "Invalid input" and never blame ("Você digitou errado").
- **Conflicts are described, never decided.** "A agenda externa da Dra. Helena bloqueia um horário já reservado." followed by the two human options — *Reagendar* / *Cancelar*. The product never says it resolved something on its own.
- **Numbers are literal and always mono.** "30 min", "14 consultas", "ocupação 87%", "#AP-20418", "SINCRONIZADO 08:12". No approximations ("cerca de meia hora"), no rounding.
- **Console copy is telegraphic; portal copy is sentences.** The console labels ("Fila da recepção", "Reconciliação", "2 AGENDAS EXTERNAS"); the portal explains ("Precisamos apenas do necessário para reservar o horário no seu nome."). Portal prose is capped at 68 characters of measure.
- **No emoji, ever.** No exclamation marks in system copy, no encouragement, no humor. Nothing celebratory around a medical appointment.
- **Vocabulary is fixed:** *consulta* (never "sessão"), *horário* (never "slot" in user-facing copy), *profissional*, *reagendar*, *cancelar*, *conflito*, *reconciliação*, *sala*.

## Visual foundations

**Color.** One sourced primary — `#006965`, the blue-green of a surgical suite, chosen because it rests the eye across a long shift and is dark enough (6.4:1 on surface) to be text-safe, so interaction never needs a lighter variant. It is the *only* interaction color: if two things on a screen look clickable in green, one is wrong. `#05504F` is hover/pressed. `#DEF4F0` fills nothing but a bookable slot. `#536064` slate carries all metadata. `#CA9246` amber is the sole warm note and marks one thing only: a reconciliation conflict needing a human. `#A44033` brick carries destructive actions, no-shows, and input errors. Neutrals are two tinted near-whites a half-step apart (`#FAFCFC` surface, `#F0F5F4` neutral), ink is `#1B2324`. Every neutral is tinted toward OKLCH hue ≈188–215; no value has R=G=B, and untinted `#FFFFFF`/`#000000` are defects — including in charts, emails and PDF exports.

**Type.** One superfamily split by job: **IBM Plex Sans** for anything a human reads, **IBM Plex Mono** for anything a machine measured (times, durations, IDs, counts, timestamps) with `'tnum' 1` always on. Scale 11 → 40px on ≈1.2 ratio. Only two weights exist, 400 and 600 — a heading that isn't reading as a heading gets more space above it, not more weight. Tracking is optical: −0.02em at display, neutral through body, +0.08em on uppercase labels. Portal body runs a full 17px.

**Spacing & layout.** 4px base (4/8/12/20/32), 12-column grid, 24px gutters, 32px outer margins, single column below 768px. Density is the wayfinding cue and is deliberately uneven: the portal breathes (44px targets, one decision per screen), the console tightens (8px rows, persistent sidebar + day header + status bar). Console layout is flush-left and asymmetric with a right rail; the portal centers its single booking column — the one place centering is correct.

**Backgrounds.** Flat tinted paper, nothing else. No photography, no illustration, no gradients, no textures, no patterns, no full-bleed imagery. Nearly the whole interface is built from the half-step between `#FAFCFC` and `#F0F5F4`. No imagery direction exists yet because no imagery was supplied — if photography is added later it should be cool-neutral and undramatic, and it must be specified before it is used.

**Elevation.** Nearly flat, because float obscures the state changes that matter (a slot going from free to taken, a conflict appearing). Depth comes from tonal layering, 1px hairline rules, and space — in that order. Only genuinely floating layers get a shadow, and there is one level: `0 4px 12px rgba(27,35,36,0.12)`, light from directly above, never neutral black. A resting card never carries it.

**Shape.** Near-square and hierarchical: 3px on inputs, buttons, chips and status pills; 6px on cards, panels, dialogs and banners; `9999px` reserved strictly for avatars and the status dot, never a container. Borders are 1px `#D9DFDF` at rest, 1px `#006965` when active/focused.

**Transparency & blur.** None. No glassmorphism, no scrim blurs, no translucent panels, no protection gradients — text always sits on a solid tinted surface, so nothing needs protecting. Dialog backdrops, if added, should be a flat `rgba(27,35,36,0.35)` wash rather than a blur.

**Motion.** Restrained and functional: 120ms for hover/fill changes, 180ms for panel and dialog entrance, easing `cubic-bezier(0.2,0,0.2,1)`. Fades and small opacity/fill changes only — no bounce, no spring, no slide-in from off-canvas, no scale on press, no skeleton shimmer. A state change must be legible, not choreographed. Tooltips are the one timed behavior: 150ms delay in, none out.

**Hover / press / focus / disabled.** Hover *darkens* the fill toward `primary-strong` (secondary buttons go from neutral to `primary-subtle`); nothing lifts, scales or gains a shadow. Press is the same darker fill, no transform. Focus is a **2px `#006965` outline offset 2px** — non-negotiable, and it clears 3:1 on both neutrals. Disabled is a neutral fill with slate ink at 0.6 opacity plus `not-allowed`. Table rows highlight by tonal step, never by border.

**Cards.** Neutral fill, 6px radius, 20px padding, **no border and no shadow** — the tonal step alone is the edge. A card is one discrete record. Field lists inside a card are plain rows with hairline rules, never nested cards.

**Fixed layout elements.** Console: sidebar (232px), day header, and status bar are persistent; only the table and rail scroll. Portal: nothing is sticky — the page is short by design.

## Iconography

- **Set:** **Lucide** at 1.75px stroke, loaded from CDN (`https://unpkg.com/lucide@0.544.0/dist/umd/lucide.js`) and rendered through the `Icon` component. **This is a flagged substitution** — the source supplied no icon set, sprite, icon font or SVGs. Lucide was chosen for its even single-weight stroke, which matches the system's hairline rules and lack of fill.
- **Sizes:** 12 inside `data-sm` status tags, 14 in buttons and inline errors, 16 in body/label contexts and nav, 18–20 in headers and banners. Never above 24 — there is no decorative iconography in this system.
- **Color:** `currentColor` by default so glyphs inherit their text context. The only deliberate colored glyphs are the amber `alert-triangle` (conflict) and the brick `user-x`/`alert-circle` (no-show, error).
- **Semantic set in use:** `calendar-check` (scheduled/booked), `calendar-clock` (reschedule), `clock` (queue), `check` (completed), `ban` (cancelled), `arrow-right` (rescheduled / continue), `alert-triangle` (conflict), `alert-circle` (input error), `user-x` (no-show), `user` (patients), `refresh-cw` (sync), `search`, `filter`, `plus`, `x`, `phone`, `chevron-left`/`chevron-right`.
- **Every state icon is mandatory, not decorative.** Redundant encoding (color + icon + word) is an accessibility requirement here: roughly 8% of men cannot rely on an amber/green distinction, and this is a healthcare audience.
- **No emoji, no unicode glyphs standing in for icons, no PNG icons.** No logo mark exists, so no icon derives from one.

## Gaps & substitutions

1. **No logo.** No brand mark, wordmark file, or favicon was supplied. Nothing was drawn: wherever a mark would go, the name "Consultório" is set in IBM Plex Sans 600 (see the Wordmark card, `thumbnail.html`, and both UI kit headers). `assets/` stays empty until real files arrive.
2. **Fonts are CDN, not self-hosted.** DESIGN.md requires self-hosted, open-licensed faces with no third-party font CDN. No binaries were provided, so `tokens/fonts.css` currently `@import`s IBM Plex Sans + Mono from Google Fonts. The families are correct; only the hosting is wrong. Drop `.woff2` files into `assets/fonts/` and swap the `@import` for local `@font-face` rules.
3. **Icons are Lucide, substituted** (see Iconography).
4. **No imagery, illustration, or photography direction** — none was supplied, and none was invented.
5. **UI kits are constructed from the specification, not recreated from a product.** No codebase, Figma file, or screenshot of the real Consultório product was available. Screens follow the layout, density, and component rules stated in DESIGN.md (UC-1 booking, UC-2 reconciliation conflict), with plausible Portuguese sample data. They should be checked against the real product before being treated as ground truth.
