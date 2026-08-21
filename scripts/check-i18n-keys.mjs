#!/usr/bin/env node
/**
 * Fails when the two product languages do not carry an identical key set.
 *
 * This is the mechanism behind the i18n clause of the Definition of Done: "pt-BR and en
 * keys present for any new user-facing string" is a promise until something checks it.
 * Written in change 1, while there is one small resource pair to check, so it is trivially
 * verifiable now and simply keeps working as every later change adds strings.
 *
 * Usage: pnpm check:i18n
 */

import { readFile, readdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
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

  if (problems.length === 0) {
    console.log(`${directory}: ${referenceKeys.size} keys, consistent across ${REQUIRED_LOCALES.join(' / ')}.`);
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
