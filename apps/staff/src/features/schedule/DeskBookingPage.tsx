import {
  Alert,
  Button,
  Field,
  Input,
  Select,
  Table,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  bookAppointmentForPatient,
  clinicTime,
  getAvailability,
  getBookingOptions,
  getScheduleDay,
  minutesBetween,
  rescheduleAppointment,
  resolvePatientByEmail,
  useApiErrorMessage,
  type AvailabilitySlotResponse,
  type ResolvedPatient,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { SCHEDULE_QUERY_KEY } from './ScheduleDay';

/**
 * S5 — booking on a patient's behalf, and moving an appointment (design N4, N11).
 *
 * **This is not the patient's `SlotGrid`, and it is not a copy of it** (design N4, locked as
 * option c). It calls the same availability API and renders its own table. The two screens
 * disagree about almost everything except the data: P2 is the designed showcase, centres a bounded
 * column, hides the room by decision (D7) and carries a trust panel naming an external calendar
 * that change 7 has yet to make true. This one is used with a patient standing at the desk — dense
 * rows, the room shown, no trust claim a receptionist would know is not yet real, and no selection
 * ceremony between choosing a time and having it booked.
 *
 * A shared component serving both would need a prop for each of those, and a primitive with five
 * behavioural switches is two components with extra steps. The revisit trigger is a *third*
 * availability surface — change 7's reconciliation queue is the candidate.
 *
 * **Two jobs, one surface** (design N11). Without `?move=`, it books a walk-in. With it, it moves
 * the named appointment, scoped to that appointment's professional and kind of visit — exactly as
 * P6 reuses P2's search, and the reason the duplication above stays affordable.
 */
export function DeskBookingPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();
  const navigate = useNavigate();
  const [params] = useSearchParams();

  const movingId = params.get('move');

  /**
   * The clinic day the appointment being moved is on, carried from S4.
   *
   * Separate from the date being SEARCHED, which the receptionist changes freely — the appointment
   * does not move to another day just because they looked at one. Falling back to today keeps a
   * hand-typed URL working for an appointment that is in fact today.
   */
  const movingOn = params.get('on');

  const [email, setEmail] = useState('');
  const [patient, setPatient] = useState<ResolvedPatient | null>(null);
  const [appointmentTypeId, setAppointmentTypeId] = useState('');
  const [professionalId, setProfessionalId] = useState('');
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [searched, setSearched] = useState<{ typeId: string; professionalId: string; date: string } | null>(
    null,
  );
  const [notice, setNotice] = useState<string | null>(null);

  const options = useQuery({
    queryKey: ['booking-options'],
    queryFn: getBookingOptions,
    retry: false,
  });

  /**
   * The appointment being moved, found on its own day.
   *
   * Read through the day endpoint rather than a new one: it is the only staff read of an
   * appointment there is, and adding a by-id read to save one round trip would be a second place
   * for the access record to be got wrong.
   */
  const moving = useQuery({
    queryKey: [...SCHEDULE_QUERY_KEY, 'move', movingId, movingOn],
    queryFn: () => getScheduleDay({ date: movingOn ?? new Date().toISOString().slice(0, 10) }),
    enabled: movingId !== null,
    retry: false,
  });

  const target = movingId
    ? (moving.data?.appointments.find((entry) => entry.id === movingId) ?? null)
    : null;

  // Scoped to the appointment's own professional and kind of visit — the request carries neither,
  // so this is the screen agreeing with a rule the server already enforces structurally.
  const effectiveTypeId = target?.appointmentTypeId ?? appointmentTypeId;
  const effectiveProfessionalId = target?.professionalId ?? professionalId;

  const find = useMutation({
    mutationFn: () => resolvePatientByEmail(email),
    onSuccess: (found) => {
      setNotice(null);
      setPatient(found);
    },
  });

  const availability = useQuery({
    queryKey: ['desk-availability', searched],
    queryFn: () =>
      getAvailability({
        appointmentTypeId: searched!.typeId,
        from: searched!.date,
        to: searched!.date,
        professionalId: searched!.professionalId || undefined,
      }),
    enabled: searched !== null,
    retry: false,
  });

  const book = useMutation({
    mutationFn: (slot: AvailabilitySlotResponse) =>
      bookAppointmentForPatient({
        appointmentTypeId: effectiveTypeId,
        professionalId: slot.professionalId,
        startsAt: slot.start,
        patientId: patient!.patientId,
      }),
    onSuccess: (created) => {
      setNotice(
        t('deskBooking.booked', {
          name: patient!.fullName,
          time: clinicTime(created.startsAt, created.timezone),

          // The room ASSIGNED, from the appointment that now exists — not the candidate the slot
          // named. Availability's room is an explanation; this one is the reservation.
          room: created.resourceName,
        }),
      );
      void queryClient.invalidateQueries({ queryKey: SCHEDULE_QUERY_KEY });
      void availability.refetch();
    },
  });

  const move = useMutation({
    mutationFn: (slot: AvailabilitySlotResponse) =>
      rescheduleAppointment(movingId!, { startsAt: slot.start }),
    onSuccess: (moved) => {
      setNotice(t('deskBooking.moved', { time: clinicTime(moved.startsAt, moved.timezone) }));
      void queryClient.invalidateQueries({ queryKey: SCHEDULE_QUERY_KEY });
      void availability.refetch();
    },
  });

  const timezone = availability.data?.timezone ?? options.data?.timezone ?? '';

  const types = (options.data?.specialties ?? []).flatMap((specialty) => specialty.appointmentTypes);
  const chosenType = types.find((type) => type.appointmentTypeId === effectiveTypeId);

  /**
   * Whether there is anything to choose a time for.
   *
   * **A resolved patient is the whole condition, and it used to also require a chosen kind of
   * visit — which was a deadlock** (validation check 5): the select that chooses one lives inside
   * the section this gate opens, so nothing could ever be chosen and the screen stopped dead after
   * naming the patient. The kind of visit is required to *search*, which the form enforces, not to
   * show the form.
   */
  /**
   * Whether the move has already happened.
   *
   * **Load-bearing, because `target === null` means two different things** (validation check 7,
   * second pass). A successful move takes the original appointment to `Rescheduled`, and the day
   * read excludes terminal appointments — correctly — so the appointment being moved *disappears
   * from its own day the moment the move succeeds*. Without this flag the screen reported success
   * and, directly underneath, warned that the appointment could not be found.
   *
   * It also closes a trap: `movingId` still names the original, which is now terminal, so a second
   * click would be refused with `booking.appointment_not_changeable`. After a move there is nothing
   * further to do here, and the only thing offered is the way back.
   */
  const moved = move.isSuccess;

  const ready = movingId ? target !== null && !moved : patient !== null;

  const action = movingId ? move : book;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-heading">
          {t(movingId ? 'deskBooking.moveTitle' : 'deskBooking.title')}
        </h1>
        <p className="mt-1 text-meta">
          {t(movingId ? 'deskBooking.moveDescription' : 'deskBooking.description')}
        </p>
      </div>

      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {action.isError ? <Alert tone="error">{describeError(action.error)}</Alert> : null}

      {movingId ? (
        <section className="space-y-2">
          {moved ? (
            // Nothing but the way back: the appointment that was here has been moved, and its
            // replacement is a different appointment with a different id. The success alert above
            // already names the new time.
            null
          ) : moving.isPending ? (
            <p role="status" className="text-meta">
              {t('common.loading')}
            </p>
          ) : target ? (
            <p className="text-body">
              <strong className="font-semibold">{target.patientName}</strong> ·{' '}
              {target.appointmentTypeName} · {target.professionalName} ·{' '}
              <span className="font-mono tabular-nums">
                {t('deskBooking.moveCurrent', {
                  time: clinicTime(target.startsAt, moving.data?.timezone ?? ''),
                })}
              </span>
            </p>
          ) : (
            /*
              NOT `schedule.empty`, which says "nothing is booked on this day" — a sentence about a
              day, shown here to mean "this appointment could not be found". Validation check 7 hit
              exactly that: the day being read was the wrong one, and the message sent the reader
              looking for a booking problem instead of a navigation one.
            */
            <Alert tone="warning">{t('deskBooking.moveNotFound')}</Alert>
          )}

          <Button variant="secondary" size="sm" onClick={() => void navigate('/day')}>
            {t('deskBooking.backToDay')}
          </Button>
        </section>
      ) : (
        <section className="space-y-3">
          <h2 className="text-lg font-semibold text-heading">{t('deskBooking.findPatient')}</h2>

          {patient ? (
            <div className="space-y-2">
              <p className="text-body">
                {t('deskBooking.patientFound', { name: patient.fullName })}{' '}
                <span className="text-meta">({patient.contactEmail})</span>
              </p>

              {/*
                Told BEFORE the receptionist takes a walk-in's time, not as a refusal after they
                have chosen a slot. The gate itself is not relaxed for staff and must not be:
                exempting the desk would let the clinic route around a patient's own withdrawal by
                telephone. So the screen warns and the server still refuses.
              */}
              {!patient.hasDataProcessingConsent ? (
                <Alert tone="warning">{t('deskBooking.consentMissing')}</Alert>
              ) : null}

              <Button
                variant="secondary"
                size="sm"
                onClick={() => {
                  setPatient(null);
                  setSearched(null);
                  setNotice(null);
                  find.reset();
                }}
              >
                {t('deskBooking.change')}
              </Button>
            </div>
          ) : (
            <form
              className="flex flex-wrap items-end gap-3"
              onSubmit={(event) => {
                event.preventDefault();
                find.mutate();
              }}
            >
              {/*
                The exact address, and not a search (design N8). A name or prefix search over
                patients is an enumeration surface, and every result would have to be recorded,
                which would bury the entries that matter.
              */}
              <Field label={t('deskBooking.emailLabel')} hint={t('deskBooking.emailHint')}>
                {({ id, describedBy, invalid }) => (
                  <Input
                    id={id}
                    type="email"
                    aria-describedby={describedBy}
                    aria-invalid={invalid}
                    value={email}
                    onChange={(event) => setEmail(event.target.value)}
                    required
                  />
                )}
              </Field>

              <Button type="submit" className="mb-1" disabled={find.isPending}>
                {t('deskBooking.find')}
              </Button>
            </form>
          )}

          {find.isError ? <Alert tone="error">{describeError(find.error)}</Alert> : null}
        </section>
      )}

      {ready ? (
        <section className="space-y-3">
          <h2 className="text-lg font-semibold text-heading">{t('deskBooking.chooseTime')}</h2>

          <form
            className="flex flex-wrap items-end gap-3"
            onSubmit={(event) => {
              event.preventDefault();

              // The gate that moved off `ready` (see above). `required` on the select already
              // stops the browser submitting without one; this is the same rule stated where the
              // value is used, so a change to the markup cannot quietly search for nothing.
              if (!effectiveTypeId) {
                return;
              }

              setNotice(null);
              action.reset();
              setSearched({
                typeId: effectiveTypeId,
                professionalId: effectiveProfessionalId,
                date,
              });
            }}
          >
            {!movingId ? (
              <>
                <Field label={t('deskBooking.appointmentType')}>
                  {({ id, describedBy, invalid }) => (
                    <Select
                      id={id}
                      aria-describedby={describedBy}
                      aria-invalid={invalid}
                      value={appointmentTypeId}
                      onChange={(event) => {
                        setAppointmentTypeId(event.target.value);
                        setProfessionalId('');
                      }}
                      required
                    >
                      <option value="">—</option>
                      {types.map((type) => (
                        <option key={type.appointmentTypeId} value={type.appointmentTypeId}>
                          {type.name}
                        </option>
                      ))}
                    </Select>
                  )}
                </Field>

                <Field label={t('deskBooking.professional')}>
                  {({ id, describedBy, invalid }) => (
                    <Select
                      id={id}
                      aria-describedby={describedBy}
                      aria-invalid={invalid}
                      value={professionalId}
                      onChange={(event) => setProfessionalId(event.target.value)}
                    >
                      {/*
                        "Any" is the default rather than an explicit choice of two, unlike P2. A
                        receptionist with a patient waiting wants the earliest time, and the
                        specific-versus-any framing exists to make a MODE visible to somebody
                        deciding between them — which is a patient's question, not the desk's.
                      */}
                      <option value="">{t('deskBooking.anyProfessional')}</option>
                      {(chosenType?.professionals ?? []).map((entry) => (
                        <option key={entry.professionalId} value={entry.professionalId}>
                          {entry.displayName}
                        </option>
                      ))}
                    </Select>
                  )}
                </Field>
              </>
            ) : null}

            <Field label={t('deskBooking.date')}>
              {({ id, describedBy, invalid }) => (
                <Input
                  id={id}
                  type="date"
                  aria-describedby={describedBy}
                  aria-invalid={invalid}
                  value={date}
                  onChange={(event) => setDate(event.target.value)}
                  className="font-mono tabular-nums"
                  required
                />
              )}
            </Field>

            <Button type="submit" className="mb-1">
              {t('deskBooking.search')}
            </Button>
          </form>

          {availability.isFetching ? (
            <p role="status" className="text-meta">
              {t('deskBooking.searching')}
            </p>
          ) : availability.isError ? (
            <Alert tone="error">{describeError(availability.error)}</Alert>
          ) : availability.data ? (
            availability.data.slots.length === 0 ? (
              <p className="text-meta">{t('deskBooking.noSlots')}</p>
            ) : (
              <>
                {/*
                  What the room on a slot IS: change 4's own words, "an explanation, not a
                  reservation". The assigned one is reported after booking. Saying so is the
                  difference between a useful column and one a receptionist learns to distrust.
                */}
                <p className="text-sm text-meta">{t('deskBooking.roomNote')}</p>
                <p className="text-sm text-meta">{t('schedule.timezoneNote', { timezone })}</p>

                <Table>
                  <TableHead>
                    <TableRow>
                      <TableHeaderCell>{t('deskBooking.columnTime')}</TableHeaderCell>
                      <TableHeaderCell>{t('deskBooking.columnProfessional')}</TableHeaderCell>
                      <TableHeaderCell>{t('deskBooking.columnRoom')}</TableHeaderCell>
                      <TableHeaderCell>{t('schedule.columnActions')}</TableHeaderCell>
                    </TableRow>
                  </TableHead>
                  <tbody>
                    {availability.data.slots.map((slot) => {
                      const who = (chosenType?.professionals ?? []).find(
                        (entry) => entry.professionalId === slot.professionalId,
                      );

                      return (
                        <TableRow key={`${slot.start}-${slot.professionalId}-${slot.resourceId}`}>
                          <TableCell className="whitespace-nowrap font-mono font-medium tabular-nums">
                            {clinicTime(slot.start, timezone)}
                            <span className="ml-2 text-xs text-meta">
                              {minutesBetween(slot.start, slot.end)} min
                            </span>
                          </TableCell>
                          <TableCell>
                            {who?.displayName ?? target?.professionalName ?? '—'}
                          </TableCell>
                          <TableCell>{slot.resourceName}</TableCell>
                          <TableCell>
                            {/*
                              One click books. No selection step, unlike P2 — that step exists so a
                              patient can compare two times before committing, and a receptionist
                              on the telephone has already agreed the time out loud.
                            */}
                            <Button
                              size="sm"
                              onClick={() => {
                                setNotice(null);
                                action.mutate(slot);
                              }}
                              disabled={action.isPending}
                            >
                              {t(movingId ? 'deskBooking.move' : 'deskBooking.book')}
                            </Button>
                          </TableCell>
                        </TableRow>
                      );
                    })}
                  </tbody>
                </Table>
              </>
            )
          ) : null}
        </section>
      ) : null}
    </div>
  );
}
