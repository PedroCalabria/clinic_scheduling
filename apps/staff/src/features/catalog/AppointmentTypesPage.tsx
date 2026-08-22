import {
  Alert,
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
  createAppointmentType,
  listAppointmentTypes,
  listResourceTypes,
  listSpecialties,
  setCatalogEntityActive,
  updateAppointmentType,
  useApiErrorMessage,
  type AppointmentTypeResponse,
} from '@clinic/shared';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ActionsCell, CatalogHeading, StatusCell } from './CatalogBits';

const APPOINTMENT_TYPES_QUERY_KEY = ['config', 'appointment-types'] as const;

/**
 * S10 — Appointment types (docs/06-ui-surfaces.md).
 *
 * The screen that ties the catalog together: a kind of visit belongs to a specialty and
 * requires a kind of room. Both choosers offer only ACTIVE records, because the API refuses
 * a reference to a retired one (design D5) and offering it would be inviting a refusal.
 *
 * Deliberately no duration field. Duration is per professional × type (Decision C) and is
 * configured on S7 in change 3b.
 */
export function AppointmentTypesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();

  const { data: appointmentTypes, isPending, isError, error } = useQuery({
    queryKey: APPOINTMENT_TYPES_QUERY_KEY,
    queryFn: listAppointmentTypes,
    retry: false,
  });

  // The two reference lists, fetched together — the form cannot be filled in without both.
  const [specialties, resourceTypes] = useQueries({
    queries: [
      { queryKey: ['config', 'specialties'], queryFn: listSpecialties, retry: false },
      { queryKey: ['config', 'resource-types'], queryFn: listResourceTypes, retry: false },
    ],
  });

  const activeSpecialties = (specialties.data ?? []).filter((entry) => entry.isActive);
  const activeResourceTypes = (resourceTypes.data ?? []).filter((entry) => entry.isActive);

  const [editing, setEditing] = useState<AppointmentTypeResponse | 'new' | null>(null);
  const [name, setName] = useState('');
  const [specialtyId, setSpecialtyId] = useState('');
  const [requiredResourceTypeId, setRequiredResourceTypeId] = useState('');
  const [notice, setNotice] = useState<string | null>(null);

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: APPOINTMENT_TYPES_QUERY_KEY });

  const save = useMutation({
    mutationFn: () => {
      const input = { name, specialtyId, requiredResourceTypeId };

      return editing && editing !== 'new'
        ? updateAppointmentType(editing.id, input)
        : createAppointmentType(input);
    },
    onSuccess: () => {
      setNotice(editing === 'new' ? t('catalog.created') : t('catalog.updated'));
      setEditing(null);
      void invalidate();
    },
  });

  const toggle = useMutation({
    mutationFn: (type: AppointmentTypeResponse) =>
      setCatalogEntityActive('appointment-types', type.id, !type.isActive),
    onSuccess: (_result, type) => {
      setNotice(type.isActive ? t('catalog.deactivated') : t('catalog.reactivated'));
      void invalidate();
    },
  });

  function open(target: AppointmentTypeResponse | 'new') {
    setNotice(null);
    save.reset();
    setName(target === 'new' ? '' : target.name);
    setSpecialtyId(target === 'new' ? (activeSpecialties[0]?.id ?? '') : target.specialtyId);
    setRequiredResourceTypeId(
      target === 'new' ? (activeResourceTypes[0]?.id ?? '') : target.requiredResourceTypeId,
    );
    setEditing(target);
  }

  const canAdd = activeSpecialties.length > 0 && activeResourceTypes.length > 0;

  return (
    <div className="space-y-6">
      <CatalogHeading
        title={t('catalog.appointmentTypesTitle')}
        description={t('catalog.appointmentTypesDescription')}
      />

      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {toggle.isError ? <Alert tone="error">{describeError(toggle.error)}</Alert> : null}

      {/* Say which prerequisite is missing, rather than opening a form with an empty chooser. */}
      {canAdd ? (
        <Button onClick={() => open('new')}>{t('catalog.addAppointmentType')}</Button>
      ) : (
        <Alert tone="info">
          {activeSpecialties.length === 0
            ? t('catalog.needsActiveSpecialty')
            : t('catalog.needsActiveResourceType')}
        </Alert>
      )}

      {isPending ? (
        <p role="status" className="text-meta">
          {t('common.loading')}
        </p>
      ) : isError ? (
        <Alert tone="error">{describeError(error)}</Alert>
      ) : appointmentTypes && appointmentTypes.length > 0 ? (
        <Table>
          <TableHead>
            <TableRow>
              <TableHeaderCell>{t('catalog.name')}</TableHeaderCell>
              <TableHeaderCell>{t('catalog.specialty')}</TableHeaderCell>
              <TableHeaderCell>{t('catalog.requiredResourceType')}</TableHeaderCell>
              <TableHeaderCell>{t('catalog.columnStatus')}</TableHeaderCell>
              <TableHeaderCell>{t('catalog.columnActions')}</TableHeaderCell>
            </TableRow>
          </TableHead>
          <tbody>
            {appointmentTypes.map((type) => (
              <TableRow key={type.id}>
                <TableCell className="font-medium">{type.name}</TableCell>
                <TableCell>{type.specialtyName}</TableCell>
                <TableCell>{type.requiredResourceTypeName}</TableCell>
                <StatusCell isActive={type.isActive} />
                <ActionsCell
                  isActive={type.isActive}
                  onEdit={() => open(type)}
                  onToggle={() => {
                    setNotice(null);
                    toggle.mutate(type);
                  }}
                  busy={toggle.isPending}
                />
              </TableRow>
            ))}
          </tbody>
        </Table>
      ) : (
        <p className="text-meta">{t('catalog.empty')}</p>
      )}

      <Dialog open={editing !== null} onOpenChange={(next) => !next && setEditing(null)}>
        {editing !== null ? (
          <DialogContent
            title={
              editing === 'new'
                ? t('catalog.addAppointmentType')
                : t('catalog.editAppointmentType')
            }
          >
            <form
              className="space-y-5"
              onSubmit={(event) => {
                event.preventDefault();
                save.mutate();
              }}
            >
              <Field label={t('catalog.name')}>
                {({ id, describedBy, invalid }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    aria-invalid={invalid}
                    value={name}
                    onChange={(event) => setName(event.target.value)}
                    required
                    autoFocus
                  />
                )}
              </Field>

              <Field label={t('catalog.specialty')}>
                {({ id, describedBy }) => (
                  <Select
                    id={id}
                    aria-describedby={describedBy}
                    value={specialtyId}
                    onChange={(event) => setSpecialtyId(event.target.value)}
                    required
                  >
                    {activeSpecialties.map((specialty) => (
                      <option key={specialty.id} value={specialty.id}>
                        {specialty.name}
                      </option>
                    ))}
                  </Select>
                )}
              </Field>

              <Field label={t('catalog.requiredResourceType')}>
                {({ id, describedBy }) => (
                  <Select
                    id={id}
                    aria-describedby={describedBy}
                    value={requiredResourceTypeId}
                    onChange={(event) => setRequiredResourceTypeId(event.target.value)}
                    required
                  >
                    {activeResourceTypes.map((type) => (
                      <option key={type.id} value={type.id}>
                        {type.name}
                      </option>
                    ))}
                  </Select>
                )}
              </Field>

              {save.isError ? <Alert tone="error">{describeError(save.error)}</Alert> : null}

              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => setEditing(null)}>
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
    </div>
  );
}
