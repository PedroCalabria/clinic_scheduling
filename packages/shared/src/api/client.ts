/**
 * The API client both frontends share.
 *
 * Requests use a RELATIVE path (design D3): both SPAs are served from the same origin as
 * the API (Caddy routes `/api/*` to it), so there is no API base URL to configure, no
 * CORS in any environment, and no build-time environment coupling. In dev, each Vite dev
 * server proxies `/api` to the API so the identical code path works without Caddy.
 */

const API_PREFIX = '/api';

/** Cookie the API issues for double-submit request-forgery protection (design A3). */
const CSRF_COOKIE = 'clinic.csrf';

/** Header the same token must be echoed in on every unsafe request. */
const CSRF_HEADER = 'X-CSRF-Token';

const UNSAFE_METHODS = ['POST', 'PUT', 'PATCH', 'DELETE'];

/** The API's only error shape (Decision I — see docs/07-error-codes.md). */
export interface ApiError {
  /** Stable, namespaced code, e.g. `booking.slot_taken`. Never translated prose. */
  code: string;
  /** Flat values interpolated into the translated message. */
  params?: Record<string, unknown>;
}

/**
 * Thrown when the API returns a failure the caller is not expected to render as data.
 * Carries the `code` so the UI can map it to an i18n key.
 */
export class ApiRequestError extends Error {
  // Declared as fields rather than constructor parameter properties: the workspace
  // enables `erasableSyntaxOnly`, so TypeScript-only syntax that emits runtime code
  // is rejected. Keeps the build type-strippable.
  readonly status: number;
  readonly error: ApiError;

  constructor(status: number, error: ApiError) {
    super(`API request failed (${status}): ${error.code}`);
    this.name = 'ApiRequestError';
    this.status = status;
    this.error = error;
  }
}

const UNEXPECTED: ApiError = { code: 'server.unexpected' };

async function readError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as unknown;

    if (typeof body === 'object' && body !== null && typeof (body as ApiError).code === 'string') {
      return body as ApiError;
    }
  } catch {
    // Non-JSON or empty body (a proxy error page, for instance) — fall through.
  }

  return UNEXPECTED;
}

function readCsrfToken(): string | undefined {
  // The CSRF cookie is deliberately readable by scripts; echoing it in a header is the
  // whole mechanism (design A3). The session cookie, by contrast, is HttpOnly and this
  // code can never see it.
  const match = document.cookie.match(new RegExp(`(?:^|; )${CSRF_COOKIE}=([^;]*)`));

  return match ? decodeURIComponent(match[1]) : undefined;
}

type SessionEndedListener = () => void;

const sessionEndedListeners = new Set<SessionEndedListener>();

/**
 * Subscribes to "the server says this session is over".
 *
 * The frontend keeps no copy of the session (design A11), so when the API refuses with
 * `auth.session_expired` the only correct response is to stop believing whatever is on
 * screen. Both apps use this to invalidate the session query and route to sign-in, which is
 * what turns a revoked session into a visible, translated sign-out instead of a page of
 * silently failing requests.
 */
export function onSessionEnded(listener: SessionEndedListener): () => void {
  sessionEndedListeners.add(listener);

  return () => sessionEndedListeners.delete(listener);
}

function notifySessionEnded(): void {
  for (const listener of sessionEndedListeners) {
    listener();
  }
}

interface RequestOptions extends RequestInit {
  /** Status codes whose body is meaningful data rather than an error. Defaults to 2xx only. */
  acceptStatuses?: readonly number[];
  /**
   * Suppresses the session-ended signal for this call.
   *
   * Exactly one caller needs it: the session probe itself. A `401` there is the answer
   * ("you are signed out"), not an event — announcing it would fire the signal on every
   * anonymous page load.
   */
  expectUnauthorized?: boolean;
}

async function apiFetch<T>(
  path: string,
  { acceptStatuses, expectUnauthorized, ...init }: RequestOptions = {},
): Promise<T | undefined> {
  const method = (init.method ?? 'GET').toUpperCase();
  const headers = new Headers(init.headers);

  headers.set('Accept', 'application/json');

  if (UNSAFE_METHODS.includes(method)) {
    const csrf = readCsrfToken();

    if (csrf) {
      headers.set(CSRF_HEADER, csrf);
    }

    if (init.body !== undefined && !headers.has('Content-Type')) {
      headers.set('Content-Type', 'application/json');
    }
  }

  const response = await fetch(`${API_PREFIX}${path}`, {
    ...init,
    method,
    headers,
    // Explicit rather than relying on the default: the session lives in a cookie, and a
    // future change to the fetch defaults must not quietly sign everyone out.
    credentials: 'same-origin',
  });

  const accepted = response.ok || (acceptStatuses?.includes(response.status) ?? false);

  if (!accepted) {
    const error = await readError(response);

    if (response.status === 401 && !expectUnauthorized) {
      notifySessionEnded();
    }

    throw new ApiRequestError(response.status, error);
  }

  if (response.status === 204 || response.headers.get('Content-Length') === '0') {
    return undefined;
  }

  return (await response.json()) as T;
}

