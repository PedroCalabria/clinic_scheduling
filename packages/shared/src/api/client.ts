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

// --- Clinic catalog (S8-S10) --------------------------------------------------------

/**
 * A catalog entity's lifecycle is one reversible flag (design D1), so `isActive` is all the
 * screens need — the API deliberately does not ship the retirement timestamp.
 */
export interface SpecialtyResponse {
  id: string;
  name: string;
  isActive: boolean;
}

export interface ResourceTypeResponse {
  id: string;
  name: string;
  /** Turnaround minutes kept out of the bookable window (F1). */
  bufferMinutes: number;
  isActive: boolean;
}

export interface ResourceResponse {
  id: string;
  name: string;
  resourceTypeId: string;
  resourceTypeName: string;
  isActive: boolean;
}

/** No duration: that is per professional × type, and lives on change 3b's junction. */
export interface AppointmentTypeResponse {
  id: string;
  name: string;
  specialtyId: string;
  specialtyName: string;
  requiredResourceTypeId: string;
  requiredResourceTypeName: string;
  isActive: boolean;
}

/** The four catalog collections, as their route segments. */
export type CatalogCollection =
  | 'specialties'
  | 'resource-types'
  | 'resources'
  | 'appointment-types';

/**
 * Retires or restores a catalog entity.
 *
 * One function for all four kinds because the call is genuinely identical — the rules that
 * differ per entity are enforced server-side, which is where they belong. The refusal arrives
 * as `config.in_use`, `config.duplicate_name`, or `config.not_found` and the screen
 * translates it.
 */
export function setCatalogEntityActive(
  collection: CatalogCollection,
  id: string,
  active: boolean,
): Promise<undefined> {
  const action = active ? 'reactivate' : 'deactivate';

  return apiFetch<undefined>(`/config/${collection}/${id}/${action}`, {
    method: 'POST',
  }) as Promise<undefined>;
}

export async function listSpecialties(): Promise<SpecialtyResponse[]> {
  return (await apiFetch<SpecialtyResponse[]>('/config/specialties'))!;
}

export async function createSpecialty(name: string): Promise<SpecialtyResponse> {
  return (await apiFetch<SpecialtyResponse>('/config/specialties', {
    method: 'POST',
    body: JSON.stringify({ name }),
  }))!;
}

export async function renameSpecialty(id: string, name: string): Promise<SpecialtyResponse> {
  return (await apiFetch<SpecialtyResponse>(`/config/specialties/${id}`, {
    method: 'PUT',
    body: JSON.stringify({ name }),
  }))!;
}

export async function listResourceTypes(): Promise<ResourceTypeResponse[]> {
  return (await apiFetch<ResourceTypeResponse[]>('/config/resource-types'))!;
}

export async function createResourceType(input: {
  name: string;
  bufferMinutes: number;
}): Promise<ResourceTypeResponse> {
  return (await apiFetch<ResourceTypeResponse>('/config/resource-types', {
    method: 'POST',
    body: JSON.stringify(input),
  }))!;
}

export async function updateResourceType(
  id: string,
  input: { name: string; bufferMinutes: number },
): Promise<ResourceTypeResponse> {
  return (await apiFetch<ResourceTypeResponse>(`/config/resource-types/${id}`, {
    method: 'PUT',
    body: JSON.stringify(input),
  }))!;
}

export async function listResources(): Promise<ResourceResponse[]> {
  return (await apiFetch<ResourceResponse[]>('/config/resources'))!;
}

export async function createResource(input: {
  name: string;
  resourceTypeId: string;
}): Promise<ResourceResponse> {
  return (await apiFetch<ResourceResponse>('/config/resources', {
    method: 'POST',
    body: JSON.stringify(input),
  }))!;
}

export async function updateResource(
  id: string,
  input: { name: string; resourceTypeId: string },
): Promise<ResourceResponse> {
  return (await apiFetch<ResourceResponse>(`/config/resources/${id}`, {
    method: 'PUT',
    body: JSON.stringify(input),
  }))!;
}

export async function listAppointmentTypes(): Promise<AppointmentTypeResponse[]> {
  return (await apiFetch<AppointmentTypeResponse[]>('/config/appointment-types'))!;
}

