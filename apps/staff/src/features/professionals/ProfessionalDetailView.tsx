import {
  Alert,
  Button,
  Card,
  CardDescription,
  CardHeader,
  CardTitle,
  Dialog,
  DialogContent,
  DialogFooter,
  Field,
  renameProfessional,
  Input,
  Select,
  Table,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  WEEKDAYS,
  clearDuration,
  defineException,
  defineWorkingHours,
  getProfessional,
  grantSpecialty,
  listAppointmentTypes,
  listSpecialties,
  retireException,
  retireWorkingHours,
  revokeSpecialty,
  setDuration,
  useApiErrorMessage,
  type ProfessionalDetail,
} from '@clinic/shared';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { PROFESSIONALS_QUERY_KEY } from './ProfessionalsPage';

/**
 * One professional's configuration: qualifications, durations, hours, exceptions.
 *
 * The ordering on screen is the ordering of the rules. Specialties come first because they gate
 * the durations below them (design E2), and the duration section says so when it is empty
 * rather than merely being empty — the gate should be a visible consequence, not a surprise
 * refusal.
 */
export function ProfessionalDetailView({
  userId,
  onBack,
}: {
  userId: string;
  onBack: () => void;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();

  const detailKey = ['config', 'professionals', userId] as const;

  const { data: detail, isPending, isError, error } = useQuery({
    queryKey: detailKey,
    queryFn: () => getProfessional(userId),
    retry: false,
  });

  // The catalog lists, reused from 3a's endpoints rather than a new read shape (design E8).
  const [specialties, appointmentTypes] = useQueries({
    queries: [
      { queryKey: ['config', 'specialties'], queryFn: listSpecialties, retry: false },
      { queryKey: ['config', 'appointment-types'], queryFn: listAppointmentTypes, retry: false },
    ],
  });

  const [notice, setNotice] = useState<string | null>(null);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: detailKey });
    // The list carries the counts, so it goes stale on every write here.
    void queryClient.invalidateQueries({ queryKey: PROFESSIONALS_QUERY_KEY });
  };

  if (isPending) {
    return (
      <p role="status" className="text-meta">
        {t('common.loading')}
      </p>
    );
  }

  if (isError || !detail) {
    return <Alert tone="error">{describeError(error)}</Alert>;
  }

  const activeSpecialties = (specialties.data ?? []).filter((entry) => entry.isActive);
  const heldIds = new Set(detail.specialties.map((held) => held.specialtyId));

  // The gate, expressed in the UI: only types whose specialty this professional holds.
  const assignableTypes = (appointmentTypes.data ?? []).filter(
    (type) => type.isActive && heldIds.has(type.specialtyId),
  );

  return (
    <div className="space-y-6">
      <div>
        <Button variant="ghost" size="sm" onClick={onBack}>
          {t('professionals.backToList')}
        </Button>
        <h1 className="mt-2 text-2xl font-semibold text-heading">
          {detail.fullName ?? detail.email}
        </h1>
        {/*
          The address stays visible under the name once there is one — it is how an administrator
          tells two people apart, and it is what a professional signs in with.
        */}
        {detail.fullName ? <p className="text-sm text-meta">{detail.email}</p> : null}
        {detail.awaitsClaim ? (
          <p className="mt-1 text-sm text-meta">{t('professionals.awaitsClaim')}</p>
        ) : null}
      </div>

      {notice ? <Alert tone="success">{notice}</Alert> : null}

      <NameSection
        detail={detail}
        onDone={(message) => {
          setNotice(message);
          refresh();
        }}
      />

      <SpecialtiesSection
        detail={detail}
        available={activeSpecialties}
        onDone={(message) => {
          setNotice(message);
          refresh();
        }}
      />

      <DurationsSection
        detail={detail}
        assignableTypes={assignableTypes}
        onDone={(message) => {
          setNotice(message);
          refresh();
        }}
      />

      <WorkingHoursSection
        detail={detail}
        onDone={(message) => {
          setNotice(message);
          refresh();
        }}
      />

      <ExceptionsSection
        detail={detail}
        onDone={(message) => {
          setNotice(message);
          refresh();
        }}
      />
    </div>
  );
}

