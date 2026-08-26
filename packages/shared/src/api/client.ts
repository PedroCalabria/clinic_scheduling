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

/**
 * Turns a disabled account back on — the other half of {@link disableStaffAccount}.
 *
 * The account returns to the state it should hold, which the server derives: an unclaimed
 * professional invitation stays claimable rather than becoming active.
 *
 * A calendar authorization withdrawn when the account was disabled does **not** come back with
 * it; the professional reconnects on S2.
 */
export function enableStaffAccount(id: string): Promise<undefined> {
  return apiFetch<undefined>(`/staff-accounts/${id}/enable`, { method: 'POST' }) as Promise<undefined>;
}

/**
 * Which account holds an address, or `null` if none does.
 *
 * S11 lists staff only, so the account most likely to be blocking an invitation — a patient
 * provisioned by mistake on the portal — cannot be found by browsing. This resolves the exact
 * address the administrator has just typed, and nothing else.
 *
 * A 404 is a normal answer here ("nobody holds it") rather than a failure, so it comes back as
 * `null` instead of throwing.
 */
export async function findStaffAccountByEmail(email: string): Promise<StaffAccountResponse | null> {
  try {
    return (await apiFetch<StaffAccountResponse>(
      `/staff-accounts/by-email?email=${encodeURIComponent(email)}`,
    ))!;
  } catch (error) {
    if (error instanceof ApiRequestError && error.status === 404) {
      return null;
    }

    throw error;
  }
}

/**
 * Retires an account, releasing its address so it can be registered again.
 *
 * Distinct from {@link disableStaffAccount}, which ends access but keeps the address. This is
 * the deactivate half of the deactivate-and-invite-anew recovery: a role never changes, so an
 * account created with the wrong one is retired and the address invited afresh.
 */
