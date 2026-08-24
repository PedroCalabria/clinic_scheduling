import {
  Alert,
  Button,
  cancelAppointment,
  getBookingOptions,
  listMyAppointments,
  useApiErrorMessage,
  type MyAppointment,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { slotTime } from '../booking/slots';

/**
 * P5 — my appointments (docs/06-ui-surfaces.md, design C10).
 *
 * The screen where domain-model F3 becomes visible. Two things about it are decisions rather than
 * layout:
 *
 * 1. **The cutoff is shown, not merely enforced.** An appointment inside the cutoff renders with its
 *    actions disabled and a sentence saying why, rather than with a button that fails. `02 §5` calls
 *    F3 the concrete demonstration of RBAC and ownership coexisting, and that argument only lands if
 *    a patient can see the boundary instead of bumping into it.
 * 2. **The server decides whether an action is available.** `canChange` arrives on the payload; this
 *    screen never compares a start time to `Date.now()`. The browser's clock is not the clinic's and
 *    is user-settable — the exact class of bug that passes every test in the repository, because the
 *    whole suite runs in one process with one notion of local time.
 *
 * The refusal still has to be handled: a patient can sit here while the window closes. That is not a
 * gap in (2) but its cost, and it is designed for — see the mutation's error handling.
 */
export function AppointmentsPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const describeError = useApiErrorMessage();
  const queryClient = useQueryClient();

  const [confirming, setConfirming] = useState<string | null>(null);

  // Same posture as P2: never cached. An appointment cancelled in another tab, or a cutoff that has
  // since passed, must not persist on screen.
  const appointments = useQuery({
    queryKey: ['my-appointments'],
    queryFn: listMyAppointments,
    staleTime: 0,
    refetchOnWindowFocus: true,
    refetchOnReconnect: true,
    retry: false,
  });

  // Only for labels, and already cached from P2 in the ordinary case.
  const options = useQuery({ queryKey: ['booking-options'], queryFn: getBookingOptions, retry: false });

  const cancel = useMutation({
    mutationFn: cancelAppointment,
    onSettled: () => {
      // Refetched on success AND on failure. A refusal here usually means the world moved — the
      // cutoff passed, or another tab already cancelled it — so the list itself is what was stale.
      setConfirming(null);
      void queryClient.invalidateQueries({ queryKey: ['my-appointments'] });
    },
  });

  const timezone = appointments.data?.timezone;

  function label(appointment: MyAppointment) {
    const types = options.data?.specialties.flatMap((specialty) => specialty.appointmentTypes) ?? [];
    const type = types.find((entry) => entry.appointmentTypeId === appointment.appointmentTypeId);
    const who = type?.professionals.find(
      (entry) => entry.professionalId === appointment.professionalId,
    );

    return { type: type?.name ?? '—', professional: who?.displayName ?? '—' };
  }

  return (
    <div className="space-y-8">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight text-heading">
          {t('appointments.title')}
        </h1>
        <p className="text-meta">{t('appointments.subtitle')}</p>
      </header>

      {cancel.isError ? <Alert tone="error">{describeError(cancel.error)}</Alert> : null}

      {appointments.isPending ? (
        <p className="text-meta" role="status">
          {t('appointments.loading')}
        </p>
      ) : appointments.isError ? (
        <Alert tone="error">{describeError(appointments.error)}</Alert>
      ) : (
        <>
          <Section
            title={t('appointments.upcomingTitle')}
            empty={t('appointments.upcomingEmpty')}
            entries={appointments.data.upcoming}
          >
            {(appointment) => (
              <Row
                key={appointment.id}
                appointment={appointment}
                timezone={timezone!}
                language={i18n.language}
                names={label(appointment)}
              >
                {appointment.canChange ? (
                  <div className="flex flex-wrap gap-2">
                    <Button
                      variant="secondary"
                      onClick={() => void navigate(`/appointments/${appointment.id}/reschedule`)}
                    >
                      {t('appointments.reschedule')}
                    </Button>

                    {confirming === appointment.id ? (
                      // An explicit confirmation, in place. Cancelling frees a slot somebody else
                      // can take within seconds, so it is not an action to fire on one click.
                      <span className="flex flex-wrap items-center gap-2">
                        <span className="text-sm text-meta">{t('appointments.cancelConfirm')}</span>
                        <Button
                          variant="danger"
                          disabled={cancel.isPending}
                          onClick={() => cancel.mutate(appointment.id)}
                        >
                          {t('appointments.cancelYes')}
                        </Button>
                        <Button variant="secondary" onClick={() => setConfirming(null)}>
                          {t('appointments.cancelNo')}
                        </Button>
                      </span>
                    ) : (
                      <Button variant="secondary" onClick={() => setConfirming(appointment.id)}>
                        {t('appointments.cancel')}
                      </Button>
                    )}
                  </div>
                ) : (
                  /*
                    The rule, shown before it is hit. Not a hidden button — a patient should be able
                    to tell that the action exists and that a policy, rather than a bug, is why they
                    cannot use it. `role="note"` so the explanation is in the accessibility tree
                    rather than conveyed only by two buttons looking grey.
                  */
                  <p role="note" className="text-sm text-meta">
                    {t('appointments.locked')}
                  </p>
                )}
              </Row>
            )}
          </Section>

          <Section
            title={t('appointments.pastTitle')}
            empty={t('appointments.pastEmpty')}
            entries={appointments.data.past}
          >
            {(appointment) => (
              <Row
                key={appointment.id}
                appointment={appointment}
                timezone={timezone!}
                language={i18n.language}
                names={label(appointment)}
              />
            )}
          </Section>
        </>
      )}

      <div>
        <Button onClick={() => void navigate('/book')}>{t('appointments.bookAnother')}</Button>
      </div>
    </div>
  );
}

