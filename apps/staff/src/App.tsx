import { HealthPanel, RequireAuth } from '@clinic/shared';
import { useTranslation } from 'react-i18next';
import { Route, Routes } from 'react-router';
import { AppShell } from './components/AppShell';
import { BlockedTimePage } from './features/blocks/BlockedTimePage';
import { CalendarConnectionPage } from './features/calendar/CalendarConnectionPage';
import { AppointmentTypesPage } from './features/catalog/AppointmentTypesPage';
import { ResourcesPage } from './features/catalog/ResourcesPage';
import { SpecialtiesPage } from './features/catalog/SpecialtiesPage';
import { ProfessionalsPage } from './features/professionals/ProfessionalsPage';
import { ChangePasswordPage } from './features/password/ChangePasswordPage';
import { DeskBookingPage } from './features/schedule/DeskBookingPage';
import { DayViewPage, MySchedulePage } from './features/schedule/ScheduleDay';
import { SignInPage } from './features/signin/SignInPage';
import { UsersPage } from './features/users/UsersPage';

/**
 * The staff console: sign-in (S0), the app-shell, users (S11), and the clinic catalog
 * (S8-S10, added by `clinic-catalog`).
 *
 * S3 arrived with change 4, S1/S4/S5 with change 5c, and S2 with change 6a; S6 comes with
 * `calendar-inbound` and mounts into the same shell. The catch-all route
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

      {/*
        The catalog (S8-S10). Administrator-only in the guard AND at the API — the guard is
        the courtesy, the API's policy is the boundary.
      */}
      <Route
        path="/admin/specialties"
        element={
          <Guarded allow={['Administrator']}>
            <SpecialtiesPage />
          </Guarded>
        }
      />

      <Route
        path="/admin/resources"
        element={
          <Guarded allow={['Administrator']}>
            <ResourcesPage />
          </Guarded>
        }
      />

      <Route
        path="/admin/appointment-types"
        element={
          <Guarded allow={['Administrator']}>
            <AppointmentTypesPage />
          </Guarded>
        }
      />

      {/* S7 — a professional's clinical configuration (change 3b). */}
      <Route
        path="/admin/professionals"
        element={
          <Guarded allow={['Administrator']}>
            <ProfessionalsPage />
          </Guarded>
        }
      />

      {/*
        S3 — blocked time (change 4). The first professional-role screen: every route above is
        an administrator's. Professional-only in the guard AND at the API, same as the catalog —
        the guard is the courtesy, the API's policy is the boundary.
      */}
      <Route
        path="/blocks"
        element={
          <Guarded allow={['Professional']}>
            <BlockedTimePage />
          </Guarded>
        }
      />

      {/*
        S1, S4 and S5 (change 5c) — the first staff screens that show an appointment, and the
        first that show a patient's name to somebody who is not that patient. Role-scoped here AND
        at the API: the guard is the courtesy, the API's policy is the boundary.
      */}
      <Route
        path="/schedule"
        element={
          <Guarded allow={['Professional']}>
            <MySchedulePage />
          </Guarded>
        }
      />

      <Route
        path="/day"
        element={
          <Guarded allow={['FrontDesk', 'Administrator']}>
            <DayViewPage />
          </Guarded>
        }
      />

      <Route
        path="/book"
        element={
          <Guarded allow={['FrontDesk', 'Administrator']}>
            <DeskBookingPage />
          </Guarded>
        }
      />

      {/*
        S2 — the professional's own calendar connection (change 6a). Professional-only in the
        guard AND at the API: an administrator cannot connect a calendar on somebody's behalf,
        because the grant is the professional's to give.
      */}
      <Route
        path="/calendar"
        element={
          <Guarded allow={['Professional']}>
            <CalendarConnectionPage />
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
