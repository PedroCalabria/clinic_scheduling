import { HealthPanel, LanguageSwitch } from '@clinic/shared';
import { useTranslation } from 'react-i18next';
import { Route, Routes } from 'react-router';

/**
 * The staff console's entire surface in change 1: a health probe and a catch-all.
 *
 * The app-shell with role-conditioned navigation and screens S0-S11
 * (docs/06-ui-surfaces.md) arrive from change 2 onward. The catch-all route is what makes
 * the deep-link scenario meaningful: a full reload of `/staff/anything` must render this
 * app — Caddy serves the staff `index.html`, and the router resolves the rest here.
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
        <h1 className="text-2xl font-semibold">{t('staff.title')}</h1>
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
      <p data-testid="not-found">{t('staff.notFound')}</p>
    </Shell>
  );
}