export async function createAppointmentType(input: {
  name: string;
  specialtyId: string;
  requiredResourceTypeId: string;
}): Promise<AppointmentTypeResponse> {
  return (await apiFetch<AppointmentTypeResponse>('/config/appointment-types', {
    method: 'POST',
    body: JSON.stringify(input),
  }))!;
}

export async function updateAppointmentType(
  id: string,
  input: { name: string; specialtyId: string; requiredResourceTypeId: string },
): Promise<AppointmentTypeResponse> {
  return (await apiFetch<AppointmentTypeResponse>(`/config/appointment-types/${id}`, {
    method: 'PUT',
    body: JSON.stringify(input),
  }))!;
}

// --- Professional configuration (S7) ------------------------------------------------

/**
 * Keyed by `userId`, not by a professional id.
 *
 * That is deliberate and worth knowing: S7 lists users holding the professional role, and the
 * configuration record is created on the first save. A caller should not have to know whether
 * it exists yet.
 */
export interface ProfessionalListEntry {
  userId: string;
  email: string;
  /** False for an invited professional nobody has configured yet. */
  isConfigured: boolean;
  /** True while the invitation has not been claimed by a first Google sign-in. */
  awaitsClaim: boolean;
  isActive: boolean;
  specialtyCount: number;
  durationCount: number;
  workingHoursCount: number;
}

export interface HeldSpecialty {
  specialtyId: string;
  specialtyName: string;
}

export interface ConfiguredDuration {
  appointmentTypeId: string;
  appointmentTypeName: string;
  specialtyId: string;
  specialtyName: string;
  durationMinutes: number;
}

/** Times are `"HH:mm"` and dates `"yyyy-MM-dd"` — wall clock, never an instant. */
export interface WorkingHoursSegment {
  id: string;
  dayOfWeek: string;
  startTime: string;
  endTime: string;
  effectiveFrom: string;
  effectiveTo: string | null;
}

export interface WorkingHoursOverride {
  id: string;
  date: string;
  /** Null on both when the professional is unavailable all day. */
  startTime: string | null;
  endTime: string | null;
}

export interface ProfessionalDetail {
  userId: string;
  email: string;
  isConfigured: boolean;
  awaitsClaim: boolean;
  specialties: HeldSpecialty[];
  durations: ConfiguredDuration[];
  workingHours: WorkingHoursSegment[];
  exceptions: WorkingHoursOverride[];
}

/** The weekdays a working-hour segment may name, in the order a week is read. */
export const WEEKDAYS = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
] as const;

export type Weekday = (typeof WEEKDAYS)[number];

export async function listProfessionals(): Promise<ProfessionalListEntry[]> {
  return (await apiFetch<ProfessionalListEntry[]>('/config/professionals'))!;
}

export async function getProfessional(userId: string): Promise<ProfessionalDetail> {
  return (await apiFetch<ProfessionalDetail>(`/config/professionals/${userId}`))!;
}

export function grantSpecialty(userId: string, specialtyId: string): Promise<undefined> {
  return apiFetch<undefined>(`/config/professionals/${userId}/specialties`, {
    method: 'POST',
    body: JSON.stringify({ specialtyId }),
  }) as Promise<undefined>;
}

export function revokeSpecialty(userId: string, specialtyId: string): Promise<undefined> {
  return apiFetch<undefined>(
    `/config/professionals/${userId}/specialties/${specialtyId}/revoke`,
    { method: 'POST' },
  ) as Promise<undefined>;
}

export function setDuration(
  userId: string,
  input: { appointmentTypeId: string; durationMinutes: number },
): Promise<undefined> {
  return apiFetch<undefined>(`/config/professionals/${userId}/durations`, {
    method: 'PUT',
    body: JSON.stringify(input),
  }) as Promise<undefined>;
}

export function clearDuration(userId: string, appointmentTypeId: string): Promise<undefined> {
  return apiFetch<undefined>(
    `/config/professionals/${userId}/durations/${appointmentTypeId}/clear`,
    { method: 'POST' },
  ) as Promise<undefined>;
}

export function defineWorkingHours(
  userId: string,
  input: {
    dayOfWeek: string;
    startTime: string;
    endTime: string;
    effectiveFrom: string;
    effectiveTo: string | null;
  },
): Promise<undefined> {
  return apiFetch<undefined>(`/config/professionals/${userId}/working-hours`, {
    method: 'POST',
    body: JSON.stringify(input),
  }) as Promise<undefined>;
}

