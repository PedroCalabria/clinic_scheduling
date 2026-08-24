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
 * The clinic-local parts of an instant.
 *
 * **Never `new Date(value).getHours()`.** That reads the BROWSER's zone, and the browser's zone is
 * not the clinic's — a patient travelling, or simply a laptop set wrong, would be shown times that
 * are off by hours while everything else on the page looked fine. The server tells us which zone it
 * means and `Intl` is what applies it, so the conversion happens once, here, against a value the
 * response carries.
 */
function inZone(instant: string, timeZone: string): { date: string; time: string } {
  const value = new Date(instant);

  const date = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(value);

  const time = new Intl.DateTimeFormat('en-GB', {
    timeZone,
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(value);

  return { date, time };
}

/** A slot's start on the clinic's clock, as `HH:mm`. */
export function slotTime(instant: string, timeZone: string): string {
  return inZone(instant, timeZone).time;
}

/**
 * The offset a slot's instant sits at, as `GMT-3`.
 *
 * Shown only when a day contains two slots reading the same local time, which happens on the date
 * a zone turns its clock back. Both are real, distinct times an hour apart; hiding one would lose
 * an hour of genuinely bookable capacity, and showing them identically would make the screen look
 * broken. This is change 4's open question 4, answered on the surface that created it.
 */
export function slotOffset(instant: string, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-GB', { timeZone, timeZoneName: 'shortOffset' })
    .formatToParts(new Date(instant));

  return parts.find((part) => part.type === 'timeZoneName')?.value ?? '';
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
    const { date } = inZone(slot.start, timeZone);
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

/** Today in the clinic's zone, as `yyyy-MM-dd` — the earliest date worth searching. */
export function clinicToday(timeZone: string): string {
  return inZone(new Date().toISOString(), timeZone).date;
}

/** `yyyy-MM-dd` a number of days after a date, without touching a zone. */
export function addDays(date: string, days: number): string {
  const value = new Date(`${date}T00:00:00Z`);

  value.setUTCDate(value.getUTCDate() + days);

  return value.toISOString().slice(0, 10);
}
