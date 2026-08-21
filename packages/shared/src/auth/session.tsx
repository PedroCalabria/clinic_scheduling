import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { type ReactNode, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, useLocation } from 'react-router';
import {
  type ApiError,
  ApiRequestError,
  type RoleName,
  type SessionResponse,
  getSession,
  onSessionEnded,
  signOut,
} from '../api/client';

/** The one query key both apps use for "who am I".  */
export const SESSION_QUERY_KEY = ['auth', 'session'] as const;

/**
 * The server's answer to "who is signed in", as a query.
 *
 * There is deliberately no second copy of the session in React state (design A11). Holding
 * the user in a context populated at sign-in is the obvious approach and it goes stale
 * exactly when the session mechanism is designed to be correct: an administrator disables
 * an account, the API starts refusing, and the UI keeps rendering an authenticated shell.
 * Making the server authoritative on both sides keeps one story.
 */
export function useSession(): UseQueryResult<SessionResponse | null> {
  return useQuery({
    queryKey: SESSION_QUERY_KEY,
    queryFn: getSession,
    // A signed-out answer is data, not a failure to retry.
    retry: false,
    staleTime: 30_000,
  });
}

/**
 * Keeps the session query honest when the API says a session is over.
 *
 * Mounted once per app, near the root. Without it, a revoked session shows up as unrelated
 * requests failing one by one while the shell still looks signed in.
 */
export function SessionExpiryWatcher({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();

  useEffect(
    () =>
      onSessionEnded(() => {
        void queryClient.invalidateQueries({ queryKey: SESSION_QUERY_KEY });
      }),
    [queryClient],
  );

  return children;
}

/** Signs out and clears everything the previous session put in the cache. */
export function useSignOut() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: signOut,
    onSuccess: () => {
      // clear(), not invalidate(): the next user of this browser must not see the previous
      // one's data flash on screen while a refetch is in flight.
      queryClient.clear();
    },
  });
}

export interface RequireAuthProps {
  /** Where to send a visitor who is not signed in. */
  signInPath: string;
  /** Roles allowed here. Omitted means any signed-in user. */
  allow?: readonly RoleName[];
  /** Where to send a signed-in user whose role is not allowed. */
  fallbackPath?: string;
  /** Where to send a user who must replace their password first. */
  changePasswordPath?: string;
  children: ReactNode;
}

/**
 * The route guard both apps use.
 *
 * It is an affordance, not a security boundary — every one of these decisions is made again
 * by the API, which is the only place it counts. What this buys is that an unauthenticated
 * visitor sees a sign-in screen instead of a screen full of failed requests, including on a
 * full page load of a deep link (the client half of change 1's SPA-fallback contract).
 */
export function RequireAuth({
  signInPath,
  allow,
  fallbackPath,
  changePasswordPath,
  children,
}: RequireAuthProps) {
  const { data: session, isPending } = useSession();
  const location = useLocation();
  const { t } = useTranslation();

  if (isPending) {
    // Announced rather than silent: a guard that resolves slowly on a cold load would
    // otherwise be a blank screen to a screen-reader user.
    return (
      <p role="status" className="p-8 text-meta">
        {t('common.loading')}
      </p>
    );
  }

  if (!session) {
    // The attempted destination travels along, so sign-in can return the visitor to it.
    return <Navigate to={signInPath} replace state={{ from: location.pathname }} />;
  }

  if (session.mustChangePassword && changePasswordPath && location.pathname !== changePasswordPath) {
    return <Navigate to={changePasswordPath} replace />;
  }

  if (allow && !allow.includes(session.role)) {
    return <Navigate to={fallbackPath ?? signInPath} replace />;
  }

  return children;
}

/**
 * Turns an API failure into a translated sentence.
 *
 * The API returns `{ code, params }` and never prose (Decision I), so this is the single
 * place a code becomes words. Dots are replaced with underscores because i18next reads a
 * dot as nesting, and `auth.session_expired` is one key rather than a path.
 */
export function useApiErrorMessage(): (error: unknown) => string | undefined {
  const { t } = useTranslation();

  return (error: unknown) => {
    if (error === null || error === undefined) {
      return undefined;
    }

    const apiError: ApiError =
      error instanceof ApiRequestError ? error.error : { code: 'server.unexpected' };

    return translateApiError(apiError, t);
  };
}

/**
 * Translates a code that arrived somewhere other than a thrown error — the query parameter
 * the Google callback uses to report a refusal, for instance.
 */
export function useApiCodeMessage(): (code: string | null | undefined) => string | undefined {
  const { t } = useTranslation();

  return (code) => (code ? translateApiError({ code }, t) : undefined);
}

function translateApiError(
  error: ApiError,
  t: (key: string | string[], options?: Record<string, unknown>) => string,
): string {
  const key = `errors.${error.code.replace(/\./g, '_')}`;

  // The fallback matters: a code the frontend has never heard of must still produce a
  // sentence rather than showing the user a raw key.
  return t([key, 'errors.server_unexpected'], { ...error.params });
}
