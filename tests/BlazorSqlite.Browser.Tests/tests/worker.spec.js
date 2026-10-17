import { expect, test } from '@playwright/test';
import { exec, execute, executeExpectingFailure, openHost, query } from './host.js';

test.beforeEach(async ({ page }) => {
  await openHost(page);
  await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT, price REAL)');
});

/**
 * SQLite defaults foreign keys off; Microsoft.Data.Sqlite turns them on for every connection. The
 * worker has to do the same, or the same model enforces its relationships on the server and
 * silently does not in the browser - which is the one difference this library exists to not have.
 */
test.describe('foreign keys', () => {
  test.beforeEach(async ({ page }) => {
    await exec(page, 'CREATE TABLE parent (id INTEGER PRIMARY KEY)');
    await exec(page,
      'CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE)');
  });

  test('are on, the way Microsoft.Data.Sqlite leaves them', async ({ page }) => {
    const result = await query(page, 'PRAGMA foreign_keys');
    expect(result.rows).toEqual([[1]]);
  });

  test('reject an orphaned row rather than storing it', async ({ page }) => {
    const error = await executeExpectingFailure(page, [
      { commandText: 'INSERT INTO child (id, parent_id) VALUES (1, 999)' },
    ]);

    expect(error).not.toBeNull();
    expect(error.message).toContain('FOREIGN KEY');
    expect(error.sqliteCode).toBe(19);
  });

  test('cascade a delete to the child rows', async ({ page }) => {
    await exec(page, 'INSERT INTO parent (id) VALUES (1)');
    await exec(page, 'INSERT INTO child (id, parent_id) VALUES (1, 1)');

    await exec(page, 'DELETE FROM parent WHERE id = 1');

    const remaining = await query(page, 'SELECT COUNT(*) FROM child');
    expect(remaining.rows).toEqual([[0]]);
  });

  // EF's SQLite migrations rebuild tables with the pragma toggled off and back on, so it has to
  // stay something the application can change.
  test('can still be turned off by the application', async ({ page }) => {
    await exec(page, 'PRAGMA foreign_keys=OFF');

    expect((await query(page, 'PRAGMA foreign_keys')).rows).toEqual([[0]]);
    await exec(page, 'INSERT INTO child (id, parent_id) VALUES (1, 999)');
  });
});

test.describe('executing SQL', () => {
  test('round-trips a row through parameters', async ({ page }) => {
    const insert = await exec(
      page,
      'INSERT INTO product (name, price) VALUES (@name, @price)',
      [{ name: '@name', value: 'Widget' }, { name: '@price', value: 9.5 }]);

    expect(insert.recordsAffected).toBe(1);

    const selected = await query(page, 'SELECT id, name, price FROM product');

    expect(selected.columnNames).toEqual(['id', 'name', 'price']);
    expect(selected.rows).toEqual([[1, 'Widget', 9.5]]);
  });

  test('matches parameter names with or without their sigil', async ({ page }) => {
    // ADO.NET callers write both, and Microsoft.Data.Sqlite accepts both.
    await exec(page, 'INSERT INTO product (name) VALUES (@name)', [{ name: 'name', value: 'Bare' }]);

    const selected = await query(page, 'SELECT name FROM product');

    expect(selected.rows).toEqual([['Bare']]);
  });

  test('binds positional markers by position', async ({ page }) => {
    await exec(
      page,
      'INSERT INTO product (name, price) VALUES (?, ?)',
      [{ name: '?1', value: 'Positional' }, { name: '?2', value: 1.25 }]);

    expect((await query(page, 'SELECT name, price FROM product')).rows)
      .toEqual([['Positional', 1.25]]);
  });

  /**
   * The failure this prevents is the quiet one: binding a name the statement never asked for would
   * otherwise leave the real parameter NULL and return wrong rows instead of an error.
   */
  test('refuses to run a statement with a parameter left unbound', async ({ page }) => {
    const error = await executeExpectingFailure(page, [{
      commandText: 'INSERT INTO product (name) VALUES (@name)',
      parameters: [{ name: '@misspelled', value: 'Widget' }],
      resultKind: 'nonQuery',
    }]);

    expect(error).not.toBeNull();
    expect(error.message).toContain('@name');
    expect(error.message).toContain('@misspelled');
  });

  test('reports storage classes per column', async ({ page }) => {
    await exec(
      page,
      "INSERT INTO product (name, price) VALUES ('Mixed', 3)");

    // The alias avoids NOTHING, which SQLite reserves for `ON CONFLICT DO NOTHING`.
    const selected = await query(page, 'SELECT id, name, price, NULL AS absent FROM product');

    // 'price' is declared REAL but was given an integer, and this reports what SQLite stored rather
    // than what the column was declared as - the engine is built without declared types at all.
    expect(selected.columnTypes).toEqual(['INTEGER', 'TEXT', 'REAL', null]);
  });
});

