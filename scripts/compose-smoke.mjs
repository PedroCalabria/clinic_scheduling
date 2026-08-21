#!/usr/bin/env node
/**
 * Compose-smoke tier (design D6).
 *
 * WHY THIS EXISTS: the integration tests drive the API in-process through
 * WebApplicationFactory, which bypasses Caddy entirely — no proxy, no static files, no
 * base paths. A fully green integration suite is therefore compatible with a Caddyfile
 * that 404s every deep link. Since serving two SPA builds from one origin is the single
 * riskiest thing in this change, it gets a tier that exercises the real stack.
 *
 * These assertions map one-to-one onto the scenarios in the platform-health spec.
 *
 * Implemented with Node's fetch rather than shell curl so the same script runs unchanged
 * on a Windows dev machine and on the Linux CI runner.
 *
 * Usage:
 *   pnpm smoke              # brings the stack up, asserts, tears it down
 *   pnpm smoke --no-manage  # asserts against a stack that is already running
 */

import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const manageStack = !process.argv.includes('--no-manage');

const COMPOSE_ARGS = ['compose', '--env-file', '.env', '-f', 'infra/docker-compose.yml'];
const READY_TIMEOUT_MS = 240_000;
const POLL_INTERVAL_MS = 3_000;

function run(command, args, { capture = false } = {}) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(command, args, {
      cwd: repoRoot,
      stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit',
      shell: false,
    });

    let stdout = '';
    let stderr = '';

    if (capture) {
      child.stdout.on('data', (chunk) => (stdout += chunk));
      child.stderr.on('data', (chunk) => (stderr += chunk));
    }

    child.on('error', reject);
    child.on('close', (code) =>
      code === 0
        ? resolvePromise({ stdout, stderr })
        : reject(new Error(`${command} ${args.join(' ')} exited with ${code}\n${stderr}`)),
    );
  });
}

/** Reads a variable out of .env, which is the same file Compose interpolates from. */
async function readEnvValue(name, fallback) {
  try {
    const env = await readFile(resolve(repoRoot, '.env'), 'utf8');
    // Spaces and tabs only around the `=`, never `\s`: `\s` matches a newline, so an
    // empty value (`NAME=`) would quietly capture the NEXT line and report it as this
    // variable's value.
    const match = env.match(new RegExp(`^${name}[ \\t]*=[ \\t]*(.*)$`, 'm'));

    if (match) {
      const value = match[1].trim();

      if (value.length > 0) {
        return value;
      }
    }
  } catch {
    // Falls through to the caller's default.
  }

  return fallback;
}

async function readHostPort() {
  return Number(await readEnvValue('CADDY_HTTP_PORT', 8080));
}

/**
 * Waits on Compose healthchecks rather than a fixed sleep: the stack is ready when
 * Compose says db and api are `healthy`, which is the same signal `depends_on` uses.
 */
async function waitForStack() {
  const deadline = Date.now() + READY_TIMEOUT_MS;

  while (Date.now() < deadline) {
    const { stdout } = await run('docker', [...COMPOSE_ARGS, 'ps', '--format', 'json'], {
      capture: true,
    });

    const services = stdout
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean)
      .flatMap((line) => {
        try {
          const parsed = JSON.parse(line);
          return Array.isArray(parsed) ? parsed : [parsed];
        } catch {
          return [];
        }
      });

    const byName = new Map(services.map((service) => [service.Service, service]));
    const db = byName.get('db');
    const api = byName.get('api');
    const caddy = byName.get('caddy');

    const dbReady = db?.Health === 'healthy';
    const apiReady = api?.Health === 'healthy';
    const caddyReady = caddy?.State === 'running';

    if (dbReady && apiReady && caddyReady) {
      return byName;
    }

    for (const [name, service] of byName) {
      if (service.State === 'exited') {
        throw new Error(`Service ${name} exited before becoming ready.`);
      }
    }

    process.stdout.write(
      `  waiting — db=${db?.Health ?? 'n/a'} api=${api?.Health ?? 'n/a'} caddy=${caddy?.State ?? 'n/a'}\n`,
    );

    await new Promise((r) => setTimeout(r, POLL_INTERVAL_MS));
  }

  throw new Error(`Stack did not become healthy within ${READY_TIMEOUT_MS / 1000}s.`);
}

