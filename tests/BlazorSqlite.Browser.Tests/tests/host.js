// Helpers for driving the library from test code.
//
// Tests write plain JavaScript values and read plain results; the wire tagging is applied and undone
// here using the library's own encoders, so tests exercise the real format rather than a parallel one.
//
// Values that JSON cannot carry are tagged again on the way out to Playwright, so that "came back as a
// 64-bit integer" and "came back as a blob" stay observable instead of being silently coerced.

/**
 * Loads the host page and opens a database.
 *
 * @returns {Promise<{version: string, build: string}>} what the worker reports about itself
 */
export async function openHost(page, { databaseName = 'test.db', requiredBuild = 'synchronous', vfs, limits } = {}) {
  await page.goto('/index.html');
  await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

  return await page.evaluate(async options => {
    globalThis.host = await globalThis.BlazorSqlite.host.open(options);
    return await globalThis.host.version();
  }, { databaseName, requiredBuild, vfs, limits });
}

/**
 * Runs a batch, encoding parameters and decoding rows through the library's wire helpers.
 *
 * Parameters are given as `{ name, value }` with a natural JavaScript value. Two kinds cannot cross into
 * `page.evaluate` as themselves and are written as `{ name, bytes: [...] }` for a blob and
 * `{ name, big: '123' }` for a 64-bit integer.
 */
export function execute(page, batch) {
  return page.evaluate(async batch => {
    const { encodeValue, decodeValue } = globalThis.BlazorSqlite.host;

    const naturalValue = p => {
      if (p.bytes) return new Uint8Array(p.bytes);
      if (p.big) return BigInt(p.big);
      return p.value;
    };

    const wire = batch.map(request => ({
      commandText: request.commandText,
      resultKind: request.resultKind ?? 'nonQuery',
      parameters: (request.parameters ?? []).map(p => ({
        name: p.name,
        ...encodeValue(naturalValue(p)),
      })),
    }));

    const results = await globalThis.host.execute(wire);

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
      rows: result.rows.map(row => row.v.map((value, i) => forTest(decodeValue(row.t[i], value)))),
    }));
  }, batch);
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
 * survives the boundary — a test asserting only on the message would pass for the wrong error.
 */
export function executeExpectingFailure(page, batch) {
  return page.evaluate(async batch => {
    const { encodeValue } = globalThis.BlazorSqlite.host;

    const wire = batch.map(request => ({
      commandText: request.commandText,
      resultKind: request.resultKind ?? 'nonQuery',
      parameters: (request.parameters ?? []).map(p => ({ name: p.name, ...encodeValue(p.value) })),
    }));

    try {
      await globalThis.host.execute(wire);
      return null;
    } catch (error) {
      return { message: error.message, sqliteCode: error.sqliteCode ?? null, name: error.name };
    }
  }, batch);
}
