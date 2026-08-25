import { clinicDate, clinicOffset, clinicTime } from '@clinic/shared';

/**
 * Turning the API's instants into something a patient can read (design B14).
 *
 * Kept out of the components because it is the one part of P2 that is genuinely easy to get
 * wrong, and it is worth being able to read it on its own.
 */

/** A slot as the availability response gives it. */
export interface Slot {
  professionalId: string;
  resourceId: string;
  start: string;
  end: string;
}

/** Slots that fall on one clinic day, in the order they occur. */
export interface SlotDay {
  /** `yyyy-MM-dd` in the clinic's zone — the grouping key, never displayed raw. */
  date: string;
  /** "quinta-feira, 3 de setembro" — already localised. */
  label: string;
  slots: Slot[];
}

/**
 * Re-exported from `@clinic/shared` rather than defined here.
 *
 * `booking-desk` needed the same conversions for the staff console, and "which clock is this
 * instant on" must not have two implementations — the failure mode is a screen that is wrong by
 * hours while looking fine. The grid itself is deliberately NOT shared (design N4); the arithmetic
 * under it is.
 */
export { clinicToday, addDays } from '@clinic/shared';

/** A slot's start on the clinic's clock, as `HH:mm`. */
export function slotTime(instant: string, timeZone: string): string {
  return clinicTime(instant, timeZone);
}

/** The offset a slot's instant sits at, as `GMT-3` — shown only to tell two equal times apart. */
export function slotOffset(instant: string, timeZone: string): string {
  return clinicOffset(instant, timeZone);
}

/**
 * Whether this slot shares its displayed time with another slot on the same day.
 *
 * The trigger for showing an offset, computed per day rather than assumed from the calendar: no
 * client-side DST table is involved, so it is right for every zone including ones whose rules
 * change.
 */
export function needsOffset(day: SlotDay, timeZone: string): Set<string> {
  const seen = new Map<string, number>();

  for (const slot of day.slots) {
    const time = slotTime(slot.start, timeZone);
    seen.set(time, (seen.get(time) ?? 0) + 1);
  }

  return new Set([...seen.entries()].filter(([, count]) => count > 1).map(([time]) => time));
}

/**
 * Groups slots by clinic day, ordered, with a localised heading per day.
 *
 * Grouped on the clinic's date rather than the instant's UTC date, because a 21:00 appointment in
 * São Paulo is midnight in UTC and would head up the wrong day.
 */
export function groupByDay(slots: Slot[], timeZone: string, language: string): SlotDay[] {
  const days = new Map<string, Slot[]>();

  for (const slot of slots) {
    const date = clinicDate(slot.start, timeZone);
    const existing = days.get(date);

    if (existing) {
      existing.push(slot);
    } else {
      days.set(date, [slot]);
    }
  }

  const heading = new Intl.DateTimeFormat(language, {
    timeZone,
    weekday: 'long',
    day: 'numeric',
    month: 'long',
  });

  return [...days.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([date, daySlots]) => ({
      date,
      // Formatted from the first slot's instant rather than from the date string: parsing
      // `2026-09-03` gives a UTC midnight that can land on the previous day once a zone is
      // applied, which is the same class of bug as reading times in the browser's zone.
      label: heading.format(new Date(daySlots[0].start)),
      slots: [...daySlots].sort((a, b) => a.start.localeCompare(b.start)),
    }));
}