export function retireWorkingHours(userId: string, segmentId: string): Promise<undefined> {
  return apiFetch<undefined>(
    `/config/professionals/${userId}/working-hours/${segmentId}/retire`,
    { method: 'POST' },
  ) as Promise<undefined>;
}

export function defineException(
  userId: string,
  input: { date: string; startTime?: string; endTime?: string },
): Promise<undefined> {
  return apiFetch<undefined>(`/config/professionals/${userId}/exceptions`, {
    method: 'POST',
    body: JSON.stringify(input),
  }) as Promise<undefined>;
}

export function retireException(userId: string, exceptionId: string): Promise<undefined> {
  return apiFetch<undefined>(
    `/config/professionals/${userId}/exceptions/${exceptionId}/retire`,
    { method: 'POST' },
  ) as Promise<undefined>;
}

// --- Blocked time (S3) --------------------------------------------------------------

/**
 * A professional's own unavailability.
 *
 * Times are clinic wall clock (`"2026-08-25T14:00"`), not instants, and deliberately so: a
 * `datetime-local` input has no zone to give, and the server owns the conversion using the
 * clinic's configured timezone. That keeps zone arithmetic out of the browser entirely.
 */
export interface TimeBlockResponse {
  id: string;
  startsAt: string;
  endsAt: string;
  /** Retired blocks stay listed, and stop removing availability. */
  isActive: boolean;
}

export interface TimeBlockListResponse {
  /** The IANA zone the times above are expressed in, so the screen can say which it means. */
  timezone: string;
  blocks: TimeBlockResponse[];
}

/** No professional: a new block always belongs to the caller. */
export interface SaveTimeBlockInput {
  startsAt: string;
  endsAt: string;
}

export async function listMyBlocks(): Promise<TimeBlockListResponse> {
  return (await apiFetch<TimeBlockListResponse>('/blocks'))!;
}

export async function createMyBlock(input: SaveTimeBlockInput): Promise<TimeBlockResponse> {
  return (await apiFetch<TimeBlockResponse>('/blocks', {
    method: 'POST',
    body: JSON.stringify(input),
  }))!;
}

export async function updateMyBlock(
  id: string,
  input: SaveTimeBlockInput,
): Promise<TimeBlockResponse> {
  return (await apiFetch<TimeBlockResponse>(`/blocks/${id}`, {
    method: 'PUT',
    body: JSON.stringify(input),
  }))!;
}

export function setMyBlockActive(id: string, active: boolean): Promise<undefined> {
  const action = active ? 'restore' : 'retire';

  return apiFetch<undefined>(`/blocks/${id}/${action}`, { method: 'POST' }) as Promise<undefined>;
}

// --- Availability (change 4) --------------------------------------------------------

/**
 * One time an appointment could be placed.
 *
 * UTC instants, unlike a block's wall clock above: the consumer is a booking flow that has to
 * reason about real time.
 */
export interface AvailabilitySlotResponse {
  professionalId: string;
  /**
   * The room or machine that satisfies this slot.
   *
   * An explanation, not a reservation: by the time a patient confirms, it may be taken. Booking
   * assigns the resource server-side, so do NOT send this back expecting it to be honoured.
   */
  resourceId: string;
  start: string;
  end: string;
}

export interface AvailabilityResponse {
  appointmentTypeId: string;
  from: string;
  to: string;
  timezone: string;
  slots: AvailabilitySlotResponse[];
}

/**
 * Asks what is free. Omitting `professionalId` asks about every professional qualified for the
 * appointment type.
 *
 * No consumer screen yet: P2 arrives with change 5. Declared here so the contract lives beside
 * every other one rather than being rediscovered.
 */
export async function getAvailability(input: {
  appointmentTypeId: string;
  from: string;
  to: string;
  professionalId?: string;
}): Promise<AvailabilityResponse> {
  const query = new URLSearchParams({
    appointmentTypeId: input.appointmentTypeId,
    from: input.from,
    to: input.to,
  });

  if (input.professionalId) {
    query.set('professionalId', input.professionalId);
  }

  return (await apiFetch<AvailabilityResponse>(`/availability?${query.toString()}`))!;
}