test.describe('batching', () => {
  test('runs a batch in order and returns one result per request', async ({ page }) => {
    const results = await execute(page, [
      {
        commandText: 'INSERT INTO product (name) VALUES (@n)',
        parameters: [{ name: '@n', value: 'First' }],
        resultKind: 'nonQuery',
      },
      {
        commandText: 'INSERT INTO product (name) VALUES (@n)',
        parameters: [{ name: '@n', value: 'Second' }],
        resultKind: 'nonQuery',
      },
      { commandText: 'SELECT name FROM product ORDER BY id', resultKind: 'reader' },
    ]);

    expect(results).toHaveLength(3);
    expect(results[0].recordsAffected).toBe(1);
    expect(results[1].recordsAffected).toBe(1);
    expect(results[2].rows).toEqual([['First'], ['Second']]);
  });

  /**
   * EF Core's insert pattern: the write and the identity read arrive as one command, and the reader has
   * to see the SELECT's rows while the INSERT's effect is still counted.
   */
  test('takes rows from the first row-producing statement and still runs the rest', async ({ page }) => {
    const result = await query(
      page,
      `INSERT INTO product (name) VALUES (@n);
       SELECT id FROM product WHERE rowid = last_insert_rowid();`,
      [{ name: '@n', value: 'Identity' }]);

    expect(result.columnNames).toEqual(['id']);
    expect(result.rows).toEqual([[1]]);

    // Exactly one: sqlite3_changes still says 1 after the SELECT, and a count that asked it after
    // every statement billed the row twice.
    expect(result.recordsAffected).toBe(1);
  });

  test('a scalar request stops after the first row', async ({ page }) => {
    await exec(page, "INSERT INTO product (name) VALUES ('A'), ('B'), ('C')");

    const [result] = await execute(page, [{
      commandText: 'SELECT name FROM product ORDER BY id',
      resultKind: 'scalar',
    }]);

    expect(result.rows).toEqual([['A']]);
  });

  test('a failing request does not leave the connection unusable', async ({ page }) => {
    const error = await executeExpectingFailure(page, [
      { commandText: 'SELECT * FROM nonexistent', resultKind: 'reader' },
    ]);

    expect(error).not.toBeNull();
    expect(error.sqliteCode).not.toBeNull();

    // The queue must survive a rejected request; otherwise one bad query poisons the worker.
    expect((await query(page, 'SELECT COUNT(*) FROM product')).rows).toEqual([[0]]);
  });
});

