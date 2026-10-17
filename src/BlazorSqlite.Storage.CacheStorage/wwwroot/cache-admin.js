// Cache Storage as a bag of SQLite file images, without the engine.
//
// Our layout is page-sized entries under the `blazor-sqlite` cache. besql stored a whole file
// under `bit-Besql` at `/data/cache/{name}` - list/export/exists see those too so a migration
// can copy them out. Opening through the VFS imports a besql file losslessly into our layout.

import {
  BESQL_CACHE,
  CACHE_NAME,
  PAGE_SIZE,
  fileNameOf,
  importBesqlIfPresent,
  metaUrl,
  pageCount,
  pageUrl,
  pathOf,
} from './cache-vfs.js';

const RELATED_SUFFIXES = ['-journal', '-wal'];

export async function probe() {
  const environment = {
    hasCaches: String(typeof caches !== 'undefined'),
    secureContext: String(Boolean(globalThis.isSecureContext)),
    supportsJspi: String(typeof WebAssembly?.Suspending === 'function'),
  };

  if (typeof caches === 'undefined') {
    return unavailable('This browser does not expose the Cache Storage API.', environment);
  }

  try {
    await caches.open(CACHE_NAME);

    let quotaBytes = null;
    let usageBytes = null;
    if (typeof navigator.storage?.estimate === 'function') {
      const estimate = await navigator.storage.estimate();
      quotaBytes = estimate.quota ?? null;
      usageBytes = estimate.usage ?? null;
    }

    return { available: true, reason: null, quotaBytes, usageBytes, environment };
  } catch (error) {
    return unavailable(error?.message ?? String(error), environment);
  }
}

export async function exists(databaseName) {
  const name = requireName(databaseName);
  const path = pathOf(name);
  const cache = await caches.open(CACHE_NAME);
  if (await cache.match(metaUrl(path))) {
    return true;
  }

  return Boolean(await importBesqlIfPresent(name));
}

export async function list() {
  const names = new Set();
  const cache = await caches.open(CACHE_NAME);
  for (const request of await cache.keys()) {
    const url = new URL(request.url);
    const match = url.pathname.match(/\/blazor-sqlite\/db\/([^/]+)\/meta$/);
    if (!match) {
      continue;
    }

    const path = decodeURIComponent(match[1]);
    if (RELATED_SUFFIXES.some(suffix => path.endsWith(suffix))) {
      continue;
    }

    names.add(fileNameOf(path));
  }

  try {
    const besql = await caches.open(BESQL_CACHE);
    for (const request of await besql.keys()) {
      const url = new URL(request.url, 'https://placeholder.invalid');
      const match = url.pathname.match(/\/data\/cache\/(.+)$/);
      if (match) {
        names.add(decodeURIComponent(match[1]));
      }
    }
  } catch {
    // besql's cache is optional.
  }

  return [...names].sort();
}

export async function deleteDatabase(databaseName) {
  const name = requireName(databaseName);
  const cache = await caches.open(CACHE_NAME);

  for (const suffix of ['', ...RELATED_SUFFIXES]) {
    const path = pathOf(name + suffix);
    const meta = await cache.match(metaUrl(path));
    if (!meta) {
      continue;
    }

    const { size } = await meta.json();
    const deletions = [];
    for (let index = 0; index < pageCount(size ?? 0); index++) {
      deletions.push(cache.delete(pageUrl(path, index)));
    }

    await Promise.all(deletions);
    await cache.delete(metaUrl(path));
  }

  try {
    const besql = await caches.open(BESQL_CACHE);
    await besql.delete(`/data/cache/${name}`);
  } catch {
    // optional
  }
}

export async function exportDatabase(databaseName) {
  const name = requireName(databaseName);
  const path = pathOf(name);
  const cache = await caches.open(CACHE_NAME);
  const meta = await cache.match(metaUrl(path));
  if (meta) {
    const { size } = await meta.json();
    const image = new Uint8Array(size ?? 0);
    const reads = [];
    for (let offset = 0; offset < image.byteLength; offset += PAGE_SIZE) {
      reads.push(cache.match(pageUrl(path, offset / PAGE_SIZE)).then(async page => {
        if (!page) {
          return;
        }

        const bytes = new Uint8Array(await page.arrayBuffer());
        image.set(bytes.subarray(0, Math.min(PAGE_SIZE, image.byteLength - offset)), offset);
      }));
    }

    await Promise.all(reads);
    return image;
  }

  const besql = await importBesqlIfPresent(name);
  if (besql) {
    return besql;
  }

  throw new Error(`Cache Storage holds no database named '${name}'.`);
}

export async function importDatabase(databaseName, contents) {
  const name = requireName(databaseName);
  const path = pathOf(name);
  const bytes = contents instanceof Uint8Array ? contents : new Uint8Array(contents ?? []);
  const cache = await caches.open(CACHE_NAME);

  await deleteDatabase(name);

  const writes = [];
  for (let offset = 0; offset < bytes.byteLength; offset += PAGE_SIZE) {
    const page = new Uint8Array(PAGE_SIZE);
    page.set(bytes.subarray(offset, offset + PAGE_SIZE));
    writes.push(cache.put(pageUrl(path, offset / PAGE_SIZE), new Response(page, {
      headers: { 'content-type': 'application/octet-stream' },
    })));
  }

  await Promise.all(writes);

  await cache.put(metaUrl(path), new Response(JSON.stringify({ size: bytes.byteLength }), {
    headers: { 'content-type': 'application/json' },
  }));
}

function unavailable(reason, environment) {
  return { available: false, reason, quotaBytes: null, usageBytes: null, environment };
}

function requireName(databaseName) {
  if (typeof databaseName !== 'string' || databaseName.trim() === '') {
    throw new Error('A database name is required.');
  }

  return databaseName.trim();
}
