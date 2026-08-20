import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { DATABASE_CHECK, getHealth } from '../api/client';

/**
 * Renders the deployed system's health, including database connectivity.
 *
 * Lives in `@clinic/shared` because both frontends render exactly this probe in change 1
 * (walking-skeleton) — and because having both apps consume a real shared React component
 * is what proves the workspace link works, rather than merely asserting it.
 *
 * This is a probe, not a product surface: the design system is deliberately not applied
 * yet (see the proposal's non-goals).
 */
export function HealthPanel() {
  const { t } = useTranslation();

  const { data, isPending, isError } = useQuery({
    queryKey: ['health'],
    queryFn: getHealth,
    retry: false,
  });

  if (isPending) {
    return <p className="text-slate-500">{t('common.loading')}</p>;
  }

  // A thrown error means the API itself was unreachable. An *unhealthy* API is not an
  // error here — it answers with 503 and a body, which the branch below renders.
  if (isError || !data) {
    return (
      <p role="alert" className="text-red-700">
        {t('common.unreachable')}
      </p>
    );
  }

  const databaseStatus = data.checks[DATABASE_CHECK];

  return (
    <dl className="space-y-2">
      <div className="flex gap-2">
        <dt className="font-medium">{t('common.systemStatus')}:</dt>
        <dd data-testid="overall-status">{translateStatus(data.status, t)}</dd>
      </div>
      <div className="flex gap-2">
        <dt className="font-medium">{t('common.database')}:</dt>
        <dd data-testid="database-status">{translateStatus(databaseStatus, t)}</dd>
      </div>
    </dl>
  );
}

/**
 * Maps the API's status string to a translated label.
 *
 * The API returns a stable, untranslated token (`Healthy`) and the frontend owns the
 * wording — the same division as the `{ code, params }` error contract (Decision I).
 */
function translateStatus(status: string | undefined, t: (key: string) => string): string {
  switch (status) {
    case 'Healthy':
      return t('common.statusHealthy');
    case 'Degraded':
      return t('common.statusDegraded');
    case 'Unhealthy':
      return t('common.statusUnhealthy');
    default:
      return t('common.statusUnhealthy');
  }
}