export function deactivateStaffAccount(id: string): Promise<undefined> {
  return apiFetch<undefined>(`/staff-accounts/${id}/deactivate`, {
    method: 'POST',
  }) as Promise<undefined>;
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
  /** The stored name, or null while nobody has entered one. Null is ordinary, not an error. */
  fullName: string | null;
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
  fullName: string | null;
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

/**
 * Sets or clears how a professional is named to a person (S7).
 *
 * An empty name clears it and the derived label applies again, so removing a name is a safe act
 * rather than leaving a blank where a name should be. Setting one on a professional who has never
 * been configured **creates** their configuration record — it is a first save like any other.
 */
export function renameProfessional(userId: string, fullName: string): Promise<undefined> {
  return apiFetch<undefined>(`/config/professionals/${userId}/name`, {
    method: 'PUT',
    body: JSON.stringify({ fullName }),
  }) as Promise<undefined>;
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
  /**
   * What that room is called.
   *
   * **Show it on a staff surface, never on a patient one.** D7 says a patient is not told which
   * room, and that is a rule about what a screen renders — the wire has carried `resourceId` since
   * change 4. S4 and S5 are required to show a room; P2, P3, P4 and P6 must not.
   *
   * Like `resourceId`, this is the room that WOULD be used. The assigned one comes back on the
   * booking response.
   */
  resourceName: string;
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

// --- Booking (change 5a) ------------------------------------------------------------

/**
 * A professional a patient may choose on P2.
 *
 * `displayName` is the professional's stored name, entered on S7 — or, while nobody has entered
 * one, a label the server derives from the account address. `booking-desk` closed that seam (P-5)
 * and **no client changed**, which is exactly what naming the field for what it IS rather than for
 * where it came from was for.
 */
export interface BookableProfessional {
  professionalId: string;
  displayName: string;
}

export interface BookableAppointmentType {
  appointmentTypeId: string;
  name: string;
  specialtyId: string;
  /** Everyone qualified for this kind of visit. Never empty — the server filters. */
  professionals: BookableProfessional[];
}

export interface BookableSpecialty {
  specialtyId: string;
  name: string;
  /** Never empty: a specialty nobody can deliver anything in is not offered at all. */
  appointmentTypes: BookableAppointmentType[];
}

export interface BookingOptionsResponse {
  /**
   * The clinic's configured zone.
   *
   * Carried here as well as on the availability response so that a booking screen which does NOT
   * ask for availability — the confirmation step — still has something to render wall clock
   * against other than the browser's own zone. Reading the browser's zone is the exact bug storing
   * instants exists to avoid, and the last screen before the commit is the worst place to
   * reintroduce it.
   */
  timezone: string;
  specialties: BookableSpecialty[];
}

/**
 * What a patient may choose from: specialties, kinds of visit, and who offers them.
 *
 * One request rather than three, and already filtered to what is genuinely bookable — a
 * specialty nobody is qualified in would otherwise be a path that always ends in an empty
 * result. Unlike `/api/config/*`, this is readable by any authenticated caller: it is the
 * clinic's service catalogue, not patient data.
 */
export async function getBookingOptions(): Promise<BookingOptionsResponse> {
  return (await apiFetch<BookingOptionsResponse>('/booking/options'))!;
}

/** The appointment that now exists. Instants, like an availability slot. */
export interface AppointmentResponse {
  id: string;
  professionalId: string;
  appointmentTypeId: string;
  startsAt: string;
  endsAt: string;
  /** `Scheduled` for anything this change can create. */
  status: string;
  /** The zone to render the instants above in, so the browser never guesses. */
  timezone: string;
}

/**
 * Books a slot.
 *
 * **Carries no resource, on purpose.** The server assigns the room itself, so it is not a value a
 * client could get wrong or abuse — `resourceId` on an availability slot explains the answer and is
 * never authority. `startsAt` is the slot's UTC instant exactly as the availability response gave
 * it: sending wall clock is refused rather than coerced, because a coerced local time is an
 * appointment an hour out on a clock-change date.
 *
 * **It carries no patient either, and a patient must not add one.** Use
 * {@link bookAppointmentForPatient} from the staff console instead; a patient sending `patientId`
 * is refused with `auth.forbidden`, including when the value is their own.
 */
export async function bookAppointment(input: {
  appointmentTypeId: string;
  professionalId: string;
  startsAt: string;
}): Promise<AppointmentResponse> {
  return (await apiFetch<AppointmentResponse>('/appointments', {
    method: 'POST',
    body: JSON.stringify(input),
  }))!;
}

/** The appointment reception just made, including the room to send the patient to. */
export interface StaffAppointmentResponse extends AppointmentResponse {
  patientId: string;
  /** The room **assigned**, read back from the created appointment. */
  resourceId: string;
  resourceName: string;
}

/**
 * Books on a patient's behalf (S5) — the same endpoint, with the patient named explicitly.
 *
 * The identifier is **role-gated**: honoured for front desk and administrators, refused with
 * `auth.forbidden` from anyone else. Omitting it as staff is `validation.required`, and naming a
 * patient who does not exist is `patient.not_found`.
 *
 * Every booking rule still applies unchanged — the cutoff is about *changing* an appointment and
 * has never governed creating one, so a time too close to now is still refused with
 * `booking.lead_time_violation`. The patient's data-processing consent is still required, and it
 * is the **patient's** rather than the caller's.
 */
export async function bookAppointmentForPatient(input: {
  appointmentTypeId: string;
  professionalId: string;
  startsAt: string;
  patientId: string;
}): Promise<StaffAppointmentResponse> {
  return (await apiFetch<StaffAppointmentResponse>('/appointments', {
    method: 'POST',
    body: JSON.stringify(input),
  }))!;
}

/** One appointment as P5 lists it. */
export interface MyAppointment {
  id: string;
  professionalId: string;
  appointmentTypeId: string;
  startsAt: string;
  endsAt: string;
  /** Any of the five values; `booking-lifecycle` can produce three of them. */
  status: string;
  /**
   * Whether the caller may still reschedule or cancel it — **the server's decision, not inputs
   * to one**.
   *
   * The cancellation cutoff is deliberately not sent. A browser's clock is not the clinic's and
   * is user-settable, so a screen computing the rule locally could offer an action the server
   * will refuse — and the whole point of P5 showing the rule is that the rule shown is the rule
   * enforced. It folds two causes together (terminal, and inside the cutoff) because a screen
   * needs only to know the action is unavailable; what it *says* comes from `status` and
   * `startsAt`, which it already has.
   */
  canChange: boolean;
  /** Whether it has yet to finish. How the two lists are split. */
  isUpcoming: boolean;
}

/** P5's payload — the caller's own appointments, split by time rather than by status. */
export interface MyAppointmentsResponse {
  upcoming: MyAppointment[];
  past: MyAppointment[];
  /** The zone to render every instant above in, so the browser never guesses. */
  timezone: string;
}

/**
 * The caller's own appointments, upcoming and past.
 *
 * Terminal appointments are present rather than filtered out, annotated where they fall in time:
 * "what happened to my 3pm?" is the question a patient asks, and a cancelled appointment belongs
 * where they would look for it.
 */
export async function listMyAppointments(): Promise<MyAppointmentsResponse> {
  return (await apiFetch<MyAppointmentsResponse>('/appointments'))!;
}

/**
 * Cancels the caller's own appointment, freeing the time it held.
 *
 * Refused with `booking.cutoff_passed` inside the cancellation cutoff, and with
 * `booking.appointment_not_changeable` if it is already cancelled or rescheduled — the two-tab
 * case. An appointment belonging to somebody else and an id that never existed both answer
 * `auth.ownership_denied`, so this cannot be used to discover which appointments are real.
 *
 * **Requires no active consent**, unlike {@link rescheduleAppointment}: refusing to let somebody
 * leave because they withdrew consent to data processing would trap them as a consequence of
 * exercising a right.
 */
export async function cancelAppointment(id: string): Promise<AppointmentResponse> {
  return (await apiFetch<AppointmentResponse>(`/appointments/${id}/cancel`, {
    method: 'POST',
  }))!;
}

/**
 * Moves the caller's own appointment to a new time.
 *
 * **Carries an instant and nothing else.** No professional, no appointment type, no room — a
 * reschedule keeps the first two by definition and the server assigns the third, so "a reschedule
 * cannot change the professional" is structural rather than validated. Moving to a different
 * professional is a cancellation followed by a new booking, through the two calls above.
 *
 * Returns the **new** appointment; the original is now `Rescheduled` and keeps its own time, so
 * the history stays readable. Refused for every reason a booking of the same instant would be
 * refused, plus the cutoff and the already-terminal cases.
 */
export async function rescheduleAppointment(
  id: string,
  input: { startsAt: string },
): Promise<AppointmentResponse> {
  return (await apiFetch<AppointmentResponse>(`/appointments/${id}/reschedule`, {
    method: 'POST',
    body: JSON.stringify(input),
  }))!;
}

/**
 * Grants a consent again, at the version currently in force.
 *
 * The counterpart to {@link revokeConsent}, added because booking now requires an active
 * data-processing consent: without this, withdrawing one on P7 left a patient unable to book and
 * with no way back. A new record rather than un-revoking the old one, so "consented, withdrew,
 * consented again" stays three facts.
 */
export async function grantConsent(type: string): Promise<ConsentResponse> {
  return (await apiFetch<ConsentResponse>(`/patients/me/consents/${type}/grant`, {
    method: 'POST',
  }))!;
}

// --- The staff console (change 5c) --------------------------------------------------

/** One appointment as S1 and S4 read it. */
export interface ScheduledAppointment {
  id: string;
  professionalId: string;
  professionalName: string;
  patientId: string;
  /** Personal data. Reading this screen wrote an `AccessLog` row; treat it accordingly. */
  patientName: string;
  appointmentTypeId: string;
  appointmentTypeName: string;
  resourceId: string;
  /** The room, shown on staff surfaces (D7 is patient-facing only). */
  resourceName: string;
  startsAt: string;
  endsAt: string;
  status: string;
  /** `SelfService` or `FrontDesk` — how the appointment came to exist. */
  source: string;
  /**
   * Whether **the patient** may still change it — not whether the reader may.
   *
   * The server's decision, for the reason `canChange` on P5 is: a browser's clock is not the
   * clinic's. Named for the patient because S4's whole point is the sentence "the patient can no
   * longer change this, and you can" — reception's own actions are never gated by it.
   */
  patientCanChange: boolean;
}

/** A period a professional has declared themselves unavailable for. */
export interface ScheduledBlock {
  id: string;
  professionalId: string;
  professionalName: string;
  startsAt: string;
  endsAt: string;
}

export interface ScheduleDayResponse {
  date: string;
  /** The zone to render every instant above in, so the browser never guesses. */
  timezone: string;
  appointments: ScheduledAppointment[];
  blocks: ScheduledBlock[];
}

/**
 * A clinic day — a professional's own (S1), or reception's across professionals (S4).
 *
 * `professionalId` narrows the day for reception and is **disregarded** for a professional, whose
 * scope is always their own. That is structural rather than a refusal, so a professional cannot use
 * it to learn whether another professional exists.
 *
 * Terminal appointments are absent: a cancelled appointment is not part of the day being run.
 */
export async function getScheduleDay(input: {
  date: string;
  professionalId?: string;
}): Promise<ScheduleDayResponse> {
  const query = new URLSearchParams({ date: input.date });

  if (input.professionalId) {
    query.set('professionalId', input.professionalId);
  }

  return (await apiFetch<ScheduleDayResponse>(`/schedule?${query.toString()}`))!;
}

/** A patient reception has resolved for a booking. */
export interface ResolvedPatient {
  patientId: string;
  fullName: string;
  contactEmail: string;
  /**
   * Whether they currently hold an active data-processing consent at the version in force.
   *
   * Surfaced so a receptionist learns it **before** taking a walk-in's time rather than as an
   * `auth.consent_required` refusal after choosing a slot. The gate itself is not relaxed for
   * staff, and must not be: the patient grants consent in the portal.
   */
  hasDataProcessingConsent: boolean;
}

/**
 * Finds a patient by their **exact** contact email (S5).
 *
 * Exact, and deliberately not a search: a name or prefix search over patients is an enumeration
 * surface, and every result would have to be recorded, which would bury the entries that matter.
 * Half an address is `validation.invalid_format`; a whole one belonging to nobody is
 * `patient.not_found`. A successful lookup writes one `AccessLog` row.
 */
export async function resolvePatientByEmail(email: string): Promise<ResolvedPatient> {
  return (await apiFetch<ResolvedPatient>(
    `/patients/by-email?email=${encodeURIComponent(email)}`,
  ))!;
}

// --- Calendar connection (change 6a) ------------------------------------------------

/**
 * What S2 knows about a professional's calendar authorization.
 *
 * `stateObservedAtUtc` travels beside `status` on purpose, and the screen must render both.
 * Nothing calls Google on a schedule in 6a, so the status is the result of the last look
 * rather than current truth — showing it alone would state a fact more confidently than the
 * server can support.
 *
 * There is no field for the credential, sealed or otherwise, and there never should be.
 */
export interface CalendarConnectionResponse {
  /** True only when the connection could actually be used. */
  connected: boolean;
  /** `Connected` · `Revoked` · `Disconnected` · `NotConnected`. */
  status: string;
  provider: string | null;
  targetCalendarId: string | null;
  connectedAtUtc: string | null;
  /** When `status` was last observed to be what it says. */
  stateObservedAtUtc: string | null;
  /** The calendar consent in force, so the professional can see what they agreed to. */
  consentVersion: string | null;
  consentGrantedAtUtc: string | null;
}

export interface CalendarDisconnectResponse {
  connection: CalendarConnectionResponse;
  /**
   * Whether the grant is confirmed gone from Google's side.
   *
   * `false` is not a failure — the local withdrawal happened either way — but it is not success
   * either, and the screen says so rather than reporting an unqualified success it cannot vouch
   * for.
   */
  revokedAtProvider: boolean;
}

export async function getCalendarConnection(): Promise<CalendarConnectionResponse> {
  return (await apiFetch<CalendarConnectionResponse>('/calendar/connection'))!;
}

/**
 * Asks Google whether the stored grant still stands, reading no calendar content.
 *
 * Explicit rather than automatic: probing on every page load would tie this screen to Google's
 * availability, and probing on a hidden throttle would decide for the professional how stale is
 * acceptable. The button plus "last checked" is the honest version.
 */
export async function checkCalendarConnection(): Promise<CalendarConnectionResponse> {
  return (await apiFetch<CalendarConnectionResponse>('/calendar/connection/check', {
    method: 'POST',
  }))!;
}

export async function disconnectCalendar(): Promise<CalendarDisconnectResponse> {
  return (await apiFetch<CalendarDisconnectResponse>('/calendar/connection/disconnect', {
    method: 'POST',
  }))!;
}

/**
 * Where the browser goes to start the authorization.
 *
 * A plain URL rather than a fetch, because connecting is a **top-level navigation** to Google
 * and back — the state cookie has to be set on a real request the browser then follows. Calling
 * this with `fetch` would follow the redirect to Google's consent screen inside XHR and fail on
 * CORS, which is a confusing way to learn the flow is not an API call.
 */
export function calendarConnectUrl(returnTo: string): string {
  return `/api/calendar/connect?returnTo=${encodeURIComponent(returnTo)}`;
}