// --- assertions ----------------------------------------------------------------------

const results = [];

async function check(name, fn) {
  try {
    await fn();
    results.push({ name, ok: true });
    console.log(`  PASS  ${name}`);
  } catch (error) {
    results.push({ name, ok: false, error });
    console.log(`  FAIL  ${name}\n        ${error.message}`);
  }
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

/** Reads the `x-clinic-app` marker so we can tell WHICH build was served. */
function servedApp(html) {
  return html.match(/<meta\s+name="x-clinic-app"\s+content="([^"]+)"/)?.[1];
}

function firstScriptSrc(html) {
  return html.match(/<script[^>]+src="([^"]+)"/)?.[1];
}

function firstStylesheetHref(html) {
  return html.match(/<link[^>]+rel="stylesheet"[^>]+href="([^"]+)"/)?.[1];
}

/**
 * Picks one cookie out of a response.
 *
 * Cookies are handled by hand here because Node's fetch has no cookie jar. That is a
 * feature for this tier: it makes the Set-Cookie attributes something the test can assert
 * on rather than something a client library quietly applies.
 */
function readSetCookie(response, name) {
  const headers = response.headers.getSetCookie?.() ?? [];

  return headers.find((cookie) => cookie.startsWith(`${name}=`));
}

function cookieValue(setCookie) {
  return setCookie.slice(setCookie.indexOf('=') + 1).split(';')[0];
}

