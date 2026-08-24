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
import { BookingSearchPage } from './features/booking/BookingSearchPage';
import { BookingSuccessPage } from './features/booking/BookingSuccessPage';
import { ConfirmBookingPage } from './features/booking/ConfirmBookingPage';
import { LandingPage } from './features/landing/LandingPage';
import { ProfilePage } from './features/profile/ProfilePage';

/**
 * The patient portal's surface after change 2: the public door (P1) and the patient's own
 * record (P7).
 *
 * The booking flow's first three screens arrive with `booking-core`: P2 searches real
 * availability, P3 confirms and commits, P4 reassures. P5 and P6 — the patient's own list, and
 * reschedule or cancel — belong to `booking-lifecycle`, which is why P4's onward link points at the
 * profile for now.
 *
 * All three are behind `RequireAuth`: availability is readable by any authenticated caller and
 * booking is the patient's own act, so there is no anonymous browsing of the schedule.
 *
 * The catch-all route stays for the same reason it was there in change 1: a full reload of a deep
 * link must render this app rather than a blank page — the client half of the SPA-fallback contract
 * Caddy serves (design D2). It matters more now, because P2 and P3 keep their whole state in the
 * query string and are meant to survive a reload.
 */
export function App() {
  return (
    <Routes>
      {/* Full-bleed, no shell: the landing page is the designed surface (Z2). */}
      <Route path="/" element={<LandingPage />} />

      <Route
        path="/book"
        element={
          <RequireAuth signInPath="/">
            <Shell>
              <BookingSearchPage />
            </Shell>
          </RequireAuth>
        }
      />

      <Route
        path="/book/confirm"
        element={
          <RequireAuth signInPath="/">
            <Shell>
              <ConfirmBookingPage />
            </Shell>
          </RequireAuth>
        }
      />

      <Route
        path="/book/success"
        element={
          <RequireAuth signInPath="/">
            <Shell>
              <BookingSuccessPage />
            </Shell>
          </RequireAuth>
        }
      />

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
        <div className="mx-auto flex max-w-4xl flex-wrap items-center justify-between gap-4 px-6 py-4">
          <Link to="/" className="font-semibold text-heading">
            {t('portal.clinicName')}
          </Link>

          <div className="flex flex-wrap items-center gap-4">
            {session ? (
              <>
                {/* The one action this portal exists for, reachable from every screen in it. */}
                <Link to="/book" className="text-sm font-medium text-primary underline">
                  {t('portal.bookAppointment')}
                </Link>

                <Link to="/profile" className="text-sm text-meta underline">
                  {t('portal.myProfile')}
                </Link>

                <span className="text-sm text-meta">
                  {t('portal.signedInAs', { email: session.email })}
                </span>
              </>
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

      <main className="mx-auto max-w-4xl px-6 py-10">{children}</main>
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