// --- The name (P-5) ----------------------------------------------------------------

/**
 * How this professional is named to a person (P-5, open since 3b; design N10).
 *
 * First in the screen because it is what a patient sees. Until this change the server derived a
 * label from the account address behind a `displayName` field, deliberately, so that entering a
 * real name here would cost no client a change — and it did not.
 *
 * Saving a name on a professional who has never been configured **creates** their configuration
 * record: it is a first save like any other, through the same seam (design E1). Clearing the field
 * removes the name and the derived label applies again, which is what makes removing one safe
 * rather than destructive.
 */
function NameSection({
  detail,
  onDone,
}: {
  detail: ProfessionalDetail;
  onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const describeError = useApiErrorMessage();

  const [fullName, setFullName] = useState(detail.fullName ?? '');

  const save = useMutation({
    mutationFn: () => renameProfessional(detail.userId, fullName),
    onSuccess: () => onDone(t('professionals.nameSaved')),
  });

  return (
    <section className="space-y-3">
      <h2 className="text-lg font-semibold text-heading">{t('professionals.fullName')}</h2>

      {save.isError ? <Alert tone="error">{describeError(save.error)}</Alert> : null}

      <form
        className="flex flex-wrap items-center gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          save.mutate();
        }}
      >
        <Field label={t('professionals.fullName')} hint={t('professionals.fullNameHint')}>
          {({ id, describedBy, invalid }) => (
            <Input
              id={id}
              aria-describedby={describedBy}
              aria-invalid={invalid}
              value={fullName}
              onChange={(event) => setFullName(event.target.value)}
              placeholder={t('professionals.fullNameMissing')}
            />
          )}
        </Field>

        <Button type="submit" className="mb-1" disabled={save.isPending}>
          {t('professionals.saveName')}
        </Button>
      </form>
    </section>
  );
}

// --- Specialties -------------------------------------------------------------------

function SpecialtiesSection({
  detail,
  available,
  onDone,
}: {
  detail: ProfessionalDetail;
  available: readonly { id: string; name: string }[];
  onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const describeError = useApiErrorMessage();

  const held = new Set(detail.specialties.map((entry) => entry.specialtyId));
  const grantable = available.filter((entry) => !held.has(entry.id));

  const [open, setOpen] = useState(false);
  const [specialtyId, setSpecialtyId] = useState('');

  const grant = useMutation({
    mutationFn: () => grantSpecialty(detail.userId, specialtyId),
    onSuccess: () => {
      setOpen(false);
      onDone(t('professionals.saved'));
    },
  });

  const revoke = useMutation({
    mutationFn: (id: string) => revokeSpecialty(detail.userId, id),
    onSuccess: () => onDone(t('professionals.removed')),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('professionals.specialtiesTitle')}</CardTitle>
        <CardDescription>{t('professionals.specialtiesNote')}</CardDescription>
      </CardHeader>

      <div className="space-y-4">
        {/* The refusal that names how many durations depend on the qualification. */}
        {revoke.isError ? <Alert tone="error">{describeError(revoke.error)}</Alert> : null}

        {grantable.length === 0 ? (
          <p className="text-sm text-meta">{t('professionals.noSpecialtiesLeft')}</p>
        ) : (
          <Button
            onClick={() => {
              grant.reset();
              setSpecialtyId(grantable[0].id);
              setOpen(true);
            }}
          >
            {t('professionals.addSpecialty')}
          </Button>
        )}

        {detail.specialties.length === 0 ? (
          <p className="text-meta">{t('professionals.specialtiesEmpty')}</p>
        ) : (
          <ul className="flex flex-wrap gap-2">
            {detail.specialties.map((entry) => (
              <li
                key={entry.specialtyId}
                className="flex items-center gap-2 rounded-sm border border-line px-3 py-1.5"
              >
                <span className="text-sm">{entry.specialtyName}</span>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => revoke.mutate(entry.specialtyId)}
                  disabled={revoke.isPending}
                >
                  {t('professionals.revoke')}
                </Button>
              </li>
            ))}
          </ul>
        )}
      </div>

      <Dialog open={open} onOpenChange={setOpen}>
        {open ? (
          <DialogContent title={t('professionals.addSpecialty')}>
            <form
              className="space-y-5"
              onSubmit={(event) => {
                event.preventDefault();
                grant.mutate();
              }}
            >
              <Field label={t('catalog.specialty')}>
                {({ id, describedBy }) => (
                  <Select
                    id={id}
                    aria-describedby={describedBy}
                    value={specialtyId}
                    onChange={(event) => setSpecialtyId(event.target.value)}
                    required
                  >
                    {grantable.map((entry) => (
                      <option key={entry.id} value={entry.id}>
                        {entry.name}
                      </option>
                    ))}
                  </Select>
                )}
              </Field>

              {grant.isError ? <Alert tone="error">{describeError(grant.error)}</Alert> : null}

              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => setOpen(false)}>
                  {t('common.cancel')}
                </Button>
                <Button type="submit" disabled={grant.isPending}>
                  {t('common.save')}
                </Button>
              </DialogFooter>
            </form>
          </DialogContent>
        ) : null}
      </Dialog>
    </Card>
  );
}

