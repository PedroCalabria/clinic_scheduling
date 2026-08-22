import {
  Alert,
  Button,
  Dialog,
  DialogContent,
  DialogFooter,
  Field,
  Input,
  Table,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  createSpecialty,
  listSpecialties,
  renameSpecialty,
  setCatalogEntityActive,
  useApiErrorMessage,
  type SpecialtyResponse,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ActionsCell, CatalogHeading, StatusCell } from './CatalogBits';

export const SPECIALTIES_QUERY_KEY = ['config', 'specialties'] as const;

/**
 * S8 — Specialties (docs/06-ui-surfaces.md).
 *
 * The simplest of the three catalog screens, and the one that establishes their shape: a
 * table showing active and inactive records distinguishably, a modal form for create and
 * edit, and every refusal rendered as its translated code rather than a raw response.
 */
export function SpecialtiesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();

  const { data: specialties, isPending, isError, error } = useQuery({
    queryKey: SPECIALTIES_QUERY_KEY,
    queryFn: listSpecialties,
    retry: false,
  });

  /** `null` means closed; a record means editing it; `'new'` means creating. */
  const [editing, setEditing] = useState<SpecialtyResponse | 'new' | null>(null);
  const [name, setName] = useState('');
  const [notice, setNotice] = useState<string | null>(null);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: SPECIALTIES_QUERY_KEY });

  const save = useMutation({
    mutationFn: () =>
      editing && editing !== 'new'
        ? renameSpecialty(editing.id, name)
        : createSpecialty(name),
    onSuccess: () => {
      setNotice(editing === 'new' ? t('catalog.created') : t('catalog.updated'));
      setEditing(null);
      void invalidate();
    },
  });

  const toggle = useMutation({
    mutationFn: (specialty: SpecialtyResponse) =>
      setCatalogEntityActive('specialties', specialty.id, !specialty.isActive),
    onSuccess: (_result, specialty) => {
      setNotice(specialty.isActive ? t('catalog.deactivated') : t('catalog.reactivated'));
      void invalidate();
    },
  });

  function open(target: SpecialtyResponse | 'new') {
    setNotice(null);
    save.reset();
    setName(target === 'new' ? '' : target.name);
    setEditing(target);
  }

  return (
    <div className="space-y-6">
      <CatalogHeading
        title={t('catalog.specialtiesTitle')}
        description={t('catalog.specialtiesDescription')}
      />

      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {/* A refused deactivation reports next to the table it acted on, never as a toast. */}
      {toggle.isError ? <Alert tone="error">{describeError(toggle.error)}</Alert> : null}

      <Button onClick={() => open('new')}>{t('catalog.addSpecialty')}</Button>

      {isPending ? (
        <p role="status" className="text-meta">
          {t('common.loading')}
        </p>
      ) : isError ? (
        <Alert tone="error">{describeError(error)}</Alert>
      ) : specialties && specialties.length > 0 ? (
        <Table>
          <TableHead>
            <TableRow>
              <TableHeaderCell>{t('catalog.name')}</TableHeaderCell>
              <TableHeaderCell>{t('catalog.columnStatus')}</TableHeaderCell>
              <TableHeaderCell>{t('catalog.columnActions')}</TableHeaderCell>
            </TableRow>
          </TableHead>
          <tbody>
            {specialties.map((specialty) => (
              <TableRow key={specialty.id}>
                <TableCell className="font-medium">{specialty.name}</TableCell>
                <StatusCell isActive={specialty.isActive} />
                <ActionsCell
                  isActive={specialty.isActive}
                  onEdit={() => open(specialty)}
                  onToggle={() => {
                    setNotice(null);
                    toggle.mutate(specialty);
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
            title={editing === 'new' ? t('catalog.addSpecialty') : t('catalog.editSpecialty')}
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
