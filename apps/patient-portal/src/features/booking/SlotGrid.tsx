import { useTranslation } from 'react-i18next';
import { needsOffset, slotOffset, slotTime, type Slot, type SlotDay } from './slots';

/** Just enough of a professional to label a button. */
export interface SlotProfessional {
  professionalId: string;
  displayName: string;
}

/** Identifies one offered slot. Two professionals can offer the same instant. */
export function slotKey(slot: Pick<Slot, 'start' | 'professionalId'>): string {
  return `${slot.start}-${slot.professionalId}`;
}

/**
 * Tile geometry, in pixels, because "two rows" has to be an exact number to cap a day at.
 *
 * The tile's height is FIXED rather than `min-h`, and that is what makes the cap below honest: a
 * content-sized tile makes a row height that depends on whether a professional's name is shown and
 * whether a time wrapped, and a max-height guessed against that shows a sliver of a third row —
 * which looks like a rendering fault rather than a scroll region.
 *
 * The arithmetic, so a later reader can change a line height without breaking the cap:
 *   padding (p-2)          8 + 8 = 16
 *   time     (leading-5)          20
 *   gap      (gap-0.5)             2
 *   label    (leading-4)          16   -> 54 without a professional's name
 *   gap + professional              2 + 16 -> 72 with one
 */
const TILE_HEIGHT = { plain: 54, withProfessional: 72 } as const;

/** `gap-2` between tiles, in both directions. */
const TILE_GAP = 8;

/**
 * How many rows of times a single day shows before it scrolls inside itself.
 *
 * A wide window on a busy professional produces days of twenty slots, and stacked those push every
 * later day off the screen — so the shape of the week, which is what a patient is actually
 * scanning for, is lost to the first day that happens to be busy. Capping each day keeps every day
 * reachable and makes "this one has a lot" visible as a scrollbar rather than as a wall.
 *
 * No `tabIndex` on the scroll region: every child is a focusable button, so keyboard users reach
 * the hidden rows by tabbing and the browser scrolls to them. Adding one would create a redundant
 * tab stop before every day.
 */
const MAX_ROWS = 2;

/**
 * The free times, grouped by day — P2's results, and P6's (docs/06-ui-surfaces.md §P2, §P6).
 *
 * **Extracted rather than copied, which is the whole point.** `booking-lifecycle` needed the same
 * grid on the reschedule screen, and two copies of a slot renderer is two places for the
 * fall-back-day disambiguation to be got right in only one of them. `booking-surface` then added a
 * selected state, which would have been two places to get *that* right as well.
 *
 * **The tile follows the design system's own `SlotChip`** rather than an interpretation of it
 * (design D2, D3). Read out of `design/_ds/…/_ds_bundle.js`, so these are its decisions and not
 * this file's:
 *
 * - resting fill is `--surface-slot-free`, which resolves to `--green-100` — the `#DEF4F0` the
 *   guide says *"fills nothing but a bookable slot"*. The portal had been painting slots
 *   `surface`, which is why the grid read as a list of buttons;
 * - the selected tile is solid `--color-primary`, the only full-primary fill on the page;
 * - **no border while free** — the fill does the work, and a border would fight it;
 * - the time is `type-data-lg`: mono, 15px, `tnum`. Tabular figures are what make a column of
 *   times align, and a column that does not align cannot be scanned;
 * - the label under it is `type-label-caps`: 11px, 600, uppercase, +0.08em;
 * - 44px minimum height, because `06 §2` names elderly users as part of this audience.
 *
 * **State is never carried by colour alone** (a DS absolute), so the chosen tile also carries
 * `aria-pressed` — which is what the DS component does, and what a screen-reader user has instead
 * of the fill.
 */
