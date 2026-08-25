import {
  Alert,
  Badge,
  Button,
  Dialog,
  DialogContent,
  DialogFooter,
  Field,
  Input,
  Select,
  Table,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  addDays,
  cancelAppointment,
  clinicDate,
  clinicShortDate,
  clinicTime,
  clinicToday,
  getScheduleDay,
  minutesBetween,
  useApiErrorMessage,
  type ScheduledAppointment,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

export const SCHEDULE_QUERY_KEY = ['schedule'] as const;

/**
 * A period on the clinic's clock, saying which day an end belongs to when it is not this one.
 *
 * **Found by running the screen** (validation check 3). A block from 08:05 on the 30th to 07:30 on
 * the 31st was rendering as `08:05–07:30` on both days, which reads as a period inside one day and
 * is wrong by twenty-three hours — a receptionist would offer 15:00 on a day the professional is
 * away. S3 was never affected: it lists whole dates because a block is not scoped to a day there.
 *
 * The date appears only when it differs from the day on screen, so an ordinary same-day period is
 * unchanged and an appointment — minutes long, and never crossing midnight in practice — never
 * grows one. Applying it to both is a single call and leaves no second place to get this wrong.
 */
function DayRange({
  start,
  end,
  timezone,
  language,
  day,
}: {
  start: string;
  end: string;
  timezone: string;
  language: string;
  day: string;
}) {
  const startsToday = clinicDate(start, timezone) === day;
  const endsToday = clinicDate(end, timezone) === day;

  return (
    <span className="whitespace-nowrap font-mono tabular-nums">
      {startsToday ? null : (
        <span className="text-meta">{clinicShortDate(start, timezone, language)} </span>
      )}
      {clinicTime(start, timezone)}–{clinicTime(end, timezone)}
      {endsToday ? null : (
        <span className="text-meta"> {clinicShortDate(end, timezone, language)}</span>
      )}
    </span>
  );
}

/**
 * S1 and S4 — one clinic day (docs/06-ui-surfaces.md; design N9, N11).
 *
 * One component for both screens, because the API is one endpoint for both and for the same
 * reason: they ask the same question with a different scope. What differs is what a reader may
 * *do* — a professional reads their own day, reception runs it — so the actions, the professional
 * column and the narrowing control are the only things behind a flag.
 *
 * Utilitarian by decision rather than by omission (`06` Z2): a table with tabular figures, no
 * calendar grid. A day is a list of times, and a week grid has nowhere to put the three things a
 * receptionist actually needs on screen — the patient, the room, and whether the patient is still
 * allowed to move it.
 */
export function ScheduleDay({ mode }: { mode: 'mine' | 'clinic' }) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();
  const navigate = useNavigate();

  const runsTheDay = mode === 'clinic';

  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [professionalId, setProfessionalId] = useState('');
  const [notice, setNotice] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<ScheduledAppointment | null>(null);

  const { data, isPending, isError, error } = useQuery({
    queryKey: [...SCHEDULE_QUERY_KEY, mode, date],
    queryFn: () => getScheduleDay({ date }),
    retry: false,
  });

  const cancel = useMutation({
    mutationFn: (appointment: ScheduledAppointment) => cancelAppointment(appointment.id),
    onSuccess: () => {
      setNotice(t('schedule.cancelled'));
      setConfirming(null);
      void queryClient.invalidateQueries({ queryKey: SCHEDULE_QUERY_KEY });
    },
  });

  const timezone = data?.timezone ?? '';
  const shownDay = data?.date ?? date;

  /**
   * Who is on this day, taken from the day itself.
   *
   * Not from `/api/config/professionals`, which is administrator-only and would have had to be
   * widened to put a dropdown on a screen — the alternative design N5 rejected for the room, and
   * rejected again here for the same reason: a security boundary should not move to feed a filter.
   *
   * So the narrowing is **client-side**, which is also the honest choice. The whole day has already
   * been disclosed and recorded; re-reading a subset would write a second set of access rows for
   * people already on screen.
   */
  const onDuty = [
    ...new Map(
      [...(data?.appointments ?? []), ...(data?.blocks ?? [])].map((entry) => [
        entry.professionalId,
        entry.professionalName,
      ]),
    ).entries(),
  ].sort(([, first], [, second]) => first.localeCompare(second));

  const matches = (entry: { professionalId: string }) =>
    !professionalId || entry.professionalId === professionalId;

  const appointments = (data?.appointments ?? []).filter(matches);
  const blocks = (data?.blocks ?? []).filter(matches);

  function shift(days: number) {
    setNotice(null);
    cancel.reset();
    setDate((current) => addDays(current, days));
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-heading">
          {t(runsTheDay ? 'schedule.dayTitle' : 'schedule.myTitle')}
        </h1>
        <p className="mt-1 text-meta">
          {t(runsTheDay ? 'schedule.dayDescription' : 'schedule.myDescription')}
        </p>
      </div>

      {/*
        Said out loud, on the screen that causes it. This read writes an AccessLog row for every
        patient it names (design N7), and somebody who uses the console daily should know that
        rather than discover it in a document.
      */}
      <p className="text-sm text-meta">{t('schedule.accessNote')}</p>

      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {cancel.isError ? <Alert tone="error">{describeError(cancel.error)}</Alert> : null}

      <div className="flex flex-wrap items-end gap-3">
        <Field label={t('schedule.date')}>
          {({ id, describedBy, invalid }) => (
            <Input
              id={id}
              type="date"
              aria-describedby={describedBy}
              aria-invalid={invalid}
              value={date}
              onChange={(event) => {
                setNotice(null);
                setDate(event.target.value);
              }}
              className="font-mono tabular-nums"
            />
          )}
        </Field>

        {runsTheDay ? (
          <Field label={t('schedule.professional')}>
            {({ id, describedBy, invalid }) => (
              <Select
                id={id}
                aria-describedby={describedBy}
                aria-invalid={invalid}
                value={professionalId}
                onChange={(event) => setProfessionalId(event.target.value)}
              >
                <option value="">{t('schedule.allProfessionals')}</option>
                {onDuty.map(([id_, name]) => (
                  <option key={id_} value={id_}>
                    {name}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        ) : null}

        <div className="flex gap-2 pb-1">
          <Button variant="secondary" size="sm" onClick={() => shift(-1)}>
            {t('schedule.previousDay')}
          </Button>
          <Button
            variant="secondary"
            size="sm"
            onClick={() => {
              setNotice(null);
              // The CLINIC's today once a response has told us its zone. Before that the browser's
              // is the only one available, and it is only ever a starting date the reader can move.
              setDate(clinicToday(timezone || Intl.DateTimeFormat().resolvedOptions().timeZone));
            }}
          >
            {t('schedule.today')}
          </Button>
          <Button variant="secondary" size="sm" onClick={() => shift(1)}>
            {t('schedule.nextDay')}
          </Button>
        </div>
      </div>

      {isPending ? (
        <p role="status" className="text-meta">
          {t('common.loading')}
        </p>
      ) : isError ? (
        <Alert tone="error">{describeError(error)}</Alert>
      ) : (
        <>
          {/*
            Which clock these times are on, said out loud — the same note S3 carries and for the
            same reason: the clinic has exactly one (Decision H), and a reader in another zone
            would otherwise reasonably read them as their own.
          */}
          <p className="text-sm text-meta">{t('schedule.timezoneNote', { timezone })}</p>

          {appointments.length === 0 ? (
            <p className="text-meta">{t('schedule.empty')}</p>
          ) : (
            <Table>
              <TableHead>
                <TableRow>
                  <TableHeaderCell>{t('schedule.columnTime')}</TableHeaderCell>
                  <TableHeaderCell>{t('schedule.columnPatient')}</TableHeaderCell>
                  <TableHeaderCell>{t('schedule.columnType')}</TableHeaderCell>
                  {runsTheDay ? (
                    <TableHeaderCell>{t('schedule.columnProfessional')}</TableHeaderCell>
                  ) : null}
                  {/*
                    The room, on a staff surface. D7 keeps it off patient screens; a receptionist
                    has to say where to go and a professional has to know where they are sitting
                    (design N5).
                  */}
                  <TableHeaderCell>{t('schedule.columnRoom')}</TableHeaderCell>
                  {runsTheDay ? (
                    <TableHeaderCell>{t('schedule.columnSource')}</TableHeaderCell>
                  ) : null}
                  {runsTheDay ? (
                    <TableHeaderCell>{t('schedule.columnActions')}</TableHeaderCell>
                  ) : null}
                </TableRow>
              </TableHead>
              <tbody>
                {appointments.map((appointment) => (
                  <TableRow key={appointment.id}>
                    <TableCell className="font-medium">
                      <DayRange
                        start={appointment.startsAt}
                        end={appointment.endsAt}
                        timezone={timezone}
                        language={i18n.language}
                        day={shownDay}
                      />
                      <span className="ml-2 text-xs text-meta">
                        {minutesBetween(appointment.startsAt, appointment.endsAt)} min
                      </span>
                    </TableCell>
                    <TableCell>{appointment.patientName}</TableCell>
                    <TableCell>{appointment.appointmentTypeName}</TableCell>
                    {runsTheDay ? <TableCell>{appointment.professionalName}</TableCell> : null}
                    <TableCell>{appointment.resourceName}</TableCell>
                    {runsTheDay ? (
                      <TableCell>
                        <Badge tone={appointment.source === 'FrontDesk' ? 'active' : 'neutral'}>
                          {t(
                            appointment.source === 'FrontDesk'
                              ? 'schedule.sourceFrontDesk'
                              : 'schedule.sourceSelfService',
                          )}
                        </Badge>
                      </TableCell>
                    ) : null}
                    {runsTheDay ? (
                      <TableCell>
                        <div className="flex flex-col gap-2">
                          <div className="flex gap-2">
                            <Button
                              variant="secondary"
                              size="sm"
                              onClick={() => {
                                setNotice(null);
                                cancel.reset();
                                setConfirming(appointment);
                              }}
                            >
                              {t('schedule.cancel')}
                            </Button>
                            {/*
                              Move reuses S5's availability view scoped to this appointment
                              (design N11) — one staff availability surface doing both jobs, which
                              is also what makes "do not share the patient grid" affordable.
                            */}
                            <Button
                              variant="secondary"
                              size="sm"
                              onClick={() =>
                                // The day travels with the id. Without it S5 would look for the
                                // appointment on ITS default day and find nothing whenever the
                                // one being moved is not today — the day read is by date, and an
                                // appointment is only on one of them.
                                void navigate(`/book?move=${appointment.id}&on=${date}`)
                              }
                            >
                              {t('schedule.move')}
                            </Button>
                          </div>

                          {/*
                            THE SENTENCE THIS SCREEN EXISTS TO SAY. The actions above stay
                            available — reception is not bound by the cutoff — and this note says
                            whom the rule stopped. Without it the screen would merely permit the
                            override rather than demonstrate it.
                          */}
                          {!appointment.patientCanChange ? (
                            <span className="text-xs text-meta">
                              <strong className="font-semibold">{t('schedule.patientLocked')}</strong>{' '}
                              {t('schedule.patientLockedNote')}
                            </span>
                          ) : null}
                        </div>
                      </TableCell>
                    ) : null}
                  </TableRow>
                ))}
              </tbody>
            </Table>
          )}

          <section className="space-y-2">
            <h2 className="text-lg font-semibold text-heading">{t('schedule.blocks')}</h2>

            {blocks.length === 0 ? (
              <p className="text-meta">{t('schedule.blocksEmpty')}</p>
            ) : (
              <ul className="space-y-1 text-sm">
                {blocks.map((block) => (
                  <li key={block.id} className="flex gap-3">
                    <DayRange
                      start={block.startsAt}
                      end={block.endsAt}
                      timezone={timezone}
                      language={i18n.language}
                      day={shownDay}
                    />
                    <span className="text-meta">{block.professionalName}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}

      {/*
        An explicit confirmation that names who and when rather than asking "are you sure?". A
        cancel taken by mistake costs a patient their appointment and nobody is told.
      */}
      <Dialog open={confirming !== null} onOpenChange={(next) => !next && setConfirming(null)}>
        {confirming !== null ? (
          <DialogContent title={t('schedule.cancelTitle')}>
            <p className="text-body">
              {t('schedule.cancelBody', {
                patient: confirming.patientName,
                time: clinicTime(confirming.startsAt, timezone),
              })}
            </p>

            <DialogFooter>
              <Button variant="secondary" onClick={() => setConfirming(null)}>
                {t('schedule.cancelKeep')}
              </Button>
              <Button onClick={() => cancel.mutate(confirming)} disabled={cancel.isPending}>
                {t('schedule.cancelConfirm')}
              </Button>
            </DialogFooter>
          </DialogContent>
        ) : null}
      </Dialog>
    </div>
  );
}

/** S1 — a professional's own day. */
export function MySchedulePage() {
  return <ScheduleDay mode="mine" />;
}

/** S4 — the day across professionals. */
export function DayViewPage() {
  return <ScheduleDay mode="clinic" />;
}