async function main() {
  const port = await readHostPort();
  const base = `http://localhost:${port}`;

  if (manageStack) {
    console.log('Building and starting the stack…');
    await run('docker', [...COMPOSE_ARGS, 'up', '-d', '--build']);
  }

  console.log('Waiting for Compose healthchecks…');
  const services = await waitForStack();

  console.log(`\nAsserting against ${base}\n`);

  // --- the two surfaces are served at their own base paths -------------------------
  await check('GET / serves the patient-portal build', async () => {
    const response = await fetch(`${base}/`);
    assert(response.status === 200, `expected 200, got ${response.status}`);

    const app = servedApp(await response.text());
    assert(app === 'patient-portal', `expected patient-portal, got ${app}`);
  });

  await check('GET /staff/ serves the staff build', async () => {
    const response = await fetch(`${base}/staff/`);
    assert(response.status === 200, `expected 200, got ${response.status}`);

    const app = servedApp(await response.text());
    assert(app === 'staff', `expected staff, got ${app}`);
  });

  await check('GET /staff redirects to /staff/ and lands on the staff app', async () => {
    // Redirects are FOLLOWED here on purpose. With `redirect: 'manual'` the fetch spec
    // returns an opaque-redirect response — status 0, no headers — so asserting on the
    // status or Location would fail no matter what the server does.
    const response = await fetch(`${base}/staff`);

    assert(response.status === 200, `expected 200 after redirect, got ${response.status}`);
    assert(response.redirected, 'expected /staff to redirect rather than serve directly');
    assert(
      new URL(response.url).pathname === '/staff/',
      `expected to land on /staff/, got ${new URL(response.url).pathname}`,
    );

    const app = servedApp(await response.text());
    assert(app === 'staff', `expected staff, got ${app}`);
  });

  // --- assets resolve under each prefix --------------------------------------------
  await check("portal asset resolves at the root prefix", async () => {
    const html = await (await fetch(`${base}/`)).text();
    const src = firstScriptSrc(html);

    assert(src, 'no <script src> found in the portal index.html');
    assert(!src.startsWith('/staff/'), `portal asset must not live under /staff/: ${src}`);

    const asset = await fetch(`${base}${src}`);
    assert(asset.status === 200, `expected 200 for ${src}, got ${asset.status}`);
  });

  await check('staff asset resolves under /staff/', async () => {
    const html = await (await fetch(`${base}/staff/`)).text();
    const src = firstScriptSrc(html);

    assert(src, 'no <script src> found in the staff index.html');
    // The base/basename contract: the staff build MUST emit absolute /staff/ asset URLs,
    // or they resolve against the portal at the root and 404 (design D1).
    assert(src.startsWith('/staff/'), `staff asset must live under /staff/, got ${src}`);

    const asset = await fetch(`${base}${src}`);
    assert(asset.status === 200, `expected 200 for ${src}, got ${asset.status}`);
  });

  // --- deep-link reload: the assertion the integration tier cannot make -------------
  await check('deep link /staff/anything serves the STAFF index.html', async () => {
    const response = await fetch(`${base}/staff/anything`);
    assert(response.status === 200, `expected 200, got ${response.status}`);

    const app = servedApp(await response.text());
    // If the per-prefix try_files were global, this would be 'patient-portal': the wrong
    // app would boot under the wrong basename. That is the regression this guards.
    assert(app === 'staff', `expected staff, got ${app}`);
  });

  await check('deep link /anything serves the PORTAL index.html', async () => {
    const response = await fetch(`${base}/anything`);
    assert(response.status === 200, `expected 200, got ${response.status}`);

    const app = servedApp(await response.text());
    assert(app === 'patient-portal', `expected patient-portal, got ${app}`);
  });

  // --- the API through the proxy ----------------------------------------------------
  await check('GET /api/health reports healthy through Caddy', async () => {
    const response = await fetch(`${base}/api/health`);
    assert(response.status === 200, `expected 200, got ${response.status}`);

    const body = await response.json();
    assert(body.status === 'Healthy', `expected Healthy, got ${body.status}`);
    assert(
      body.checks?.database === 'Healthy',
      `expected database Healthy, got ${body.checks?.database}`,
    );
  });

  await check('/api/health echoes a correlation id', async () => {
    const response = await fetch(`${base}/api/health`, {
      headers: { 'X-Correlation-ID': 'smoke-test-correlation-id' },
    });

    assert(
      response.headers.get('x-correlation-id') === 'smoke-test-correlation-id',
      `expected the inbound correlation id back, got ${response.headers.get('x-correlation-id')}`,
    );
  });

  // --- the design system survives the production build (design A12) ----------------
  await check('shared UI primitive classes survive the built CSS', async () => {
    // The failure this guards is specific and invisible in development: Tailwind emits only
    // the classes it finds in scanned SOURCE, and the primitives live in packages/shared,
    // outside each app's own tree. Without the @source directive the components render
    // unstyled in a production build while looking perfect in dev.
    for (const [surface, path] of [
      ['portal', '/'],
      ['staff', '/staff/'],
    ]) {
      const html = await (await fetch(`${base}${path}`)).text();
      const href = firstStylesheetHref(html);

      assert(href, `no stylesheet <link> found in the ${surface} index.html`);

      const css = await (await fetch(`${base}${href}`)).text();

      // bg-primary is used ONLY by the shared Button, so its presence proves the shared
      // package was scanned rather than merely bundled.
      assert(css.includes('bg-primary'), `${surface} CSS is missing the shared Button's classes`);
      assert(css.includes('--color-primary'), `${surface} CSS is missing the design tokens`);
    }
  });

  // --- identity, through the proxy (change 2) ---------------------------------------
  await check('an unauthenticated protected endpoint answers 401 with the catalogue code', async () => {
    const response = await fetch(`${base}/api/auth/session`);

    assert(response.status === 401, `expected 401, got ${response.status}`);

    const body = await response.json();
    assert(
      body.code === 'auth.session_expired',
      `expected auth.session_expired, got ${body.code}`,
    );
  });

  await check('an internal account signs in through Caddy and the cookie survives the proxy', async () => {
    const email = await readEnvValue('Auth__BootstrapAdministrator__Email', null);
    const password = await readEnvValue('Auth__BootstrapAdministrator__Password', null);

    assert(email && password, 'no bootstrap administrator configured in .env');

    // The CSRF token has to be obtained the way a browser would: from a safe request.
    const primed = await fetch(`${base}/api/health`);
    const csrfCookie = readSetCookie(primed, 'clinic.csrf');

    assert(csrfCookie, 'the API did not issue a CSRF cookie on a safe request');

    const csrf = cookieValue(csrfCookie);

    const signIn = await fetch(`${base}/api/auth/sign-in`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-Token': csrf,
        Cookie: `clinic.csrf=${csrf}`,
      },
      body: JSON.stringify({ email, password }),
    });

    assert(signIn.status === 200, `expected 200, got ${signIn.status}`);

    const sessionCookie = readSetCookie(signIn, 'clinic.session');
    assert(sessionCookie, 'no session cookie was set');

    // The flags are asserted here rather than in the integration tier because this is the
    // response a real browser receives, through the real proxy.
    const lower = sessionCookie.toLowerCase();
    assert(lower.includes('httponly'), `session cookie must be HttpOnly: ${sessionCookie}`);
    assert(lower.includes('secure'), `session cookie must be Secure: ${sessionCookie}`);
    assert(lower.includes('samesite=lax'), `session cookie must be SameSite=Lax: ${sessionCookie}`);

    const session = cookieValue(sessionCookie);

    // And it authenticates the next request, having crossed Caddy in both directions.
    const whoami = await fetch(`${base}/api/auth/session`, {
      headers: { Cookie: `clinic.session=${session}` },
    });

    assert(whoami.status === 200, `expected 200 for an authenticated request, got ${whoami.status}`);

    const body = await whoami.json();
    assert(body.role === 'Administrator', `expected Administrator, got ${body.role}`);
    assert(
      body.mustChangePassword === true,
      'the bootstrapped administrator must be required to replace its password',
    );

    // The forced change is enforced in the pipeline, so anything else is refused until the
    // credential is replaced (design A6).
    const held = await fetch(`${base}/api/staff-accounts`, {
      headers: { Cookie: `clinic.session=${session}` },
    });

    assert(held.status === 403, `expected 403 while the bootstrap password stands, got ${held.status}`);

    const heldBody = await held.json();
    assert(
      heldBody.code === 'auth.password_change_required',
      `expected auth.password_change_required, got ${heldBody.code}`,
    );
  });

  await check('a state-changing request without the CSRF header is refused', async () => {
    const response = await fetch(`${base}/api/auth/sign-in`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: 'nobody@clinic.test', password: 'whatever' }),
    });

    assert(response.status === 403, `expected 403, got ${response.status}`);

    const body = await response.json();
    assert(body.code === 'auth.forbidden', `expected auth.forbidden, got ${body.code}`);
  });

  await check('the Google sign-in entry point reports its configuration state', async () => {
    // No Google client is configured in .env.example, so this must degrade rather than
    // break: a redirect carrying the code, which the frontend translates (design A14).
    const response = await fetch(`${base}/api/auth/google/start`, { redirect: 'manual' });

    assert(
      response.status === 302 || response.status === 0,
      `expected a redirect, got ${response.status}`,
    );

    const location = response.headers.get('location');

    if (location) {
      const configured = await readEnvValue('Auth__Google__ClientId', null);

      if (configured) {
        assert(
          location.includes('scope=openid') && !location.includes('calendar'),
          `expected identity scopes only, got ${location}`,
        );
      } else {
        assert(
          location.includes('authError=auth.google_unavailable'),
          `expected auth.google_unavailable, got ${location}`,
        );
      }
    }
  });

  // --- only Caddy is public (Decision U) -------------------------------------------
  await check('api and db publish no host ports', async () => {
    for (const name of ['api', 'db']) {
      const publishers = services.get(name)?.Publishers ?? [];
      const published = publishers.filter((p) => p.PublishedPort > 0);

      assert(
        published.length === 0,
        `${name} must not publish a host port, found ${JSON.stringify(published)}`,
      );
    }
  });

  const failed = results.filter((result) => !result.ok);

  console.log(`\n${results.length - failed.length}/${results.length} checks passed.`);

  if (failed.length > 0) {
    throw new Error(`${failed.length} smoke check(s) failed.`);
  }
}

let exitCode = 0;

try {
  await main();
  console.log('\nCompose smoke: PASS');
} catch (error) {
  console.error(`\nCompose smoke: FAIL — ${error.message}`);
  exitCode = 1;

  if (manageStack) {
    console.error('\n--- api logs (tail) ---');
    await run('docker', [...COMPOSE_ARGS, 'logs', '--tail', '60', 'api']).catch(() => {});
    console.error('\n--- caddy logs (tail) ---');
    await run('docker', [...COMPOSE_ARGS, 'logs', '--tail', '40', 'caddy']).catch(() => {});
  }
} finally {
  if (manageStack) {
    console.log('\nTearing down…');
    await run('docker', [...COMPOSE_ARGS, 'down', '-v']).catch(() => {});
  }
}

process.exit(exitCode);