// --- Durations ---------------------------------------------------------------------

function DurationsSection({
  detail,
  assignableTypes,
  onDone,
}: {
  detail: ProfessionalDetail;
  assignableTypes: readonly { id: string; name: string; specialtyName: string }[];
  onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const describeError = useApiErrorMessage();

  const [open, setOpen] = useState(false);
  const [appointmentTypeId, setAppointmentTypeId] = useState('');
  const [minutes, setMinutes] = useState('30');

  const save = useMutation({
    mutationFn: () =>
      setDuration(detail.userId, { appointmentTypeId, durationMinutes: Number(minutes) }),
    onSuccess: () => {
      setOpen(false);
      onDone(t('professionals.saved'));
    },
  });

  const clear = useMutation({
    mutationFn: (id: string) => clearDuration(detail.userId, id),
    onSuccess: () => onDone(t('professionals.removed')),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('professionals.durationsTitle')}</CardTitle>
        <CardDescription>{t('professionals.durationsNote')}</CardDescription>
      </CardHeader>

      <div className="space-y-4">
        {clear.isError ? <Alert tone="error">{describeError(clear.error)}</Alert> : null}

        {/*
          The gate made visible. An empty panel would leave an administrator guessing why the
          types they just created are not offered; this says which step is missing.
        */}
        {assignableTypes.length === 0 ? (
          <Alert tone="info">{t('professionals.durationsGateEmpty')}</Alert>
        ) : (
          <Button
            onClick={() => {
              save.reset();
              setAppointmentTypeId(assignableTypes[0].id);
              setMinutes('30');
              setOpen(true);
            }}
          >
            {t('professionals.setDuration')}
          </Button>
        )}

        {detail.durations.length === 0 ? (
          <p className="text-meta">{t('professionals.durationsEmpty')}</p>
        ) : (
          <Table>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{t('catalog.specialty')}</TableHeaderCell>
                <TableHeaderCell>{t('professionals.columnVisitType')}</TableHeaderCell>
                <TableHeaderCell>{t('professionals.columnMinutes')}</TableHeaderCell>
                <TableHeaderCell>{t('catalog.columnActions')}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <tbody>
              {detail.durations.map((duration) => (
                <TableRow key={duration.appointmentTypeId}>
                  <TableCell className="text-meta">{duration.specialtyName}</TableCell>
                  <TableCell className="font-medium">{duration.appointmentTypeName}</TableCell>
                  {/* Measured value, so the mono face with tabular figures. */}
                  <TableCell className="font-mono tabular-nums">{duration.durationMinutes}</TableCell>
                  <TableCell>
                    <div className="flex gap-2">
                      <Button
                        variant="secondary"
                        size="sm"
                        onClick={() => {
                          save.reset();
                          setAppointmentTypeId(duration.appointmentTypeId);
                          setMinutes(String(duration.durationMinutes));
                          setOpen(true);
                        }}
                      >
                        {t('catalog.edit')}
                      </Button>
                      <Button
                        variant="secondary"
                        size="sm"
                        onClick={() => clear.mutate(duration.appointmentTypeId)}
                        disabled={clear.isPending}
                      >
                        {t('professionals.clearDuration')}
                      </Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </tbody>
          </Table>
        )}
      </div>

      <Dialog open={open} onOpenChange={setOpen}>
        {open ? (
          <DialogContent title={t('professionals.setDuration')}>
            <form
              className="space-y-5"
              onSubmit={(event) => {
                event.preventDefault();
                save.mutate();
              }}
            >
              <Field label={t('professionals.columnVisitType')}>
                {({ id, describedBy }) => (
                  <Select
                    id={id}
                    aria-describedby={describedBy}
                    value={appointmentTypeId}
                    onChange={(event) => setAppointmentTypeId(event.target.value)}
                    required
                  >
                    {assignableTypes.map((type) => (
                      <option key={type.id} value={type.id}>
                        {type.specialtyName} — {type.name}
                      </option>
                    ))}
                  </Select>
                )}
              </Field>

              <Field label={t('professionals.columnMinutes')}>
                {({ id, describedBy, invalid }) => (
                  <Input
                    id={id}
                    type="number"
                    min={1}
                    aria-describedby={describedBy}
                    aria-invalid={invalid}
                    value={minutes}
                    onChange={(event) => setMinutes(event.target.value)}
                    required
                  />
                )}
              </Field>

              {save.isError ? <Alert tone="error">{describeError(save.error)}</Alert> : null}

              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => setOpen(false)}>
                  {t('common.cancel')}
                </Button>
                <Button type="submit" disabled={save.isPending}>
                  {t('common.save')}
                </Button>
              </DialogFooter>
            </form>
          </DialogContent>
        ) : null}
      </Dialog>
    </Card>
  );
}

// --- Working hours -----------------------------------------------------------------

function WorkingHoursSection({
  detail,
  onDone,
}: {
  detail: ProfessionalDetail;
  onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const describeError = useApiErrorMessage();

  const [open, setOpen] = useState(false);
  const [dayOfWeek, setDayOfWeek] = useState<string>('Monday');
  const [startTime, setStartTime] = useState('08:00');
  const [endTime, setEndTime] = useState('12:00');
  const [effectiveFrom, setEffectiveFrom] = useState('');
  const [effectiveTo, setEffectiveTo] = useState('');

  const save = useMutation({
    mutationFn: () =>
      defineWorkingHours(detail.userId, {
        dayOfWeek,
        startTime,
        endTime,
        effectiveFrom,
        effectiveTo: effectiveTo === '' ? null : effectiveTo,
      }),
    onSuccess: () => {
      setOpen(false);
      onDone(t('professionals.saved'));
    },
  });

  const retire = useMutation({
    mutationFn: (id: string) => retireWorkingHours(detail.userId, id),
    onSuccess: () => onDone(t('professionals.removed')),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('professionals.hoursTitle')}</CardTitle>
        <CardDescription>{t('professionals.hoursNote')}</CardDescription>
      </CardHeader>

      <div className="space-y-4">
        {retire.isError ? <Alert tone="error">{describeError(retire.error)}</Alert> : null}

        <Button
          onClick={() => {
            save.reset();
            setOpen(true);
          }}
        >
          {t('professionals.addHours')}
        </Button>

        {detail.workingHours.length === 0 ? (
          <p className="text-meta">{t('professionals.hoursEmpty')}</p>
        ) : (
          <Table>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{t('professionals.columnDay')}</TableHeaderCell>
                <TableHeaderCell>{t('professionals.columnFrom')}</TableHeaderCell>
                <TableHeaderCell>{t('professionals.columnTo')}</TableHeaderCell>
                <TableHeaderCell>{t('professionals.columnEffective')}</TableHeaderCell>
                <TableHeaderCell>{t('catalog.columnActions')}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <tbody>
              {detail.workingHours.map((segment) => (
                <TableRow key={segment.id}>
                  <TableCell>{t(`weekdays.${segment.dayOfWeek}`)}</TableCell>
                  <TableCell className="font-mono tabular-nums">{segment.startTime}</TableCell>
                  <TableCell className="font-mono tabular-nums">{segment.endTime}</TableCell>
                  <TableCell className="text-meta">
                    {segment.effectiveTo
                      ? t('professionals.effectiveRange', {
                          from: segment.effectiveFrom,
                          to: segment.effectiveTo,
                        })
                      : t('professionals.effectiveOpenEnded', { from: segment.effectiveFrom })}
                  </TableCell>
                  <TableCell>
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => retire.mutate(segment.id)}
                      disabled={retire.isPending}
                    >
                      {t('professionals.retire')}
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </tbody>
          </Table>
        )}
      </div>

      <Dialog open={open} onOpenChange={setOpen}>
        {open ? (
          <DialogContent title={t('professionals.addHours')}>
            <form
              className="space-y-5"
              onSubmit={(event) => {
                event.preventDefault();
                save.mutate();
              }}
            >
              <Field label={t('professionals.columnDay')}>
                {({ id, describedBy }) => (
                  <Select
                    id={id}
                    aria-describedby={describedBy}
                    value={dayOfWeek}
                    onChange={(event) => setDayOfWeek(event.target.value)}
                  >
                    {WEEKDAYS.map((day) => (
                      <option key={day} value={day}>
                        {t(`weekdays.${day}`)}
                      </option>
                    ))}
                  </Select>
                )}
              </Field>

              <div className="grid gap-5 sm:grid-cols-2">
                <Field label={t('professionals.columnFrom')}>
                  {({ id, describedBy, invalid }) => (
                    <Input
                      id={id}
                      type="time"
                      aria-describedby={describedBy}
                      aria-invalid={invalid}
                      value={startTime}
                      onChange={(event) => setStartTime(event.target.value)}
                      required
                    />
                  )}
                </Field>

                <Field label={t('professionals.columnTo')}>
                  {({ id, describedBy, invalid }) => (
                    <Input
                      id={id}
                      type="time"
                      aria-describedby={describedBy}
                      aria-invalid={invalid}
                      value={endTime}
                      onChange={(event) => setEndTime(event.target.value)}
                      required
                    />
                  )}
                </Field>

                <Field label={t('professionals.effectiveFrom')}>
                  {({ id, describedBy, invalid }) => (
                    <Input
                      id={id}
                      type="date"
                      aria-describedby={describedBy}
                      aria-invalid={invalid}
                      value={effectiveFrom}
                      onChange={(event) => setEffectiveFrom(event.target.value)}
                      required
                    />
                  )}
                </Field>

                <Field label={t('professionals.effectiveTo')}>
                  {({ id, describedBy }) => (
                    <Input
                      id={id}
                      type="date"
                      aria-describedby={describedBy}
                      value={effectiveTo}
                      onChange={(event) => setEffectiveTo(event.target.value)}
                    />
                  )}
                </Field>
              </div>

              {/* The overlap and validity refusals land here, beside the segment being edited. */}
              {save.isError ? <Alert tone="error">{describeError(save.error)}</Alert> : null}

              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => setOpen(false)}>
                  {t('common.cancel')}
                </Button>
                <Button type="submit" disabled={save.isPending}>
                  {t('common.save')}
                </Button>
              </DialogFooter>
            </form>
          </DialogContent>
        ) : null}
      </Dialog>
    </Card>
  );
}

// --- Exceptions --------------------------------------------------------------------

function ExceptionsSection({
  detail,
  onDone,
}: {
  detail: ProfessionalDetail;
  onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const describeError = useApiErrorMessage();

  const [open, setOpen] = useState(false);
  const [date, setDate] = useState('');
  const [unavailable, setUnavailable] = useState(true);
  const [startTime, setStartTime] = useState('08:00');
  const [endTime, setEndTime] = useState('12:00');

  const save = useMutation({
    mutationFn: () =>
      defineException(
        detail.userId,
        unavailable ? { date } : { date, startTime, endTime },
      ),
    onSuccess: () => {
      setOpen(false);
      onDone(t('professionals.saved'));
    },
  });

  const retire = useMutation({
    mutationFn: (id: string) => retireException(detail.userId, id),
    onSuccess: () => onDone(t('professionals.removed')),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('professionals.exceptionsTitle')}</CardTitle>
        <CardDescription>{t('professionals.exceptionsNote')}</CardDescription>
      </CardHeader>

      <div className="space-y-4">
        {retire.isError ? <Alert tone="error">{describeError(retire.error)}</Alert> : null}

        <Button
          onClick={() => {
            save.reset();
            setOpen(true);
          }}
        >
          {t('professionals.addException')}
        </Button>

        {detail.exceptions.length === 0 ? (
          <p className="text-meta">{t('professionals.exceptionsEmpty')}</p>
        ) : (
          <Table>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{t('professionals.columnDate')}</TableHeaderCell>
                <TableHeaderCell>{t('professionals.columnHours')}</TableHeaderCell>
                <TableHeaderCell>{t('catalog.columnActions')}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <tbody>
              {detail.exceptions.map((exception) => (
                <TableRow key={exception.id}>
                  <TableCell className="font-mono tabular-nums">{exception.date}</TableCell>
                  <TableCell>
                    {exception.startTime === null ? (
                      <span className="text-meta">{t('professionals.unavailableAllDay')}</span>
                    ) : (
                      <span className="font-mono tabular-nums">
                        {exception.startTime}–{exception.endTime}
                      </span>
                    )}
                  </TableCell>
                  <TableCell>
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => retire.mutate(exception.id)}
                      disabled={retire.isPending}
                    >
                      {t('professionals.retire')}
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </tbody>
          </Table>
        )}
      </div>

      <Dialog open={open} onOpenChange={setOpen}>
        {open ? (
          <DialogContent title={t('professionals.addException')}>
            <form
              className="space-y-5"
              onSubmit={(event) => {
                event.preventDefault();
                save.mutate();
              }}
            >
              <Field label={t('professionals.columnDate')}>
                {({ id, describedBy, invalid }) => (
                  <Input
                    id={id}
                    type="date"
                    aria-describedby={describedBy}
                    aria-invalid={invalid}
                    value={date}
                    onChange={(event) => setDate(event.target.value)}
                    required
                  />
                )}
              </Field>

              <Field label={t('professionals.exceptionKind')}>
                {({ id, describedBy }) => (
                  <Select
                    id={id}
                    aria-describedby={describedBy}
                    value={unavailable ? 'unavailable' : 'hours'}
                    onChange={(event) => setUnavailable(event.target.value === 'unavailable')}
                  >
                    <option value="unavailable">{t('professionals.exceptionUnavailable')}</option>
                    <option value="hours">{t('professionals.exceptionDifferentHours')}</option>
                  </Select>
                )}
              </Field>

              {/*
                The times appear only for the replacement case rather than being disabled: an
                all-day absence has no hours at all, and empty boxes would suggest otherwise.
              */}
              {unavailable ? null : (
                <div className="grid gap-5 sm:grid-cols-2">
                  <Field label={t('professionals.columnFrom')}>
                    {({ id, describedBy, invalid }) => (
                      <Input
                        id={id}
                        type="time"
                        aria-describedby={describedBy}
                        aria-invalid={invalid}
                        value={startTime}
                        onChange={(event) => setStartTime(event.target.value)}
                        required
                      />
                    )}
                  </Field>

                  <Field label={t('professionals.columnTo')}>
                    {({ id, describedBy, invalid }) => (
                      <Input
                        id={id}
                        type="time"
                        aria-describedby={describedBy}
                        aria-invalid={invalid}
                        value={endTime}
                        onChange={(event) => setEndTime(event.target.value)}
                        required
                      />
                    )}
                  </Field>
                </div>
              )}

              {save.isError ? <Alert tone="error">{describeError(save.error)}</Alert> : null}

              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => setOpen(false)}>
                  {t('common.cancel')}
                </Button>
                <Button type="submit" disabled={save.isPending}>
                  {t('common.save')}
                </Button>
              </DialogFooter>
            </form>
          </DialogContent>
        ) : null}
      </Dialog>
    </Card>
  );
}
