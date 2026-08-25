import {
  Alert,
  Button,
  Field,
  Input,
  getAvailability,
  getBookingOptions,
  listMyAppointments,
  rescheduleAppointment,
  useApiErrorMessage,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';
import { SelectionBar } from '../booking/SelectionBar';
import { SlotGrid, SlotSkeleton, slotKey } from '../booking/SlotGrid';
import { addDays, clinicToday, groupByDay, slotTime } from '../booking/slots';

/** The same fortnight P2 defaults to, for the same reason. */
const DEFAULT_WINDOW_DAYS = 13;

/**
 * P6 — reschedule (docs/06-ui-surfaces.md, design C3, C7).
 *
 * **Reuses P2's slot grid rather than copying it** (`SlotGrid`), which is why that component was
 * extracted. Two copies of a slot renderer would be two places to get the fall-back-day
 * disambiguation right in only one of them.
 *
 * **The search is scoped to the appointment's own professional and appointment type, with no
 * control to change either.** That is not a UI restriction over a permissive API: the request
 * carries an instant and nothing else, so a reschedule keeping the same professional is structural.
 * Moving to a different professional is a cancellation followed by a new booking, through the two
 * screens that already exist.
 *
 * **Both ends of the change are on screen.** What is being moved, and what it is being moved to —
 * so a patient can see the before and the after before committing, rather than trusting that the
 * right appointment was picked on the previous screen.
 */
export function ReschedulePage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const { id = '' } = useParams();
  const describeError = useApiErrorMessage();
  const queryClient = useQueryClient();

  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');

  // The same selection model as P2 (design D8), because SlotGrid is shared and a selection in only
  // one of them would fork it after all. It also matters more here: a patient rescheduling is by
  // definition dissatisfied with a time, which is exactly when comparing before committing helps.
  const [chosen, setChosen] = useState<string | null>(null);

  const appointments = useQuery({
    queryKey: ['my-appointments'],
    queryFn: listMyAppointments,
    staleTime: 0,
    retry: false,
  });

  const options = useQuery({ queryKey: ['booking-options'], queryFn: getBookingOptions, retry: false });

  const appointment =
    appointments.data?.upcoming.find((entry) => entry.id === id)
    ?? appointments.data?.past.find((entry) => entry.id === id);

  const timezone = appointments.data?.timezone;

  const windowFrom = from || (timezone ? clinicToday(timezone) : '');
  const windowTo = to || (windowFrom ? addDays(windowFrom, DEFAULT_WINDOW_DAYS) : '');

  // Scoped to this appointment's own professional and type. Not a filter over a wider search — the
  // only search this screen can express.
  const availability = useQuery({
    queryKey: ['availability', appointment?.appointmentTypeId, appointment?.professionalId, windowFrom, windowTo],
    queryFn: () =>
      getAvailability({
        appointmentTypeId: appointment!.appointmentTypeId,
        professionalId: appointment!.professionalId,
        from: windowFrom,
        to: windowTo,
      }),
    enabled: Boolean(appointment && windowFrom && windowTo),
    staleTime: 0,
    refetchOnWindowFocus: true,
    refetchOnReconnect: true,
    retry: false,
  });

  const reschedule = useMutation({
    mutationFn: (startsAt: string) => rescheduleAppointment(id, { startsAt }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['my-appointments'] });

      // Back to the list showing the new time, rather than to a separate success screen. P4's job
      // is to reassure a first-time booker that something exists; a reschedule already has a place
      // to return to, and landing there is what shows the change took effect.
      void navigate('/appointments');
    },
    onSettled: () => {
      // The list and the availability both moved, whichever way this went.
      void availability.refetch();
    },
  });

  const slots = availability.data?.slots ?? [];

  const chosenSlot = useMemo(
    () => slots.find((slot) => slotKey(slot) === chosen) ?? null,
    [slots, chosen],
  );

  const days = useMemo(
    () => (timezone ? groupByDay(slots, timezone, i18n.language) : []),
    [slots, timezone, i18n.language],
  );

  if (appointments.isPending) {
    return (
      <p className="text-meta" role="status">
        {t('appointments.loading')}
      </p>
    );
  }

  if (appointments.isError) {
    return <Alert tone="error">{describeError(appointments.error)}</Alert>;
  }

  if (!appointment) {
    return (
      <div className="space-y-4">
        <Alert tone="info">{t('appointments.notFound')}</Alert>
        <Button onClick={() => void navigate('/appointments')}>{t('appointments.backToList')}</Button>
      </div>
    );
  }

  // The server's decision, not a local clock comparison (design C10). Reaching this screen for a
  // locked appointment means a stale link or a window that closed while it was open.
  if (!appointment.canChange) {
    return (
      <div className="space-y-4">
        <Alert tone="info">{t('appointments.locked')}</Alert>
        <Button onClick={() => void navigate('/appointments')}>{t('appointments.backToList')}</Button>
      </div>
    );
  }

  const types = options.data?.specialties.flatMap((specialty) => specialty.appointmentTypes) ?? [];
  const appointmentType = types.find((entry) => entry.appointmentTypeId === appointment.appointmentTypeId);
  const professional = appointmentType?.professionals.find(
    (entry) => entry.professionalId === appointment.professionalId,
  );

  const currentDay = new Intl.DateTimeFormat(i18n.language, {
    timeZone: timezone,
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    year: 'numeric',
  }).format(new Date(appointment.startsAt));

  return (
    <div className="space-y-8">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight text-heading">
          {t('appointments.rescheduleTitle')}
        </h1>
        <p className="text-meta">{t('appointments.rescheduleSubtitle')}</p>
      </header>

      {/*
        What is being moved. Both ends of the change on one screen, so committing is a comparison
        rather than an act of faith about which appointment was picked.
      */}
      <div className="rounded-xl border border-line bg-surface-raised p-5">
        <p className="text-sm font-medium uppercase tracking-wide text-meta">
          {t('appointments.movingFrom')}
        </p>
        <p className="mt-1 font-mono font-semibold tabular-nums text-heading">
          {currentDay}, {slotTime(appointment.startsAt, timezone!)}–
          {slotTime(appointment.endsAt, timezone!)}
        </p>
        <p className="mt-1 text-sm text-meta">
          {appointmentType?.name ?? '—'} · {professional?.displayName ?? '—'}
        </p>
        {/*
          Said out loud rather than left to be inferred from a missing dropdown: the professional
          and the kind of visit are fixed, and changing either means cancelling and booking again.
        */}
        <p className="mt-3 text-sm text-meta">{t('appointments.sameProfessionalNote')}</p>
      </div>

      {reschedule.isError ? <Alert tone="error">{describeError(reschedule.error)}</Alert> : null}

      <div className="grid grid-cols-2 gap-4">
        <Field label={t('booking.from')}>
          {({ id: fieldId, describedBy }) => (
            <Input
              id={fieldId}
              type="date"
              aria-describedby={describedBy}
              value={windowFrom}
              onChange={(event) => {
                setChosen(null);
                setFrom(event.target.value);
              }}
            />
          )}
        </Field>

        <Field label={t('booking.to')}>
          {({ id: fieldId, describedBy }) => (
            <Input
              id={fieldId}
              type="date"
              aria-describedby={describedBy}
              value={windowTo}
              onChange={(event) => {
                setChosen(null);
                setTo(event.target.value);
              }}
            />
          )}
        </Field>
      </div>

      <section aria-live="polite" className="space-y-6">
        {availability.isPending ? (
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
              onClick={() => {
                setChosen(null);
                setFrom(addDays(windowTo, 1));
                setTo(addDays(windowTo, 1 + DEFAULT_WINDOW_DAYS));
              }}
            >
              {t('booking.tryNextWindow')}
            </Button>
          </div>
        ) : (
          <>
            <p className="text-sm text-meta">
              {t('appointments.movingTo', { timezone })}
            </p>

            <SlotGrid
              days={days}
              timezone={timezone!}
              professionals={appointmentType?.professionals ?? []}
              // Never: there is only one professional here, and repeating their name on every
              // button would be noise about the one thing that cannot change.
              showProfessional={false}
              selected={chosen}
              onChoose={(slot) => setChosen(slotKey(slot))}
            />

            <SelectionBar
              slot={chosenSlot}
              timezone={timezone!}
              professional={professional?.displayName}
              appointmentType={appointmentType?.name}
              actionLabel={t('appointments.confirmMove')}
              pending={reschedule.isPending}
              onContinue={() => chosenSlot && reschedule.mutate(chosenSlot.start)}
            />
          </>
        )}
      </section>

      <Button variant="secondary" onClick={() => void navigate('/appointments')}>
        {t('appointments.backToList')}
      </Button>
    </div>
  );
}
