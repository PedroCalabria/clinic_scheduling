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

async function readHostPort() {
  try {
    const env = await readFile(resolve(repoRoot, '.env'), 'utf8');
    const match = env.match(/^CADDY_HTTP_PORT\s*=\s*(\d+)/m);

    if (match) {
      return Number(match[1]);
    }
  } catch {
    // Falls through to the compose default.
  }

  return 8080;
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
