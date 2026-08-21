/**
 * The single source of truth for where this app is served (design D1).
 *
 * Vite's `base` and the router's `basename` are DERIVED from one segment so they cannot
 * drift apart. This app is the reason the contract matters: it is served under a path
 * prefix, so its emitted asset URLs must be absolute under `/staff/` (or they resolve
 * against the patient portal at the root and 404), and its router must strip `/staff`
 * before matching (or every route misses).
 *
 * Caddy's matching side of the same contract lives in infra/Caddyfile.
 */
const BASE_SEGMENT = '/staff';

/** Passed to `<BrowserRouter basename>` — no trailing slash. */
export const ROUTER_BASENAME = BASE_SEGMENT;

/** Passed to Vite's `base` — must end with a slash. */
export const VITE_BASE = `${BASE_SEGMENT}/`;
