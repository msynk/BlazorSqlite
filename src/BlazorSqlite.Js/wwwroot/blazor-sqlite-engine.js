// Chooses a SQLite build, loads it, and lets the selected storage provider register its VFS.
//
// Three builds are vendored and exactly one is fetched per session. The choice is not a preference
// setting: a VFS that suspends to read storage (IndexedDB, Cache Storage) cannot run on the
// synchronous build at all, and the two builds that can host it differ by more than a factor of two
// in download size, so the cheapest capable build wins.

import { raiseFunctionTableLimit } from './blazor-sqlite-wasm-table.js';
import * as SQLite from './engine/sqlite-api.js';

/** Which engine build a VFS needs. Mirrors BlazorSqliteEngineBuild on the .NET side. */
export const EngineBuild = Object.freeze({
  synchronous: 'synchronous',
  asyncCapable: 'asyncCapable',
});

const BUILDS = Object.freeze({
  synchronous: { id: 'synchronous', module: './engine/wa-sqlite.mjs', wasm: './engine/wa-sqlite.wasm' },
  jspi: { id: 'jspi', module: './engine/wa-sqlite-jspi.mjs', wasm: './engine/wa-sqlite-jspi.wasm' },
  asyncify: { id: 'asyncify', module: './engine/wa-sqlite-async.mjs', wasm: './engine/wa-sqlite-async.wasm' },
});

/**
 * Whether the browser has JavaScript Promise Integration, which lets the engine suspend natively
 * instead of through Asyncify's instrumented control flow.
 *
 * Unflagged in Chrome 137+, flagged in Firefox, and absent in Safari as of mid-2026, which is why
 * Asyncify is still vendored: it is the only route to an asynchronous VFS on Safari.
 */
export function supportsJspi() {
  return typeof WebAssembly?.Suspending === 'function';
}

/**
 * Picks the smallest build that can host the given VFS requirement.
 *
 * @param {string} requiredBuild a value of {@link EngineBuild}
 * @returns {{id: string, module: string}}
 */
export function selectBuild(requiredBuild) {
  switch (requiredBuild) {
    case EngineBuild.synchronous:
      return BUILDS.synchronous;

    case EngineBuild.asyncCapable:
      return supportsJspi() ? BUILDS.jspi : BUILDS.asyncify;

    default:
      throw new Error(`Unknown engine build requirement '${requiredBuild}'.`);
  }
}

/**
 * Loads the engine and registers the storage provider's VFS against it.
 *
 * @param {object} options
 * @param {string} options.requiredBuild a value of {@link EngineBuild}
 * @param {string} options.databaseName the database the VFS will be asked to open
 * @param {{moduleUrl: string, registerExport: string}} [options.vfs]
 *   The provider's VFS module. Omitted for a provider that has none, which leaves the engine on its
 *   built-in memory VFS.
 * @returns {Promise<{sqlite3: object, module: object, build: string, vfsName: string|null, onOpenSql: string[]}>}
 */
export async function loadEngine({ requiredBuild, databaseName, vfs }) {
  if (!databaseName) {
    throw new Error('A database name is required.');
  }

  const build = selectBuild(requiredBuild);

  if (vfs && build.id === BUILDS.synchronous.id && requiredBuild !== EngineBuild.synchronous) {
    // Unreachable via selectBuild, but this is the mismatch that produces
    // "Synchronous WebAssembly cannot call async function" deep inside a query rather than here.
    throw new Error('An asynchronous VFS cannot run on the synchronous engine build.');
  }

  const factory = (await import(build.module)).default;
  const wasmBinary = raiseFunctionTableLimit(
    new Uint8Array(await (await fetch(new URL(build.wasm, import.meta.url))).arrayBuffer()));
  const module = await factory({ wasmBinary });
  const sqlite3 = SQLite.Factory(module);

  let vfsName = null;
  let onOpenSql = [];

  if (vfs) {
    const vfsModule = await import(vfs.moduleUrl);
    const register = vfsModule[vfs.registerExport ?? 'register'];

    if (typeof register !== 'function') {
      throw new Error(
        `'${vfs.moduleUrl}' does not export a function named '${vfs.registerExport ?? 'register'}'.`);
    }

    // The provider both creates and registers: VFS construction is asynchronous and
    // implementation-specific (handle pools, IndexedDB connections), so the engine host cannot do it.
    const registered = await register({ module, sqlite3, databaseName });
    vfsName = registered?.vfsName ?? null;
    onOpenSql = Array.isArray(registered?.onOpenSql) ? registered.onOpenSql : [];

    if (!vfsName) {
      throw new Error(
        `'${vfs.moduleUrl}' registered a VFS without reporting its name, so the connection cannot `
        + 'ask for it.');
    }
  }

  return { sqlite3, module, build: build.id, vfsName, onOpenSql };
}
