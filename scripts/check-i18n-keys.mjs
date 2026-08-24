#!/usr/bin/env node
/**
 * Fails when the two product languages do not carry an identical key set, or when a
 * component asks for a key that does not exist.
 *
 * This is the mechanism behind the i18n clause of the Definition of Done: "pt-BR and en
 * keys present for any new user-facing string" is a promise until something checks it.
 * Written in change 1, while there is one small resource pair to check, so it is trivially
 * verifiable now and simply keeps working as every later change adds strings.
 *
 * The usage scan was added by `clinic-catalog`. Parity alone cannot catch the other half of
 * the promise: a screen calling `t('catalog.titel')` renders the raw key to the user, and
 * both languages are equally, identically wrong. Checking statically is what lets "no
 * missing-key fallback is displayed" be asserted rather than eyeballed in a browser.
 *
 * Usage: pnpm check:i18n
 */

import { readFile, readdir } from 'node:fs/promises';
import { dirname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

/**
 * Directories holding one JSON file per language. Add to this list when a change
 * introduces app-local resources alongside the shared ones.
 */
const RESOURCE_DIRECTORIES = ['packages/shared/src/i18n'];

/** Every locale that must be present, and complete, in each resource directory. */
const REQUIRED_LOCALES = ['pt-BR', 'en'];

/** Collects leaf key paths, e.g. `common.systemStatus`. */
function collectKeys(value, prefix = '', keys = new Set()) {
  for (const [key, nested] of Object.entries(value)) {
    const path = prefix ? `${prefix}.${key}` : key;

    if (nested !== null && typeof nested === 'object' && !Array.isArray(nested)) {
      collectKeys(nested, path, keys);
    } else {
      keys.add(path);
    }
  }

  return keys;
}

function difference(left, right) {
  return [...left].filter((key) => !right.has(key)).sort();
}

const problems = [];

/** Key sets per resource directory, reused by the usage scan below. */
const allKeys = new Map();

for (const directory of RESOURCE_DIRECTORIES) {
  const absolute = join(repoRoot, directory);

  let present;
  try {
    present = (await readdir(absolute)).filter((name) => name.endsWith('.json'));
  } catch {
    problems.push(`${directory}: directory not found.`);
    continue;
  }

  const keysByLocale = new Map();

  for (const locale of REQUIRED_LOCALES) {
    const fileName = `${locale}.json`;

    if (!present.includes(fileName)) {
      problems.push(`${directory}: missing ${fileName}.`);
      continue;
    }

    const raw = await readFile(join(absolute, fileName), 'utf8');

    try {
      keysByLocale.set(locale, collectKeys(JSON.parse(raw)));
    } catch (error) {
      problems.push(`${directory}/${fileName}: invalid JSON — ${error.message}`);
    }
  }

  // Compare every locale against the first, so the report names the specific gap
  // rather than only saying the counts differ.
  const [reference, ...others] = REQUIRED_LOCALES;
  const referenceKeys = keysByLocale.get(reference);

  if (!referenceKeys) {
    continue;
  }

  for (const locale of others) {
    const localeKeys = keysByLocale.get(locale);

    if (!localeKeys) {
      continue;
    }

    for (const [missingIn, missing] of [
      [locale, difference(referenceKeys, localeKeys)],
      [reference, difference(localeKeys, referenceKeys)],
    ]) {
      if (missing.length > 0) {
        problems.push(
          `${directory}: ${missing.length} key(s) missing from ${missingIn}.json — ${missing.join(', ')}`,
        );
      }
    }
  }

  allKeys.set(directory, referenceKeys);

  if (problems.length === 0) {
    console.log(`${directory}: ${referenceKeys.size} keys, consistent across ${REQUIRED_LOCALES.join(' / ')}.`);
  }
}

// --- Usage scan: every literal key a component asks for must exist -------------------

/** Source trees whose `t('...')` calls are checked against the shared resources. */
const SOURCE_DIRECTORIES = ['apps/staff/src', 'apps/patient-portal/src', 'packages/shared/src'];

/**
 * Matches `t('some.key')` and `t("some.key")` with a literal key.
 *
 * Deliberately only literals. A computed key — `t(entry.labelKey)` in the app-shell, or
 * `t(`roles.${role}`)` — cannot be resolved without running the app, so it is skipped rather
 * than guessed at. Those are the minority, and the alternative is a scanner that reports
 * confident nonsense.
 */
const LITERAL_KEY = /\bt\(\s*(['"])([A-Za-z0-9_.]+)\1/g;

async function* sourceFiles(directory) {
  let entries;

  try {
    entries = await readdir(directory, { withFileTypes: true });
  } catch {
    return;
  }

  for (const entry of entries) {
    const path = join(directory, entry.name);

    if (entry.isDirectory()) {
      yield* sourceFiles(path);
    } else if (/\.tsx?$/.test(entry.name)) {
      yield path;
    }
  }
}

/**
 * The CLDR plural categories i18next appends to a key when `count` is passed.
 *
 * pt-BR and en both use only `one` and `other`, but the whole set is listed because the cost is
 * nothing and a third locale should not silently start failing this check.
 */
const PLURAL_SUFFIXES = ['zero', 'one', 'two', 'few', 'many', 'other'];

/**
 * Whether a key used in source resolves — directly, or as a pluralised family.
 *
 * Added by `booking-core`, whose search results say "3 times available" and must not say "1 times
 * available". i18next resolves `t('a.b', { count })` to `a.b_one` / `a.b_other`, so the literal key
 * legitimately does not exist. Without this, the check pushes every future change toward copy that
 * cannot pluralise — which is a worse outcome than the check was written to prevent.
 *
 * A family counts as present only if at least one suffixed form exists; the consistency check above
 * has already proved that whatever exists, exists in BOTH locales, so a half-translated plural is
 * still caught.
 */
function resolves(key, keys) {
  return keys.has(key) || PLURAL_SUFFIXES.some((suffix) => keys.has(`${key}_${suffix}`));
}

const referenceKeys = allKeys.get('packages/shared/src/i18n');

if (referenceKeys) {
  const missing = new Map();
  let scanned = 0;
  let checked = 0;

  for (const directory of SOURCE_DIRECTORIES) {
    for await (const file of sourceFiles(join(repoRoot, directory))) {
      const source = await readFile(file, 'utf8');
      scanned += 1;

      for (const match of source.matchAll(LITERAL_KEY)) {
        const key = match[2];

        // Namespaced calls and single-segment identifiers are not resource lookups.
        if (!key.includes('.')) {
          continue;
        }

        checked += 1;

        if (!resolves(key, referenceKeys)) {
          const where = file.slice(repoRoot.length + 1).split(sep).join('/');
          missing.set(key, where);
        }
      }
    }
  }

  if (missing.size > 0) {
    for (const [key, where] of missing) {
      problems.push(`${where}: t('${key}') has no translation in either language.`);
    }
  } else {
    console.log(`i18n usage: ${checked} literal key reference(s) across ${scanned} files all resolve.`);
  }
}

if (problems.length > 0) {
  console.error('i18n key check FAILED:');
  for (const problem of problems) {
    console.error(`  - ${problem}`);
  }
  process.exit(1);
}

console.log('i18n key check passed.');