function Section({
  title,
  empty,
  entries,
  children,
}: {
  title: string;
  empty: string;
  entries: MyAppointment[];
  children: (appointment: MyAppointment) => React.ReactNode;
}) {
  return (
    <section className="space-y-3">
      <h2 className="text-sm font-semibold uppercase tracking-wide text-meta">{title}</h2>

      {entries.length === 0 ? (
        <p className="rounded-xl border border-dashed border-line p-6 text-center text-meta">{empty}</p>
      ) : (
        <ul className="space-y-3">{entries.map(children)}</ul>
      )}
    </section>
  );
}

function Row({
  appointment,
  timezone,
  language,
  names,
  children,
}: {
  appointment: MyAppointment;
  timezone: string;
  language: string;
  names: { type: string; professional: string };
  children?: React.ReactNode;
}) {
  const { t } = useTranslation();

  // Clinic wall clock, from the zone the response carries — never the browser's. Same rule as P2,
  // and the reason `timezone` travels on the payload at all.
  const day = new Intl.DateTimeFormat(language, {
    timeZone: timezone,
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    year: 'numeric',
  }).format(new Date(appointment.startsAt));

  const live = appointment.status === 'Scheduled';

  return (
    <li className="rounded-xl border border-line bg-surface p-5">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="space-y-1">
          <p className="font-mono font-semibold tabular-nums text-heading">
            {day}, {slotTime(appointment.startsAt, timezone)}–{slotTime(appointment.endsAt, timezone)}
          </p>
          <p className="text-sm text-meta">
            {names.type} · {names.professional}
          </p>

          {/*
            Terminal appointments are listed rather than filtered away, annotated with what happened
            to them. "What happened to my 3pm?" is the question a patient asks, and a cancelled
            appointment belongs where they would look for it (design Open Question 1).
          */}
          {live ? null : (
            <p className="text-sm font-medium text-meta">
              {t(`appointments.status.${appointment.status}`)}
            </p>
          )}
        </div>

        {children}
      </div>
    </li>
  );
}