export function SlotGrid({
  days,
  timezone,
  professionals,
  showProfessional,
  selected,
  onChoose,
}: {
  days: SlotDay[];
  timezone: string;
  professionals: SlotProfessional[];
  showProfessional: boolean;
  /** The chosen slot's key, or null. Exactly one at a time — see `booking-surface` design D8. */
  selected: string | null;
  onChoose: (slot: Slot) => void;
}) {
  const { t } = useTranslation();

  const tileHeight = showProfessional ? TILE_HEIGHT.withProfessional : TILE_HEIGHT.plain;

  // Exactly MAX_ROWS rows and the gaps between them — no partial row, which is the difference
  // between "there is more below" and "something is broken".
  const maxHeight = MAX_ROWS * tileHeight + (MAX_ROWS - 1) * TILE_GAP;

  return (
    <>
      {days.map((day) => {
        const ambiguous = needsOffset(day, timezone);

        return (
          <div key={day.date} className="space-y-3">
            <div className="flex flex-wrap items-baseline gap-x-3">
              <h2 className="text-[11px] font-semibold uppercase tracking-[0.08em] text-heading">
                {day.label}
              </h2>
              {/*
                The day's count, in mono like every other number. No room is named here, and that
                is deliberate: the artboard puts one on this line, and a patient is never told
                which room (design D7). A day's slots are not served by one room in any case.
              */}
              <span className="font-mono text-[11px] tabular-nums tracking-[0.02em] text-meta">
                {t('booking.dayFree', { count: day.slots.length })}
              </span>
            </div>

            {/*
              Padding inside the scroll box and a matching negative margin outside it: a focus ring
              is drawn with `outline-offset-2`, and on the first or last row it would be clipped by
              `overflow-y-auto` without the room. The negative margin keeps the grid aligned with
              the day heading above it.
            */}
            <div
              className="-m-1 flex flex-wrap gap-2 overflow-y-auto p-1"
              style={{ maxHeight: maxHeight + 8 }}
            >
              {day.slots.map((slot) => {
                const time = slotTime(slot.start, timezone);
                const key = slotKey(slot);
                const isSelected = selected === key;
                const who = professionals.find(
                  (entry) => entry.professionalId === slot.professionalId,
                );

                return (
                  <button
                    key={key}
                    type="button"
                    aria-pressed={isSelected}
                    onClick={() => onChoose(slot)}
                    style={{ height: tileHeight }}
                    className={[
                      'flex min-w-[92px] shrink-0 flex-col items-start gap-0.5 rounded-sm p-2 text-left',
                      'transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent',
                      isSelected
                        ? 'bg-primary text-on-primary'
                        : 'bg-primary-subtle text-heading hover:bg-primary-subtle/70',
                    ].join(' ')}
                  >
                    {/*
                      `whitespace-nowrap` matters here rather than being tidiness: on a fall-back
                      day the offset is appended inline, and a wrapped time would make this tile
                      taller than TILE_HEIGHT and silently break the two-row cap.
                    */}
                    <span className="font-mono text-[15px] leading-5 tabular-nums whitespace-nowrap">
                      {time}
                      {/*
                        Only where the same local time occurs twice on this day — the fall-back
                        case. Two real instants an hour apart, told apart rather than one hidden.
                      */}
                      {ambiguous.has(time) ? (
                        <span className="ml-1 text-[11px]">{slotOffset(slot.start, timezone)}</span>
                      ) : null}
                    </span>

                    <span
                      className={[
                        'text-[11px] leading-4 font-semibold uppercase tracking-[0.08em]',
                        isSelected ? 'text-on-primary' : 'text-primary',
                      ].join(' ')}
                    >
                      {isSelected ? t('booking.slotChosen') : t('booking.slotBook')}
                    </span>

                    {/*
                      Shown only in any-professional mode. On P6 it is never shown at all: a
                      reschedule keeps the same professional, so repeating their name on forty
                      buttons would be noise about the one thing that cannot change.
                    */}
                    {showProfessional && who ? (
                      <span
                        className={[
                          'text-[11px] leading-4 truncate max-w-[136px]',
                          isSelected ? 'text-on-primary/80' : 'text-meta',
                        ].join(' ')}
                      >
                        {who.displayName}
                      </span>
                    ) : null}
                  </button>
                );
              })}
            </div>
          </div>
        );
      })}
    </>
  );
}

/**
 * The loading state, shaped like the answer.
 *
 * `aria-busy` and a visually-hidden status line, so the wait is announced to a screen reader
 * without a decorative shimmer being described to anybody.
 */
export function SlotSkeleton({ label }: { label: string }) {
  return (
    <div aria-busy="true" className="space-y-6">
      <span className="sr-only" role="status">
        {label}
      </span>

      {[0, 1].map((group) => (
        <div key={group} className="space-y-3">
          <div className="h-3 w-40 animate-pulse rounded bg-surface-raised" />
          <div className="flex flex-wrap gap-2">
            {[0, 1, 2, 3, 4, 5].map((slot) => (
              <div
                key={slot}
                // Same constant as a real tile, so the page does not jolt when the answer lands.
                style={{ height: TILE_HEIGHT.plain }}
                className="w-[92px] animate-pulse rounded-sm bg-surface-raised"
              />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