test.describe('value round-tripping', () => {
  test('carries every storage class', async ({ page }) => {
    await exec(page, 'CREATE TABLE values_ (i INTEGER, r REAL, t TEXT, b BLOB, n INTEGER)');
    await exec(page, 'INSERT INTO values_ VALUES (@i, @r, @t, @b, @n)', [
      { name: '@i', value: 42 },
      { name: '@r', value: 1.5 },
      { name: '@t', value: 'text' },
      { name: '@b', bytes: [1, 2, 250] },
      { name: '@n', value: null },
    ]);

    const result = await query(page, 'SELECT i, r, t, b, n FROM values_');

    expect(result.rows[0][0]).toBe(42);
    expect(result.rows[0][1]).toBe(1.5);
    expect(result.rows[0][2]).toBe('text');
    expect(result.rows[0][3]).toEqual({ blob: [1, 2, 250] });
    expect(result.rows[0][4]).toBeNull();
  });

  /**
   * JSON has no spelling for Infinity - JSON.stringify writes null - and the .NET transport reads the
   * rows through JSON. So it travels as a string, and NaN, which SQLite stores as NULL, comes back
   * as one.
   */
  test('carries an infinite REAL, and NaN as the NULL SQLite makes of it', async ({ page }) => {
    await exec(page, 'CREATE TABLE reals (r REAL)');
    await exec(page, 'INSERT INTO reals VALUES (@a), (@b), (@c)', [
      { name: '@a', value: Number.POSITIVE_INFINITY },
      { name: '@b', value: Number.NEGATIVE_INFINITY },
      { name: '@c', value: Number.NaN },
    ]);

    const result = await query(page, 'SELECT r FROM reals');

    expect(result.rows).toEqual([[Number.POSITIVE_INFINITY], [Number.NEGATIVE_INFINITY], [null]]);

    // What actually crossed the wire: a string, not the null JSON would have made of it.
    const raw = await page.evaluate(async () => {
      const [first] = await globalThis.host.execute([{ commandText: 'SELECT r FROM reals', resultKind: 'reader' }]);
      return first.rows.map(row => row.v[0]);
    });
    expect(raw).toEqual(['Infinity', '-Infinity', null]);
  });

  /**
   * Integers beyond 2^53 cannot be represented exactly as a JavaScript number, and SQLite's INTEGER is
   * 64-bit. The engine hands these back as BigInt, which the .NET transport must be prepared for -
   * JSON interop cannot carry it.
   */
  test('returns a big integer as a BigInt rather than losing precision', async ({ page }) => {
    await exec(page, 'CREATE TABLE big (v INTEGER)');
    await exec(page, 'INSERT INTO big (v) VALUES (9007199254740993)');

    const result = await query(page, 'SELECT v FROM big');

    expect(result.rows).toEqual([[{ bigint: '9007199254740993' }]]);
  });

  /** The same boundary on the way in: a 64-bit parameter must bind as int64, not as a rounded double. */
  test('accepts a big integer as a parameter without rounding it', async ({ page }) => {
    await exec(page, 'CREATE TABLE big (v INTEGER)');
    await exec(page, 'INSERT INTO big (v) VALUES (@v)', [{ name: '@v', big: '9223372036854775807' }]);

    const result = await query(page, 'SELECT v FROM big');

    expect(result.rows).toEqual([[{ bigint: '9223372036854775807' }]]);
  });

  test('round-trips a blob too large for a single fromCharCode call', async ({ page }) => {
    await exec(page, 'CREATE TABLE blobs (b BLOB)');

    // Past the 0x8000 chunk boundary in the base64 encoder, where an unchunked implementation throws.
    const bytes = Array.from({ length: 70_000 }, (_, i) => i % 251);
    await exec(page, 'INSERT INTO blobs (b) VALUES (@b)', [{ name: '@b', bytes }]);

    const result = await query(page, 'SELECT b, length(b) AS size FROM blobs');

    expect(result.rows[0][1]).toBe(70_000);
    expect(result.rows[0][0].blob).toEqual(bytes);
  });
});

test.describe('request serialization', () => {
  /**
   * One SQLite connection cannot execute two statements at once. Requests issued without awaiting must
   * therefore queue, and the observable proof is that a counter incremented by overlapping batches
   * lands on the exact total.
   */
  test('overlapping batches queue instead of interleaving', async ({ page }) => {
    await exec(page, 'CREATE TABLE counter (n INTEGER)');
    await exec(page, 'INSERT INTO counter (n) VALUES (0)');

    await page.evaluate(async () => {
      const bump = () => globalThis.host.execute([{
        commandText: 'UPDATE counter SET n = n + 1',
        resultKind: 'nonQuery',
      }]);

      await Promise.all(Array.from({ length: 25 }, bump));
    });

    expect((await query(page, 'SELECT n FROM counter')).rows).toEqual([[25]]);
  });

  /**
   * A worker whose script never loaded has nobody listening. The first request fails through the
   * error event; the ones after it must fail the same way rather than wait for an answer that is
   * never coming.
   */
  test('a worker that failed to load rejects every request, not just the first', async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

    const outcomes = await page.evaluate(async () => {
      const host = globalThis.BlazorSqlite.host.createHost('/_content/BlazorSqlite/no-such-worker.js');
      const attempt = async () => {
        try {
          await host.version();
          return 'resolved';
        } catch (error) {
          return error.message;
        }
      };

      const first = await attempt();
      const second = await attempt();
      host.dispose();
      return { first, second };
    });

    expect(outcomes.first).toMatch(/SQLite worker failed/);
    expect(outcomes.second).toMatch(/SQLite worker failed/);
  });

  test('disposing rejects requests still in flight', async ({ page }) => {
    const outcome = await page.evaluate(async () => {
      const pending = globalThis.host.execute([
        { commandText: 'SELECT 1', resultKind: 'scalar' },
      ]);

      globalThis.host.dispose();

      try {
        await pending;
        return 'resolved';
      } catch (error) {
        return error.message;
      }
    });

    expect(outcome).toContain('disposed');
  });
});
