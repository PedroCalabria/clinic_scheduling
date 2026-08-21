#!/usr/bin/env node
/**
 * Fails when README.md points at something that does not exist.
 *
 * The README is the outward-facing surface (docs/00-context.md §8), and its most common way of
 * going wrong is not prose rot but a renamed file: a moved doc, a deleted image, a test project
 * that changed path. Nothing else in the pipeline notices, because a broken relative link is
 * perfectly valid Markdown.
 *
 * Deliberately narrow. It checks that every local path resolves, and nothing about the prose. The
 * commands and URLs the README quotes are covered elsewhere by construction: the compose-smoke
 * tier asserts the three URLs, and CI invokes both test projects by the same paths the README
 * prints. So the rule that keeps this cheap is a convention rather than a script — the README
 * quotes only paths and URLs that CI already exercises.
 *
 * External http(s) links are NOT fetched: a network call would make this gate flaky and slow, and
 * a CI failure caused by someone else's downtime teaches people to ignore CI.
 *
 * Usage: pnpm check:readme
 */

import { access, readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const readmePath = resolve(repoRoot, 'README.md');

/** Markdown links and images: `[text](target)` and `![alt](target)`. */
const LINK_PATTERN = /!?\[[^\]]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;

/**
 * Paths named in prose or code fences rather than as links — the ones a reader is told to type.
 * Listed explicitly because scanning every backticked span would flag command names and URLs.
 */
const REFERENCED_PATHS = [
  '.env.example',
  'global.json',
  'infra/docker-compose.yml',
  'apps/api/tests/Domain.UnitTests/Domain.UnitTests.csproj',
  'apps/api/tests/Api.IntegrationTests/Api.IntegrationTests.csproj',
];

function isExternal(target) {
  return /^(https?:|mailto:)/.test(target);
}

async function exists(relativePath) {
  try {
    await access(resolve(repoRoot, relativePath));
    return true;
  } catch {
    return false;
  }
}

const readme = await readFile(readmePath, 'utf8');
const failures = [];
const checked = new Set();

for (const match of readme.matchAll(LINK_PATTERN)) {
  const target = match[1];

  if (isExternal(target) || target.startsWith('#')) {
    continue;
  }

  // Strip any anchor: the file has to exist, the heading is not worth parsing for.
  const path = decodeURIComponent(target.split('#')[0]);

  if (path.length === 0 || checked.has(path)) {
    continue;
  }

  checked.add(path);

  if (!(await exists(path))) {
    failures.push(`link target does not exist: ${target}`);
  }
}

for (const path of REFERENCED_PATHS) {
  checked.add(path);

  if (!(await exists(path))) {
    failures.push(`path named in the README does not exist: ${path}`);
  }
}

if (failures.length > 0) {
  console.error('README link check failed:\n');

  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }

  console.error('\nUpdate README.md, or restore the file it points at.');
  process.exit(1);
}

console.log(`README link check passed: ${checked.size} local paths resolve.`);
