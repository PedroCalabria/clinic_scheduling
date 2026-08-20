import { HealthPanel, LanguageSwitch } from '@clinic/shared';
import { useTranslation } from 'react-i18next';
import { Route, Routes } from 'react-router';

/**
 * The patient portal's entire surface in change 1: a health probe and a catch-all.
 *
 * The real screens (P1-P7, docs/06-ui-surfaces.md) arrive from change 2 onward. The
 * catch-all route exists so a deep-link reload under this base path renders the app
 * rather than a blank page — the client half of the SPA-fallback contract Caddy serves
 * (design D2).
 */
export function App() {
  return (
    <Routes>
      <Route path="/" element={<HealthPage />} />
      <Route path="*" element={<NotFoundPage />} />
    </Routes>
  );
}

function Shell({ children }: { children: React.ReactNode }) {
  const { t } = useTranslation();

  return (
    <main className="mx-auto max-w-2xl p-8">
      <header className="mb-6 flex items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold">{t('portal.title')}</h1>
        <LanguageSwitch />
      </header>
      {children}
    </main>
  );
}

function HealthPage() {
  return (
    <Shell>
      <HealthPanel />
    </Shell>
  );
}

function NotFoundPage() {
  const { t } = useTranslation();

  return (
    <Shell>
      <p data-testid="not-found">{t('portal.notFound')}</p>
    </Shell>
  );
}
