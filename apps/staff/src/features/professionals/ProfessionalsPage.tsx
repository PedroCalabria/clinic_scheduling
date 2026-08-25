import {
  Alert,
  Badge,
  Button,
  Table,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  listProfessionals,
  useApiErrorMessage,
  type ProfessionalListEntry,
} from '@clinic/shared';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CatalogHeading } from '../catalog/CatalogBits';
import { ProfessionalDetailView } from './ProfessionalDetailView';

export const PROFESSIONALS_QUERY_KEY = ['config', 'professionals'] as const;

/**
 * S7 — Professionals (docs/06-ui-surfaces.md).
 *
 * Master-detail rather than separate routes per section (design E7): assigning specialties,
 * setting durations, and defining hours are one task — "configure this person" — and splitting
 * them would make an administrator navigate three times to finish one job.
 *
 * The list is driven from users holding the professional role, not from configuration records,
 * which is design E1 showing through: listing records would hide exactly the people an
 * administrator opened this screen to configure.
 */
export function ProfessionalsPage() {
  const { t } = useTranslation();
  const describeError = useApiErrorMessage();

  const { data: professionals, isPending, isError, error } = useQuery({
    queryKey: PROFESSIONALS_QUERY_KEY,
    queryFn: listProfessionals,
    retry: false,
  });

  const [selected, setSelected] = useState<string | null>(null);

  if (selected) {
    return <ProfessionalDetailView userId={selected} onBack={() => setSelected(null)} />;
  }

  return (
    <div className="space-y-6">
      <CatalogHeading title={t('professionals.title')} description={t('professionals.description')} />

      {isPending ? (
        <p role="status" className="text-meta">
          {t('common.loading')}
        </p>
      ) : isError ? (
        <Alert tone="error">{describeError(error)}</Alert>
      ) : professionals && professionals.length > 0 ? (
        <Table>
          <TableHead>
            <TableRow>
              <TableHeaderCell>{t('professionals.columnEmail')}</TableHeaderCell>
              <TableHeaderCell>{t('professionals.columnState')}</TableHeaderCell>
              <TableHeaderCell>{t('catalog.columnActions')}</TableHeaderCell>
            </TableRow>
          </TableHead>
          <tbody>
            {professionals.map((professional) => (
              <ProfessionalRow
                key={professional.userId}
                professional={professional}
                onOpen={() => setSelected(professional.userId)}
              />
            ))}
          </tbody>
        </Table>
      ) : (
        /* Points at the screen that fixes it, since the remedy is on a different route. */
        <Alert tone="info">{t('professionals.listEmpty')}</Alert>
      )}
    </div>
  );
}

function ProfessionalRow({
  professional,
  onOpen,
}: {
  professional: ProfessionalListEntry;
  onOpen: () => void;
}) {
  const { t } = useTranslation();

  return (
    <TableRow>
      <TableCell>
        <span className="font-medium">{professional.fullName ?? professional.email}</span>
        {/* The address stays beside the name: it is what tells two people apart, and what they sign in with. */}
        {professional.fullName ? (
          <span className="ml-2 text-xs text-meta">{professional.email}</span>
        ) : null}
        {/*
          An unclaimed invitation is worth showing but is NOT a blocker: an administrator can
          and should be able to prepare a schedule before the first sign-in (design E1).
        */}
        {professional.awaitsClaim ? (
          <span className="ml-2 text-xs text-meta">{t('professionals.awaitsClaim')}</span>
        ) : null}
      </TableCell>
      <TableCell>
        <Badge tone={professional.isConfigured ? 'active' : 'pending'}>
          {professional.isConfigured
            ? t('professionals.stateConfigured')
            : t('professionals.stateUnconfigured')}
        </Badge>
        {professional.isConfigured ? (
          <span className="ml-2 text-xs text-meta">
            {t('professionals.summary', {
              specialties: professional.specialtyCount,
              durations: professional.durationCount,
              hours: professional.workingHoursCount,
            })}
          </span>
        ) : null}
      </TableCell>
      <TableCell>
        <Button variant="secondary" size="sm" onClick={onOpen}>
          {t('professionals.configure')}
        </Button>
      </TableCell>
    </TableRow>
  );
}
