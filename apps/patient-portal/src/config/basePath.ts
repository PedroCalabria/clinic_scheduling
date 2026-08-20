/**
 * The single source of truth for where this app is served (design D1).
 *
 * Vite's `base` and the router's `basename` are DERIVED from one segment so they cannot
 * drift apart. When they disagree you get an app that mounts but cannot navigate, or one
 * that navigates but cannot load its assets — the classic failure of serving two SPAs
 * from one origin, and the reason this app and `apps/staff` each own this file.
 *
 * The patient portal is served at the root, so the segment is empty.
 * Its sibling `apps/staff` uses '/staff'.
 */
const BASE_SEGMENT = '';

/** Passed to `<BrowserRouter basename>`. */
export const ROUTER_BASENAME = BASE_SEGMENT || '/';

/** Passed to Vite's `base` — must end with a slash. */
export const VITE_BASE = `${BASE_SEGMENT}/`;
