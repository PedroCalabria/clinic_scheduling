import { Button, LanguageSwitch, useSession, useSignOut, type RoleName } from '@clinic/shared';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { NavLink, useNavigate } from 'react-router';

interface NavigationEntry {
  to: string;
  labelKey: string;
  /** Roles that see this entry. Omitted means every signed-in role. */
  roles?: readonly RoleName[];
}

/**
 * The staff console's navigation.
 *
 * Every entry below is role-scoped here AND at the API. That duplication is
 * intentional and is not a security decision: hiding an entry is a courtesy so nobody walks
 * into a refusal, while the API's policy is what actually stops them. Requesting a hidden
 * destination directly still gets `auth.forbidden` (design A8), and there is a test for it.
 */
const NAVIGATION: readonly NavigationEntry[] = [
  { to: '/', labelKey: 'staff.navHealth' },
  { to: '/blocks', labelKey: 'staff.navBlocks', roles: ['Professional'] },
  { to: '/users', labelKey: 'staff.navUsers', roles: ['Administrator'] },
  { to: '/admin/professionals', labelKey: 'staff.navProfessionals', roles: ['Administrator'] },
  { to: '/admin/specialties', labelKey: 'staff.navSpecialties', roles: ['Administrator'] },
  { to: '/admin/resources', labelKey: 'staff.navResources', roles: ['Administrator'] },
  {
    to: '/admin/appointment-types',
    labelKey: 'staff.navAppointmentTypes',
    roles: ['Administrator'],
  },
];

/**
 * The frame every staff screen mounts into — sidebar and top bar, role-conditioned
 * navigation (docs/06-ui-surfaces.md §3).
 *
 * Built in change 2 because S0 needs somewhere to land, and because every staff screen from
 * change 3 onward (S1-S10) goes inside it. Utilitarian by decision Z2: the console is for
 * people who use it all day, so it favours density and predictability over polish.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const { data: session } = useSession();
  const signOut = useSignOut();
  const navigate = useNavigate();

  const entries = NAVIGATION.filter(
    (entry) => !entry.roles || (session && entry.roles.includes(session.role)),
  );

  return (
    <div className="min-h-screen md:grid md:grid-cols-[16rem_1fr]">
      <nav
        aria-label={t('staff.navigation')}
        className="border-b border-line bg-surface-raised md:min-h-screen md:border-r md:border-b-0"
      >
        <div className="px-5 py-4 font-semibold text-heading">{t('staff.title')}</div>

        <ul className="flex gap-1 px-3 pb-3 md:flex-col md:gap-0.5">
          {entries.map((entry) => (
            <li key={entry.to}>
              <NavLink
                to={entry.to}
                end={entry.to === '/'}
                className={({ isActive }) =>
                  [
                    'block rounded-sm px-3 py-2 text-sm',
                    isActive
                      ? 'bg-primary-subtle font-semibold text-primary-strong'
                      : 'text-body hover:bg-surface',
                  ].join(' ')
                }
              >
                {t(entry.labelKey)}
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>

      <div className="flex min-w-0 flex-col">
        <header className="flex flex-wrap items-center justify-end gap-4 border-b border-line px-6 py-3">
          {session ? (
            <span className="text-sm text-meta">
              {t('staff.signedInAs', { email: session.email, role: t(`roles.${session.role}`) })}
            </span>
          ) : null}

          <LanguageSwitch />

          <Button
            variant="ghost"
            size="sm"
            onClick={() =>
              signOut.mutate(undefined, {
                onSuccess: () => void navigate('/login', { replace: true }),
              })
            }
          >
            {t('common.signOut')}
          </Button>
        </header>

        <main className="min-w-0 flex-1 px-6 py-8">{children}</main>
      </div>
    </div>
  );
}
