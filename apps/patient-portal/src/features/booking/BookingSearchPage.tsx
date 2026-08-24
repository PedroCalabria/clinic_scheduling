import {
  Alert,
  Button,
  Field,
  Input,
  RadioGroup,
  Select,
  getAvailability,
  getBookingOptions,
  useApiCodeMessage,
  useApiErrorMessage,
} from '@clinic/shared';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { SelectionBar } from './SelectionBar';
import { SlotGrid, SlotSkeleton, slotKey } from './SlotGrid';
import { addDays, clinicToday, groupByDay } from './slots';

/** How wide a window the date pickers default to. A fortnight fills the screen without flooding it. */
const DEFAULT_WINDOW_DAYS = 13;

/**
 * The query parameter a P3 refusal comes back on, so the "just taken" state is a place a patient
 * can be sent to rather than state that only exists inside one component.
 */
export const TAKEN_CODE_PARAM = 'unavailable';

/** And which slot it was, so it can be removed from the list rather than merely mentioned. */
export const TAKEN_SLOT_PARAM = 'unavailableSlot';

/**
 * P2 — the booking search (docs/06-ui-surfaces.md, design B14).
 *
 * The screen this project exists to make possible: only genuinely free times, computed against a
 * professional's hours, their blocked time, other people's appointments, and whether a room of the
 * right kind is free. Everything the three other capabilities built shows up here or nowhere.
 *
 * Three decisions worth knowing before reading the code:
 *
 * 1. **The search IS the URL.** Specialty, kind of visit, professional and window are query
 *    parameters, so a reload keeps the results, the back button from P3 returns to them, and a
 *    patient can send someone a link. It costs nothing and is most of the difference between a demo
 *    and a product.
 * 2. **Nothing is ever cached.** Availability is uncached by decision (Decision S) and the API says
 *    `no-store`; the client honours it with `staleTime: 0` and refetches on focus and reconnect. A
 *    stale slot is a slot that is already gone, and this is the first place TanStack Query is doing
 *    the job it was chosen for rather than wrapping a fetch.
 * 3. **Five states, all designed.** Results, loading, empty (a success — nothing free in THIS
 *    window), error, and just-taken. The last one is the whole point of optimistic booking: losing
 *    a race has to feel like an ordinary answer, not like a fault.
 */
