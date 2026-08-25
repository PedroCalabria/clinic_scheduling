/**
 * Reading the API's instants on the clinic's clock.
 *
 * **Never `new Date(value).getHours()`.** That reads the BROWSER's zone, which is not the clinic's
 * — a patient travelling, a receptionist on a laptop set wrong, and every time on the page is off
 * by hours while everything else looks fine. The server says which zone it means and `Intl` is what
 * applies it, so the conversion happens once, here, against a value the response carries.
 *
 * **Shared rather than duplicated, and that is not a contradiction of design N4.** N4 refuses to
 * share the patient `SlotGrid` between the portal and the staff console, because those two screens
 * disagree about almost everything except their data. These functions are the data half: a `HH:mm`
 * has no audience, no layout and no behavioural switches, and "which clock is this" is exactly the
 * rule that must not have two answers. The portal's `slots.ts` re-exports them, so P2 and P6 read
 * as they did.
 */

/** The clinic-local parts of an instant. */
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

/** An instant's time on the clinic's clock, as `HH:mm`. */
export function clinicTime(instant: string, timeZone: string): string {
  return inZone(instant, timeZone).time;
}

/** An instant's date on the clinic's clock, as `yyyy-MM-dd`. */
export function clinicDate(instant: string, timeZone: string): string {
  return inZone(instant, timeZone).date;
}

/**
 * The offset an instant sits at, as `GMT-3`.
 *
 * Shown only to tell apart two times that read the same, which happens on the date a zone turns
 * its clock back. Both are real times an hour apart; showing them identically makes the screen look
 * broken, and hiding one loses an hour of genuinely bookable capacity.
 */
export function clinicOffset(instant: string, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone,
    timeZoneName: 'shortOffset',
  }).formatToParts(new Date(instant));

  return parts.find((part) => part.type === 'timeZoneName')?.value ?? '';
}

/** Today on the clinic's clock, as `yyyy-MM-dd`. */
export function clinicToday(timeZone: string): string {
  return inZone(new Date().toISOString(), timeZone).date;
}

/** `yyyy-MM-dd` a number of days from a date, without touching a zone. */
export function addDays(date: string, days: number): string {
  const value = new Date(`${date}T00:00:00Z`);

  value.setUTCDate(value.getUTCDate() + days);

  return value.toISOString().slice(0, 10);
}

/**
 * A `yyyy-MM-dd` written out for a reader — "quinta-feira, 3 de setembro".
 *
 * Formatted at UTC noon rather than midnight: a date string parsed as midnight UTC lands on the
 * previous day once a western zone is applied, which is the same class of bug as reading times in
 * the browser's zone.
 */
export function clinicDayLabel(date: string, timeZone: string, language: string): string {
  return new Intl.DateTimeFormat(language, {
    timeZone,
    weekday: 'long',
    day: 'numeric',
    month: 'long',
  }).format(new Date(`${date}T12:00:00Z`));
}

/**
 * An instant's date as a short, localised label — `30/08`.
 *
 * Exists for one job: saying which day a time belongs to when it is not the day on screen. A period
 * running from 08:05 on the 30th to 07:30 on the 31st renders as `08:05-07:30` on both days, which
 * reads as a period inside one day and is wrong by twenty-three hours.
 */
export function clinicShortDate(instant: string, timeZone: string, language: string): string {
  return new Intl.DateTimeFormat(language, {
    timeZone,
    day: '2-digit',
    month: '2-digit',
  }).format(new Date(instant));
}

/** How long an appointment runs, in whole minutes. */
export function minutesBetween(start: string, end: string): number {
  return Math.round((new Date(end).getTime() - new Date(start).getTime()) / 60_000);
}
