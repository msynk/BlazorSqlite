// Helpers for driving the library from test code.
//
// Tests write plain JavaScript values and read plain results. Batches go through the binary wire
// path - the one the .NET transport uses - encoded and decoded with the library's own codec, so
// every test here exercises the real format rather than a parallel one. `interop.spec.js` covers the
// tagged JSON path separately.
//
// Values that JSON cannot carry are tagged again on the way out to Playwright, so that "came back as a
// 64-bit integer" and "came back as a blob" stay observable instead of being silently coerced.

import { test } from '@playwright/test';

/**
 * Loads the host page and opens a database.
 *
 * @returns {Promise<{version: string, build: string}>} what the worker reports about itself
 */
export async function openHost(page, { databaseName = 'test.db', requiredBuild = 'synchronous', vfs, limits, foreignKeys, onOpenSql, extensions } = {}) {
  await page.goto('/index.html');
  await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

  return await page.evaluate(async options => {
    globalThis.host = await globalThis.BlazorSqlite.host.open(options);
    return await globalThis.host.version();
  }, { databaseName, requiredBuild, vfs, limits, foreignKeys, onOpenSql, extensions });
}

/**
 * Runs a batch through the binary path, encoding parameters and decoding rows through the library's
 * wire helpers.
 *
 * Parameters are given as `{ name, value }` with a natural JavaScript value. Two kinds cannot cross into
 * `page.evaluate` as themselves and are written as `{ name, bytes: [...] }` for a blob and
 * `{ name, big: '123' }` for a 64-bit integer.
 */
export function execute(page, batch) {
  return page.evaluate(async batch => {
    const { encodeValue, decodeValue, encodeBatchBinary, decodeReplyBinary } = globalThis.BlazorSqlite.host;

    const naturalValue = p => {
      if (p.bytes) return new Uint8Array(p.bytes);
      if (p.big) return BigInt(p.big);
      return p.value;
    };

    const wire = batch.map(request => ({
      commandText: request.commandText,
      resultKind: request.resultKind ?? 'nonQuery',
      maxRows: request.maxRows,
      parameters: (request.parameters ?? []).map(p => ({
        name: p.name,
        ...encodeValue(naturalValue(p)),
      })),
    }));

    const results = decodeReplyBinary(await globalThis.host.executeBinary(encodeBatchBinary(wire)));

    // Re-tagged for the trip to the test runner: JSON has no BigInt and no bytes.
    const forTest = value => {
      if (typeof value === 'bigint') return { bigint: String(value) };
      if (value instanceof Uint8Array) return { blob: Array.from(value) };
      return value;
    };

    return results.map(result => ({
      columnNames: result.columnNames,
      columnTypes: result.columnTypes,
      recordsAffected: result.recordsAffected,
      lastInsertRowId: Number(result.lastInsertRowId),
      rows: result.rows.map(row => row.v.map((value, i) => forTest(decodeValue(row.t[i], value)))),
    }));
  }, batch);
}

/**
 * Starts recording the host's change notifications. Each later call to {@link notifications}
 * returns the table lists received since the previous one, sorted.
 *
 * The worker posts a notification before the reply to the batch that caused it, and the host
 * handles messages in order, so a notification is recorded by the time that batch's promise settles.
 */
export async function recordNotifications(page) {
  await page.evaluate(() => {
    globalThis.notified = [];
    globalThis.host.onNotify(payload => globalThis.notified.push([...payload.tables].sort()));
  });
}

/** The notifications recorded since the last call; see {@link recordNotifications}. */
export function notifications(page) {
  return page.evaluate(() => globalThis.notified.splice(0));
}

/** Convenience for the common case of one statement whose rows are wanted. */
export async function query(page, commandText, parameters = []) {
  const [result] = await execute(page, [{ commandText, parameters, resultKind: 'reader' }]);
  return result;
}

/** Convenience for one statement whose rows are not wanted. */
export async function exec(page, commandText, parameters = []) {
  const [result] = await execute(page, [{ commandText, parameters, resultKind: 'nonQuery' }]);
  return result;
}

/**
 * Runs a batch expected to fail, returning the error as data.
 *
 * Written as a captured rejection rather than a Playwright `rejects` matcher so the SQLite result code
 * survives the boundary - a test asserting only on the message would pass for the wrong error.
 */
export function executeExpectingFailure(page, batch) {
  return page.evaluate(async batch => {
    const { encodeValue, encodeBatchBinary, decodeReplyBinary } = globalThis.BlazorSqlite.host;

    const wire = batch.map(request => ({
      commandText: request.commandText,
      resultKind: request.resultKind ?? 'nonQuery',
      parameters: (request.parameters ?? []).map(p => ({ name: p.name, ...encodeValue(p.value) })),
    }));

    try {
      decodeReplyBinary(await globalThis.host.executeBinary(encodeBatchBinary(wire)));
      return null;
    } catch (error) {
      return { message: error.message, sqliteCode: error.sqliteCode ?? null, name: error.name };
    }
  }, batch);
}

/**
 * Skips the current test where the browser has no OPFS.
 *
 * Playwright's own WebKit builds expose no `navigator.storage` at all - not in the window, not in a
 * worker, secure context notwithstanding - so anything that opens an OPFS database there fails before
 * SQLite is involved. Safari proper has OPFS; the tests are skipped rather than failed so the suite
 * still reports on what that WebKit can exercise (IndexedDB, Cache Storage, the engine itself).
 */
export async function skipUnlessOpfs(page, path = '/index.html') {
  await page.goto(path);
  const available = await page.evaluate(() => typeof navigator.storage?.getDirectory === 'function');
  test.skip(!available, 'this browser has no navigator.storage.getDirectory, so OPFS cannot be tested');
}
