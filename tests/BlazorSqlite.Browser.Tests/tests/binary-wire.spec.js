import { expect, test } from '@playwright/test';
import { openHost } from './host.js';

/**
 * The binary wire format is what the .NET transport speaks. These tests pin the layout from the
 * JavaScript side; `SqliteWireFormatTests` pins it from the .NET side with a byte-for-byte builder.
 */
test.describe('binary wire format', () => {
  test.beforeEach(async ({ page }) => {
    await openHost(page, { databaseName: `wire-${Date.now()}` });
  });

  test('a batch encodes to the documented header', async ({ page }) => {
    const head = await page.evaluate(() => {
      const { encodeBatchBinary } = globalThis.BlazorSqlite.host;
      const bytes = encodeBatchBinary([{ commandText: 'SELECT 1', resultKind: 'scalar', parameters: [] }]);
      return Array.from(bytes.subarray(0, 6));
    });

    // magic, version, request count (u32 LE)
    expect(head).toEqual([0xb5, 1, 1, 0, 0, 0]);
  });

  test('every storage class survives the round trip', async ({ page }) => {
    const row = await page.evaluate(async () => {
      const { encodeBatchBinary, decodeReplyBinary, encodeValue } = globalThis.BlazorSqlite.host;
      const run = async batch => decodeReplyBinary(await globalThis.host.executeBinary(encodeBatchBinary(batch)));

      await run([{ commandText: 'CREATE TABLE v (i INTEGER, r REAL, t TEXT, b BLOB, n INTEGER)', parameters: [] }]);
      await run([{
        commandText: 'INSERT INTO v VALUES (@i, @r, @t, @b, @n)',
        parameters: [
          { name: '@i', ...encodeValue(9223372036854775807n) },
          { name: '@r', ...encodeValue(-2.5) },
          { name: '@t', ...encodeValue('héllo 🌍') },
          { name: '@b', ...encodeValue(new Uint8Array([0, 128, 255])) },
          { name: '@n', ...encodeValue(null) },
        ],
      }]);

      const [result] = await run([{ commandText: 'SELECT i, r, t, b, n FROM v', resultKind: 'reader', parameters: [] }]);
      return { types: result.columnTypes, t: result.rows[0].t, v: result.rows[0].v };
    });

    expect(row.types).toEqual(['INTEGER', 'REAL', 'TEXT', 'BLOB', null]);
    expect(row.t).toEqual([1, 2, 3, 4, 5]);
    expect(row.v[0]).toBe('9223372036854775807');
    expect(row.v[1]).toBe(-2.5);
    expect(row.v[2]).toBe('héllo 🌍');
    expect(row.v[3]).toBe(btoa(String.fromCharCode(0, 128, 255)));
    expect(row.v[4]).toBeNull();
  });

  test('a failure is a reply, not a rejection', async ({ page }) => {
    const outcome = await page.evaluate(async () => {
      const { encodeBatchBinary, decodeReplyBinary } = globalThis.BlazorSqlite.host;
      const reply = await globalThis.host.executeBinary(encodeBatchBinary([
        { commandText: 'SELECT * FROM nope', resultKind: 'reader', parameters: [] },
      ]));

      try {
        decodeReplyBinary(reply);
        return { threw: false };
      } catch (error) {
        return { threw: true, message: error.message, sqliteCode: error.sqliteCode };
      }
    });

    expect(outcome.threw).toBe(true);
    expect(outcome.message).toMatch(/no such table/);
    expect(outcome.sqliteCode).toBe(1);
  });

  test('a corrupt batch is reported, not thrown', async ({ page }) => {
    const outcome = await page.evaluate(async () => {
      const { decodeReplyBinary } = globalThis.BlazorSqlite.host;
      const reply = await globalThis.host.executeBinary(new Uint8Array([1, 2, 3]));
      try {
        decodeReplyBinary(reply);
        return null;
      } catch (error) {
        return error.message;
      }
    });

    expect(outcome).toMatch(/Not a BlazorSqlite wire message/);
  });

  test('a large result set round-trips intact', async ({ page }) => {
    const summary = await page.evaluate(async () => {
      const { encodeBatchBinary, decodeReplyBinary } = globalThis.BlazorSqlite.host;
      const run = async batch => decodeReplyBinary(await globalThis.host.executeBinary(encodeBatchBinary(batch)));

      await run([{ commandText: 'CREATE TABLE big (id INTEGER PRIMARY KEY, payload TEXT)', parameters: [] }]);
      await run([{
        commandText: "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 5000) INSERT INTO big (payload) SELECT 'row-' || i FROM n",
        parameters: [],
      }]);

      const [result] = await run([{ commandText: 'SELECT id, payload FROM big ORDER BY id', resultKind: 'reader', parameters: [] }]);
      return { count: result.rows.length, last: result.rows[4999].v };
    });

    expect(summary.count).toBe(5000);
    expect(summary.last).toEqual([5000, 'row-5000']);
  });
});
