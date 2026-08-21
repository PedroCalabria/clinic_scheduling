/**
 * The API client both frontends share.
 *
 * Requests use a RELATIVE path (design D3): both SPAs are served from the same origin as
 * the API (Caddy routes `/api/*` to it), so there is no API base URL to configure, no
 * CORS in any environment, and no build-time environment coupling. In dev, each Vite dev
 * server proxies `/api` to the API so the identical code path works without Caddy.
 */

const API_PREFIX = '/api';

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

/**
 * Issues a request and decodes the JSON body.
 *
 * @param acceptStatuses status codes whose body is meaningful data rather than an error.
 *   Defaults to 2xx only.
 */
async function apiFetch<T>(
  path: string,
  { acceptStatuses, ...init }: RequestInit & { acceptStatuses?: readonly number[] } = {},
): Promise<T> {
  const response = await fetch(`${API_PREFIX}${path}`, {
    headers: { Accept: 'application/json', ...init.headers },
    ...init,
  });

  const accepted = response.ok || (acceptStatuses?.includes(response.status) ?? false);

  if (!accepted) {
    throw new ApiRequestError(response.status, await readError(response));
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
export function getHealth(): Promise<HealthResponse> {
  return apiFetch<HealthResponse>('/health', { acceptStatuses: [503] });
}
