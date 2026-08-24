import {
  Alert,
  Button,
  Field,
  Input,
  Select,
  getAvailability,
  getBookingOptions,
  useApiCodeMessage,
  useApiErrorMessage,
} from '@clinic/shared';
import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { SlotGrid, SlotSkeleton } from './SlotGrid';
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

  /** Writes the search back to the URL, clearing any refusal the previous search carried. */
  function update(changes: Record<string, string>) {
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

  function chooseSlot(slot: { start: string; end: string; professionalId: string }) {
    const confirm = new URLSearchParams({
      type: appointmentType!.appointmentTypeId,
      professional: slot.professionalId,
      start: slot.start,
      end: slot.end,
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
      {takenCode ? (
        <Alert tone="error">
          <span className="font-medium">{t('booking.takenTitle')}</span>{' '}
          {describeCode(takenCode)}
        </Alert>
      ) : null}

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
          <form
            className="grid gap-5 rounded-xl border border-line bg-surface-raised p-6 sm:grid-cols-2"
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

            <Field label={t('booking.professional')} hint={t('booking.professionalHint')}>
              {({ id, describedBy }) => (
                <Select
                  id={id}
                  aria-describedby={describedBy}
                  value={professionalId}
                  onChange={(event) => update({ professional: event.target.value })}
                >
                  {/* First, and the default: the union across everyone qualified. */}
                  <option value="">{t('booking.anyProfessional')}</option>
                  {(appointmentType?.professionals ?? []).map((entry) => (
                    <option key={entry.professionalId} value={entry.professionalId}>
                      {entry.displayName}
                    </option>
                  ))}
                </Select>
              )}
            </Field>

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
                <p className="text-sm text-meta">
                  {t('booking.resultSummary', { count: slots.length, timezone })}
                </p>

                <SlotGrid
                  days={days}
                  timezone={timezone!}
                  professionals={appointmentType?.professionals ?? []}
                  showProfessional={!professionalId}
                  onChoose={chooseSlot}
                />
              </>
            )}
          </section>
        </>
      )}
    </div>
  );
}