/** Aggregate health of the deployed system. Mirrors the API's response contract. */
export interface HealthResponse {
  /** `Healthy`, `Degraded`, or `Unhealthy`. */
  status: string;
  /** Per-check status keyed by check name, e.g. `{ database: 'Healthy' }`. */
  checks: Record<string, string>;
}

/** Name of the database check in {@link HealthResponse.checks}. */
export const DATABASE_CHECK = 'database';

/**
 * Fetches `/api/health`.
 *
 * 503 is accepted rather than thrown: an unhealthy system is exactly what this call is
 * meant to report, and the health page renders that state instead of an error. Anything
 * else (404, 500, an unreachable proxy) is a genuine failure and throws.
 */
export async function getHealth(): Promise<HealthResponse> {
  return (await apiFetch<HealthResponse>('/health', { acceptStatuses: [503] }))!;
}

// --- Identity & session -------------------------------------------------------------

/** The roles the API reports. Mirrors `Clinic.Domain.Identity.Role`. */
export type RoleName = 'Patient' | 'Professional' | 'FrontDesk' | 'Administrator';

/** What the server says about the current session (design A11). */
export interface SessionResponse {
  email: string;
  role: RoleName;
  /** True while a bootstrap or administrator-set password has not been replaced. */
  mustChangePassword: boolean;
}

/**
 * Asks who the caller is.
 *
 * Returns `null` for a signed-out caller rather than throwing: "nobody is signed in" is a
 * normal answer that route guards branch on, not an exception.
 */
export async function getSession(): Promise<SessionResponse | null> {
  try {
    return (await apiFetch<SessionResponse>('/auth/session', { expectUnauthorized: true }))!;
  } catch (error) {
    if (error instanceof ApiRequestError && error.status === 401) {
      return null;
    }

    throw error;
  }
}

export function signIn(email: string, password: string): Promise<SessionResponse | undefined> {
  return apiFetch<SessionResponse>('/auth/sign-in', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  });
}

export function signOut(): Promise<undefined> {
  return apiFetch<undefined>('/auth/sign-out', { method: 'POST' }) as Promise<undefined>;
}

export function changePassword(
  currentPassword: string,
  newPassword: string,
): Promise<SessionResponse | undefined> {
  return apiFetch<SessionResponse>('/auth/password', {
    method: 'POST',
    body: JSON.stringify({ currentPassword, newPassword }),
  });
}

/**
 * Where to send the browser to sign in with Google.
 *
 * A full navigation, not a fetch: the flow is a server-side redirect that ends by setting a
 * cookie, and an XHR cannot follow a caller through Google's consent screen.
 */
export function googleSignInUrl(returnTo: string): string {
  return `${API_PREFIX}/auth/google/start?returnTo=${encodeURIComponent(returnTo)}`;
}

/** Query parameter the callback uses to report a failed sign-in. */
export const AUTH_ERROR_PARAM = 'authError';

// --- Patient profile & consents (P7) ------------------------------------------------

export interface ConsentResponse {
  type: string;
  version: string;
  grantedAtUtc: string;
  revokedAtUtc: string | null;
  active: boolean;
}

export interface PatientProfileResponse {
  fullName: string;
  contactEmail: string;
  contactPhone: string | null;
  consents: ConsentResponse[];
}

export async function getMyProfile(): Promise<PatientProfileResponse> {
  return (await apiFetch<PatientProfileResponse>('/patients/me'))!;
}

export async function updateMyProfile(
  fullName: string,
  contactPhone: string | null,
): Promise<PatientProfileResponse> {
  return (await apiFetch<PatientProfileResponse>('/patients/me', {
    method: 'PUT',
    body: JSON.stringify({ fullName, contactPhone }),
  }))!;
}

export async function revokeConsent(type: string): Promise<ConsentResponse> {
  return (await apiFetch<ConsentResponse>(`/patients/me/consents/${type}/revoke`, {
    method: 'POST',
  }))!;
}

// --- Staff accounts (S11) -----------------------------------------------------------

export interface StaffAccountResponse {
  id: string;
  email: string;
  role: RoleName;
  status: string;
  authProvider: 'Internal' | 'Google';
  /** A professional invitation nobody has claimed yet. */
  awaitsClaim: boolean;
}

export async function listStaffAccounts(): Promise<StaffAccountResponse[]> {
  return (await apiFetch<StaffAccountResponse[]>('/staff-accounts'))!;
}

export async function createStaffAccount(input: {
  email: string;
  role: RoleName;
  password?: string;
}): Promise<StaffAccountResponse> {
  return (await apiFetch<StaffAccountResponse>('/staff-accounts', {
    method: 'POST',
    body: JSON.stringify(input),
  }))!;
}

export function disableStaffAccount(id: string): Promise<undefined> {
  return apiFetch<undefined>(`/staff-accounts/${id}/disable`, { method: 'POST' }) as Promise<undefined>;
}
