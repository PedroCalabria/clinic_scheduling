import { googleSignInUrl } from '@clinic/shared';
import { ROUTER_BASENAME } from './basePath';

/**
 * Where to send the browser to sign in with Google **from this app** (S0).
 *
 * The wrapper exists for a security reason rather than convenience (staff-google-guard,
 * design D1). The API decides which provisioning rule a Google sign-in gets from the return
 * path the flow STARTS with: a path under the staff base is the console, where an unknown
 * address is refused rather than quietly turned into a patient. A staff screen calling the
 * shared `googleSignInUrl` with a bare path would silently get the PORTAL rule back — the
 * exact bug this change closes, reintroduced by omission, and invisible until someone signs
 * in un-invited.
 *
 * So the prefix is derived from the same constant as the router basename and cannot be left
 * out. The regression is unrepresentable rather than caught by a test after the fact — the
 * same reasoning as `basePath.ts` deriving Vite's `base` and the basename from one segment.
 *
 * It lives beside `basePath.ts` rather than inside it because that file is imported by
 * `vite.config.ts`, which must not have to resolve the shared package at config-load time.
 *
 * @param path Where inside the console to land afterwards, as an app-relative path.
 */
export function staffGoogleSignInUrl(path = '/'): string {
  return googleSignInUrl(`${ROUTER_BASENAME}${path}`);
}
