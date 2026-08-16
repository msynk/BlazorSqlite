import { expect, test } from '@playwright/test';

/**
 * The exact conversation the .NET transport has with the host: create a host, then drive every
 * operation through `call` so a failure is data rather than a thrown error. These tests pin the
 * envelope shape `SqliteWireFormat.DecodeCall` reads; a change here without a matching change there
 * is a broken release.
 */
test.describe('the JS interop envelope', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);
  });

  test('createHost plus call opens a database without throwing', async ({ page }) => {
    const envelope = await page.evaluate(async () => {
      const host = globalThis.BlazorSqlite.host.createHost();
      globalThis.host = host;
      return await host.call({
        kind: 'open',
        databaseName: 'interop.db',
        requiredBuild: 'synchronous',
      });
    });

    expect(envelope).toEqual({
      ok: true,
      result: { build: 'synchronous', vfsName: null, reused: false },
    });
  });

  test('a successful execute carries tagged rows, not raw JavaScript values', async ({ page }) => {
    const envelope = await page.evaluate(async () => {
      const { createHost, encodeValue } = globalThis.BlazorSqlite.host;
      const host = createHost();
      await host.call({ kind: 'open', databaseName: 'wire.db', requiredBuild: 'synchronous' });

      await host.call({
        kind: 'execute',
        batch: [{
          commandText: 'CREATE TABLE t (i INTEGER, b BLOB)',
          resultKind: 'nonQuery',
          parameters: [],
        }],
      });

      await host.call({
        kind: 'execute',
        batch: [{
          commandText: 'INSERT INTO t (i, b) VALUES (@i, @b)',
          resultKind: 'nonQuery',
          parameters: [
            { name: '@i', ...encodeValue(9007199254740993n) },
            { name: '@b', ...encodeValue(new Uint8Array([1, 2, 250])) },
          ],
        }],
      });

      return await host.call({
        kind: 'execute',
        batch: [{ commandText: 'SELECT i, b FROM t', resultKind: 'reader', parameters: [] }],
      });
    });

    expect(envelope.ok).toBe(true);
    expect(envelope.result).toHaveLength(1);

    const row = envelope.result[0].rows[0];
    expect(row.t).toEqual([1, 4]);
    // A number would have already lost the low bits; a string is the whole point of the format.
    expect(row.v[0]).toBe('9007199254740993');
    expect(row.v[1]).toBe(btoa(String.fromCharCode(1, 2, 250)));
  });

  test('a failing execute is an envelope with the SQLite result code, not a thrown error', async ({ page }) => {
    const envelope = await page.evaluate(async () => {
      const host = globalThis.BlazorSqlite.host.createHost();
      await host.call({ kind: 'open', databaseName: 'fail.db', requiredBuild: 'synchronous' });
      await host.call({
        kind: 'execute',
        batch: [{
          commandText: 'CREATE TABLE t (id INTEGER PRIMARY KEY)',
          resultKind: 'nonQuery',
          parameters: [],
        }],
      });
      await host.call({
        kind: 'execute',
        batch: [{
          commandText: 'INSERT INTO t (id) VALUES (1)',
          resultKind: 'nonQuery',
          parameters: [],
        }],
      });

      return await host.call({
        kind: 'execute',
        batch: [{
          commandText: 'INSERT INTO t (id) VALUES (1)',
          resultKind: 'nonQuery',
          parameters: [],
        }],
      });
    });

    expect(envelope.ok).toBe(false);
    expect(envelope.error.sqliteCode).toBe(19);
    expect(envelope.error.message).toMatch(/UNIQUE|constraint/i);
  });
});
