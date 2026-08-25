import { Button } from '@clinic/shared';
import { useTranslation } from 'react-i18next';
import { slotTime, type Slot } from './slots';

/**
 * What has been chosen, and the only control that leaves the page (design D8).
 *
 * **Why choosing is a step rather than a navigation.** Clicking a slot used to go straight to P3.
 * That is fine for a patient who has already decided and bad for one comparing Tuesday against
 * Wednesday, which is most of them. It also makes the just-taken recovery gentler: a refusal
 * returns to a page that still knows what was being attempted.
 *
 * **Sticky on desktop, inline on mobile.** With a wide window the day list is long, and a choice
 * made at the top would otherwise scroll out of reach; on a phone a pinned bar costs vertical space
 * that matters more. Recorded as design Open Question 2 — the validation guide asks a human whether
 * this is right, because nobody has seen it.
 *
 * Shared by P2 and P6 for the reason `SlotGrid` is: `booking-lifecycle` extracted the grid so the
 * reschedule screen would not fork it, and a selection model in only one of them would fork it
 * after all.
 */
export function SelectionBar({
  slot,
  timezone,
  professional,
  appointmentType,
  actionLabel,
  pending,
  onContinue,
}: {
  /** Null until something is chosen — the bar renders nothing at all. */
  slot: Slot | null;
  timezone: string;
  professional: string | undefined;
  appointmentType: string | undefined;
  actionLabel: string;
  pending?: boolean;
  onContinue: () => void;
}) {
  const { t, i18n } = useTranslation();

  // Nothing chosen, nothing shown. The control that proceeds does not exist yet rather than
  // existing disabled: there is no choice to explain the disabling of.
  if (!slot) {
    return null;
  }

  // Derived from the slot rather than read from the appointment type, which does not carry one on
  // the wire. Better as well as necessary: this is the length of THIS appointment, which is what
  // the professional's configured duration produced at the moment it was offered.
  const durationMinutes = Math.round(
    (new Date(slot.end).getTime() - new Date(slot.start).getTime()) / 60000,
  );

  const day = new Intl.DateTimeFormat(i18n.language, {
    timeZone: timezone,
    weekday: 'long',
    day: 'numeric',
    month: 'long',
  }).format(new Date(slot.start));

  return (
    <div
      className={[
        // `bottom-4`, not `bottom-0`: pinned to the bottom of the viewport but standing off it, so
        // the bar reads as floating above the page rather than welded to the window edge. Flush
        // against the edge also clipped the one shadow this design system has — the DS gives a
        // floating layer exactly one elevation level, and a bar with its shadow cut off looks like
        // a rendering fault rather than a raised surface.
        'sticky bottom-4 z-10 -mx-1 mt-2 mb-4 rounded-md border border-primary bg-surface p-4 shadow-float',
        'flex flex-wrap items-center justify-between gap-4',
      ].join(' ')}
    >
      <div className="min-w-0">
        <p className="text-[11px] font-semibold uppercase tracking-[0.08em] text-meta">
          {t('booking.selectedLabel')}
        </p>

        {/*
          The choice restated in full before it is committed — professional, kind of visit, how
          long, and when. Numbers in mono like everywhere else on this surface.
        */}
        <p className="mt-0.5 text-heading">
          <span className="font-mono tabular-nums">
            {day}, {slotTime(slot.start, timezone)}
          </span>
          {appointmentType ? <span> · {appointmentType}</span> : null}
          <span className="font-mono tabular-nums">
            {' · '}
            {t('booking.minutes', { count: durationMinutes })}
          </span>
          {professional ? <span> · {professional}</span> : null}
        </p>
      </div>

      <Button onClick={onContinue} disabled={pending}>
        {pending ? t('booking.confirming') : actionLabel}
      </Button>
    </div>
  );
}
