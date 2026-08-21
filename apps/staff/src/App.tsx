import { HealthPanel, RequireAuth } from '@clinic/shared';
import { useTranslation } from 'react-i18next';
import { Route, Routes } from 'react-router';
import { AppShell } from './components/AppShell';
import { ChangePasswordPage } from './features/password/ChangePasswordPage';
import { SignInPage } from './features/signin/SignInPage';
import { UsersPage } from './features/users/UsersPage';

/**
 * The staff console after change 2: sign-in (S0), the app-shell, and users (S11).
 *
 * S1-S10 arrive from change 3 onward and mount into the same shell. The catch-all route
 * stays for the reason it existed in change 1: a full reload of `/staff/anything` must render
 * this app, which is the client half of Caddy's per-prefix SPA fallback (design D2).
 */
export function App() {
  return (
    <Routes>
      <Route path="/login" element={<SignInPage />} />

      {/*
        Outside the shell and outside the role guards: an account that has to replace its
        password can reach nothing else, so wrapping this in navigation it cannot use would
        only be misleading (design A6).
      */}
      <Route
        path="/password"
        element={
          <RequireAuth signInPath="/login">
            <ChangePasswordPage />
          </RequireAuth>
        }
      />

      <Route
        path="/"
        element={
          <Guarded>
            <HealthPanel />
          </Guarded>
        }
      />

      <Route
        path="/users"
        element={
          <Guarded allow={['Administrator']}>
            <UsersPage />
          </Guarded>
        }
      />

      <Route path="*" element={<Guarded><NotFoundPage /></Guarded>} />
    </Routes>
  );
}

/**
 * Everything behind sign-in shares the same guard and the same frame.
 *
 * One wrapper rather than repeating both per route: a route that forgot the guard would look
 * exactly like one that meant to be public.
 */
function Guarded({
  allow,
  children,
}: {
  allow?: readonly ('Patient' | 'Professional' | 'FrontDesk' | 'Administrator')[];
  children: React.ReactNode;
}) {
  return (
    <RequireAuth signInPath="/login" allow={allow} fallbackPath="/" changePasswordPath="/password">
      <AppShell>{children}</AppShell>
    </RequireAuth>
  );
}

function NotFoundPage() {
  const { t } = useTranslation();

  return <p data-testid="not-found">{t('staff.notFound')}</p>;
}
