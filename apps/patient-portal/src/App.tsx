import {
  Button,
  HealthPanel,
  LanguageSwitch,
  RequireAuth,
  useSession,
  useSignOut,
} from '@clinic/shared';
import { useTranslation } from 'react-i18next';
import { Link, Route, Routes, useNavigate } from 'react-router';
import { LandingPage } from './features/landing/LandingPage';
import { ProfilePage } from './features/profile/ProfilePage';

/**
 * The patient portal's surface after change 2: the public door (P1) and the patient's own
 * record (P7).
 *
 * The booking flow (P2-P6) arrives in change 5. The catch-all route stays for the same
 * reason it was there in change 1: a full reload of a deep link must render this app rather
 * than a blank page — the client half of the SPA-fallback contract Caddy serves (design D2).
 */
export function App() {
  return (
    <Routes>
      {/* Full-bleed, no shell: the landing page is the designed surface (Z2). */}
      <Route path="/" element={<LandingPage />} />

      <Route
        path="/profile"
        element={
          <RequireAuth signInPath="/">
            <Shell>
              <ProfilePage />
            </Shell>
          </RequireAuth>
        }
      />

      {/* Kept from change 1: the probe that proves this surface reaches the API. */}
      <Route
        path="/status"
        element={
          <Shell>
            <HealthPanel />
          </Shell>
        }
      />

      <Route path="*" element={<NotFoundPage />} />
    </Routes>
  );
}

function Shell({ children }: { children: React.ReactNode }) {
  const { t } = useTranslation();
  const { data: session } = useSession();
  const signOut = useSignOut();
  const navigate = useNavigate();

  return (
    <div className="min-h-screen">
      <header className="border-b border-line bg-surface-raised">
        <div className="mx-auto flex max-w-3xl flex-wrap items-center justify-between gap-4 px-6 py-4">
          <Link to="/" className="font-semibold text-heading">
            {t('portal.clinicName')}
          </Link>

          <div className="flex flex-wrap items-center gap-4">
            {session ? (
              <span className="text-sm text-meta">
                {t('portal.signedInAs', { email: session.email })}
              </span>
            ) : null}

            <LanguageSwitch />

            {session ? (
              <Button
                variant="ghost"
                size="sm"
                onClick={() =>
                  signOut.mutate(undefined, {
                    onSuccess: () => void navigate('/', { replace: true }),
                  })
                }
              >
                {t('common.signOut')}
              </Button>
            ) : null}
          </div>
        </div>
      </header>

      <main className="mx-auto max-w-3xl px-6 py-10">{children}</main>
    </div>
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