export function BookingSearchPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const describeError = useApiErrorMessage();
  const describeCode = useApiCodeMessage();
  const [params, setParams] = useSearchParams();

  const options = useQuery({ queryKey: ['booking-options'], queryFn: getBookingOptions, retry: false });

  const specialties = options.data?.specialties ?? [];

  // The selection, read from the URL and defaulted forward: choosing a specialty implies the first
  // kind of visit in it, which means the screen is never in a half-chosen state a patient has to
  // finish before seeing anything.
  const specialtyId = params.get('specialty') ?? specialties[0]?.specialtyId ?? '';
  const specialty = specialties.find((entry) => entry.specialtyId === specialtyId) ?? specialties[0];

  const appointmentTypeId = params.get('type') ?? specialty?.appointmentTypes[0]?.appointmentTypeId ?? '';
  const appointmentType =
    specialty?.appointmentTypes.find((entry) => entry.appointmentTypeId === appointmentTypeId)
    ?? specialty?.appointmentTypes[0];

  // Empty means "any professional of this specialty" — the mode the domain model treats as
  // primary, and the one that makes the union across professionals visible.
  const professionalId = params.get('professional') ?? '';

  const from = params.get('from') || clinicToday('UTC');
  const to = params.get('to') || addDays(from, DEFAULT_WINDOW_DAYS);

  const takenCode = params.get(TAKEN_CODE_PARAM);
  const takenSlot = params.get(TAKEN_SLOT_PARAM);

  const ready = Boolean(appointmentType);

  const availability = useQuery({
    queryKey: ['availability', appointmentType?.appointmentTypeId, professionalId, from, to],
    queryFn: () =>
      getAvailability({
        appointmentTypeId: appointmentType!.appointmentTypeId,
        from,
        to,
        professionalId: professionalId || undefined,
      }),
    enabled: ready,
    retry: false,

    // Volatile server state, deliberately never fresh. The API sends no-store; anything else here
    // would be the client undoing a decision the server made on purpose.
    staleTime: 0,
    gcTime: 0,
    refetchOnWindowFocus: true,
    refetchOnReconnect: true,
  });

  const timezone = availability.data?.timezone;

  /**
   * The slot P3 just failed on is dropped from the list rather than only mentioned.
   *
   * The refusal is the newest information on the page — newer than the response, which was computed
   * before somebody else committed — so leaving the slot clickable would invite the same failure a
   * second time.
   */
  const slots = useMemo(
    () => (availability.data?.slots ?? []).filter((slot) => slot.start !== takenSlot),
    [availability.data, takenSlot],
  );

  const days = useMemo(
    () => (timezone ? groupByDay(slots, timezone, i18n.language) : []),
    [slots, timezone, i18n.language],
  );

  /**
   * The chosen slot, and it lives here rather than in the URL (design D8).
   *
   * A chosen-but-uncommitted slot is not worth restoring across a reload, and putting it in the
   * address would make the back button from P3 ambiguous — is this a fresh search, or a return to
   * a choice already made? The search itself stays in the URL, which is the part that is worth
   * restoring and the part a patient might share.
   */
  const [chosen, setChosen] = useState<string | null>(null);

  const chosenSlot = useMemo(
    () => slots.find((slot) => slotKey(slot) === chosen) ?? null,
    [slots, chosen],
  );

  /** Writes the search back to the URL, clearing any refusal the previous search carried. */
  function update(changes: Record<string, string>) {
    // A search that changed is a list that changed, so a choice made against the old one is
    // meaningless. Cleared here rather than reconciled, because "your slot is still selected but
    // is no longer offered" is not a state worth building.
    setChosen(null);

    const next = new URLSearchParams(params);

    for (const [key, value] of Object.entries(changes)) {
      if (value) {
        next.set(key, value);
      } else {
        next.delete(key);
      }
    }

    next.delete(TAKEN_CODE_PARAM);
    next.delete(TAKEN_SLOT_PARAM);

    setParams(next, { replace: true });
  }

  /**
   * Choosing a slot selects it. Choosing another moves the selection (design D8).
   *
   * Single selection, and the deselect behaviour is the point: multi-select would imply booking
   * several, which no endpoint accepts, and requiring an explicit clear before choosing again is a
   * mode a patient has to learn. This is the behaviour of a radio group, which is what it is.
   */
  function chooseSlot(slot: { start: string; end: string; professionalId: string }) {
    setChosen(slotKey(slot));
  }

  /**
   * The separate, explicit act that leaves the page.
   *
   * **The URL contract handed to P3 is unchanged** — same five parameters, in the same shape 5a
   * built. This change moved when navigation happens, not what it carries, which is why a reload,
   * a bookmark and the back button from P3 all still behave.
   */
  function continueToConfirm() {
    if (!chosenSlot) {
      return;
    }

    const confirm = new URLSearchParams({
      type: appointmentType!.appointmentTypeId,
      professional: chosenSlot.professionalId,
      start: chosenSlot.start,
      end: chosenSlot.end,
      // Carried so P3 can offer "back to these results" without re-deriving the search.
      search: params.toString(),
    });

    void navigate(`/book/confirm?${confirm.toString()}`);
  }

  return (
    <div className="space-y-8">
      <header className="space-y-2">
        <h1 className="text-3xl font-semibold tracking-tight text-heading">{t('booking.title')}</h1>
        <p className="max-w-2xl text-meta">{t('booking.description')}</p>
      </header>

      {/*
        The just-taken state, at the top of the page it sends the patient back to, with the slot
        already gone from the list below. Not a toast: the message has to survive the refetch that
        follows it.
      */}
      {/*
        The refusal, in the code's OWN words and nothing else.

        This used to prepend a fixed "that time is no longer free". That sentence is true for
        `slot_taken` and false for most of the others — and it was reached in ordinary use with
        `booking.patient_busy`, where the slot IS free and the patient simply cannot be in two
        places at once. A generic title asserting a race in front of a message explaining something
        else is exactly the confusion `booking-core` split `slot_blocked` away from `slot_taken` to
        prevent; re-adding it above every one of them undid that work.

        Every `booking.*` message in the catalogue is already a complete sentence stating the fact
        and its consequence, which is what the design system asks copy to do — so the honest fix is
        to let the code speak and delete the preamble.
      */}
      {takenCode ? <Alert tone="error">{describeCode(takenCode)}</Alert> : null}

      {options.isError ? <Alert tone="error">{describeError(options.error)}</Alert> : null}

      {options.isPending ? (
        <p role="status" className="text-meta">
          {t('common.loading')}
        </p>
      ) : specialties.length === 0 ? (
        // Not an error: a clinic that has configured nothing bookable yet is a real state, and it
        // is the administrator's to fix rather than the patient's.
        <Alert tone="info">{t('booking.noServices')}</Alert>
      ) : (
        <>
          {/*
            The search beside the results, not above them (design D1).

            Availability is a read you ADJUST — widen the window, switch to any professional, try
            the next fortnight. Stacked, each of those pushed the answer down the page, so the loop
            was change -> scroll -> read -> scroll back. Beside it, the loop is change -> read.

            Below `lg` the columns stack with the search first: the design system mandates a single
            column under 768px, and on a phone the search genuinely is the first decision.
          */}
          <div className="grid gap-8 lg:grid-cols-[minmax(0,20rem)_minmax(0,1fr)] lg:items-start">
          <form
            className="grid gap-5 rounded-md border border-line bg-surface-raised p-5 lg:sticky lg:top-6"
            onSubmit={(event) => event.preventDefault()}
          >
            <Field label={t('booking.specialty')}>
              {({ id, describedBy }) => (
                <Select
                  id={id}
                  aria-describedby={describedBy}
                  value={specialty?.specialtyId ?? ''}
                  onChange={(event) =>
                    // Changing the specialty clears the kind of visit and the professional: both
                    // belonged to the old specialty and neither is meaningful under the new one.
                    update({ specialty: event.target.value, type: '', professional: '' })
                  }
                >
                  {specialties.map((entry) => (
                    <option key={entry.specialtyId} value={entry.specialtyId}>
                      {entry.name}
                    </option>
                  ))}
                </Select>
              )}
            </Field>

            <Field label={t('booking.appointmentType')}>
              {({ id, describedBy }) => (
                <Select
                  id={id}
                  aria-describedby={describedBy}
                  value={appointmentType?.appointmentTypeId ?? ''}
                  onChange={(event) => update({ type: event.target.value, professional: '' })}
                >
                  {(specialty?.appointmentTypes ?? []).map((entry) => (
                    <option key={entry.appointmentTypeId} value={entry.appointmentTypeId}>
                      {entry.name}
                    </option>
                  ))}
                </Select>
              )}
            </Field>

            {/*
              An explicit choice of two, not one entry at the top of a dropdown (design D4).

              Searching ANY qualified professional is the mode 02 §4 calls primary — the union
              across everyone, the thing that makes this a scheduler rather than a directory. As
              the first <option> of a select it read as an escape hatch. Here the two ways to
              search are peers, and the list of names belongs to the first of them.
            */}
            <div className="space-y-2">
              <RadioGroup
                label={t('booking.professional')}
                value={professionalId ? 'specific' : 'any'}
                onChange={(mode) =>
                  update({
                    professional:
                      mode === 'any' ? '' : (appointmentType?.professionals[0]?.professionalId ?? ''),
                  })
                }
                options={[
                  { value: 'specific', label: t('booking.specificProfessional') },
                  { value: 'any', label: t('booking.anyProfessional') },
                ]}
              />

              {professionalId ? (
                <Select
                  aria-label={t('booking.professional')}
                  value={professionalId}
                  onChange={(event) => update({ professional: event.target.value })}
                >
                  {(appointmentType?.professionals ?? []).map((entry) => (
                    <option key={entry.professionalId} value={entry.professionalId}>
                      {entry.displayName}
                    </option>
                  ))}
                </Select>
              ) : null}
            </div>

            <div className="grid grid-cols-2 gap-4">
              <Field label={t('booking.from')}>
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    type="date"
                    aria-describedby={describedBy}
                    value={from}
                    onChange={(event) => update({ from: event.target.value })}
                  />
                )}
              </Field>

              <Field label={t('booking.to')}>
                {({ id, describedBy }) => (
                  <Input
                    id={id}
                    type="date"
                    aria-describedby={describedBy}
                    value={to}
                    onChange={(event) => update({ to: event.target.value })}
                  />
                )}
              </Field>
            </div>

            {/*
              The tri-constraint promise, stated where a patient can see it (design D5).

              The middle line is a FORWARD-CLAIM and is kept deliberately: external blocks arrive
              with calendar-inbound (change 7), and every block today is internal — a professional
              blocking their own time on S3. The solver subtracts blocks without caring about their
              source, so the sentence becomes true the moment change 7 lands, with no edit here.

              What makes that acceptable rather than a lie: a professional's own blocks ARE
              subtracted, so the line names a source that does not exist yet, not a check that does
              not happen. It stops being acceptable the day a real clinic is told their
              professionals' Google calendars are consulted. See design D5 for the revisit trigger.
            */}
            <div className="space-y-2 border-t border-line pt-4">
              <p className="text-[11px] font-semibold uppercase tracking-[0.08em] text-meta">
                {t('booking.checkedTitle')}
              </p>
              <ul className="space-y-1.5 text-sm text-meta">
                {['checkedHours', 'checkedCalendar', 'checkedRoom'].map((key) => (
                  <li key={key} className="flex gap-2">
                    <span aria-hidden="true" className="text-primary">
                      &#10003;
                    </span>
                    <span>{t(`booking.${key}`)}</span>
                  </li>
                ))}
              </ul>
            </div>
          </form>

          <section aria-live="polite" className="space-y-6">
            {availability.isPending ? (
              // A skeleton rather than a spinner: the shape of the answer is known, so the page can
              // stop moving before the data arrives. This screen is meant to feel live.
              <SlotSkeleton label={t('booking.searching')} />
            ) : availability.isError ? (
              <Alert tone="error">{describeError(availability.error)}</Alert>
            ) : days.length === 0 ? (
              <div className="rounded-xl border border-dashed border-line p-10 text-center">
                <p className="font-medium text-heading">{t('booking.emptyTitle')}</p>
                <p className="mt-2 text-meta">{t('booking.emptyHint')}</p>
                <Button
                  variant="secondary"
                  className="mt-5"
                  onClick={() => update({ from: addDays(to, 1), to: addDays(to, 1 + DEFAULT_WINDOW_DAYS) })}
                >
                  {t('booking.tryNextWindow')}
                </Button>
              </div>
            ) : (
              <>
                {/*
                  The count first and large, the context subordinate to it — the artboard's
                  hierarchy. "18 free times" is the answer; the window, specialty and professional
                  are what it is an answer to.
                */}
                <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                  <span className="font-mono text-lg tabular-nums text-heading">
                    {t('booking.freeTimes', { count: slots.length })}
                  </span>
                  <span className="font-mono text-[13px] tabular-nums uppercase tracking-[0.02em] text-meta">
                    {t('booking.resultContext', {
                      from,
                      to,
                      what: appointmentType?.name ?? '',
                      who: professionalId
                        ? (appointmentType?.professionals.find(
                            (entry) => entry.professionalId === professionalId,
                          )?.displayName ?? '')
                        : t('booking.anyProfessional'),
                    })}
                  </span>
                </div>

                <SlotGrid
                  days={days}
                  timezone={timezone!}
                  professionals={appointmentType?.professionals ?? []}
                  showProfessional={!professionalId}
                  selected={chosen}
                  onChoose={chooseSlot}
                />

                <SelectionBar
                  slot={chosenSlot}
                  timezone={timezone!}
                  professional={
                    appointmentType?.professionals.find(
                      (entry) => entry.professionalId === chosenSlot?.professionalId,
                    )?.displayName
                  }
                  appointmentType={appointmentType?.name}
                  actionLabel={t('booking.continue')}
                  onContinue={continueToConfirm}
                />
              </>
            )}
          </section>
          </div>
        </>
      )}
    </div>
  );
}
