import { needsOffset, slotOffset, slotTime, type Slot, type SlotDay } from './slots';

/** Just enough of a professional to label a button. */
export interface SlotProfessional {
  professionalId: string;
  displayName: string;
}

/**
 * The free times, grouped by day — P2's results, and P6's (docs/06-ui-surfaces.md §P6).
 *
 * **Extracted rather than copied, which is the whole point.** `booking-lifecycle` needs the same
 * grid on the reschedule screen, and two copies of a slot renderer is two places for the
 * fall-back-day disambiguation to be got right in only one of them. P6 differs from P2 in exactly
 * one respect — it never shows a professional's name, because there is only ever one — and that is
 * a prop rather than a fork.
 *
 * Everything subtle here belongs to `booking-core` and is unchanged: times are the clinic's wall
 * clock converted from instants using the zone the response carries, never the browser's; and where
 * one day contains two slots reading the same local time, both are shown with their offsets rather
 * than one being hidden.
 */
export function SlotGrid({
  days,
  timezone,
  professionals,
  showProfessional,
  onChoose,
}: {
  days: SlotDay[];
  timezone: string;
  professionals: SlotProfessional[];
  showProfessional: boolean;
  onChoose: (slot: Slot) => void;
}) {
  return (
    <>
      {days.map((day) => {
        const ambiguous = needsOffset(day, timezone);

        return (
          <div key={day.date} className="space-y-3">
            <h2 className="text-sm font-semibold uppercase tracking-wide text-meta">{day.label}</h2>

            <div className="flex flex-wrap gap-2">
              {day.slots.map((slot) => {
                const time = slotTime(slot.start, timezone);
                const who = professionals.find(
                  (entry) => entry.professionalId === slot.professionalId,
                );

                return (
                  <button
                    key={`${slot.start}-${slot.professionalId}`}
                    type="button"
                    onClick={() => onChoose(slot)}
                    className="min-w-28 rounded-lg border border-line bg-surface px-4 py-3 text-left transition hover:border-accent hover:bg-surface-raised focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
                  >
                    <span className="block font-semibold text-heading">
                      {time}
                      {/*
                        Only where the same local time occurs twice on this day — the fall-back
                        case. Two real instants an hour apart, told apart rather than one hidden.
                      */}
                      {ambiguous.has(time) ? (
                        <span className="ml-1 text-xs font-normal text-meta">
                          {slotOffset(slot.start, timezone)}
                        </span>
                      ) : null}
                    </span>
                    {/*
                      Shown only in any-professional mode. On P6 it is never shown at all: a
                      reschedule keeps the same professional, so repeating their name on forty
                      buttons would be noise about the one thing that cannot change.
                    */}
                    {showProfessional && who ? (
                      <span className="mt-0.5 block text-xs text-meta">{who.displayName}</span>
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
              <div key={slot} className="h-16 w-28 animate-pulse rounded-lg bg-surface-raised" />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
