import { expect, test } from '@playwright/test';
import {
  exec,
  execute,
  executeExpectingFailure,
  notifications,
  openHost,
  query,
  recordNotifications,
} from './host.js';

/**
 * Behaviours of the worker's engine loop that the .NET layer relies on: the statement cache, the
 * engine-reported change notifications, accurate affected-row counts, open-time configuration,
 * extension modules, and in-place snapshots.
 */
test.describe('statement cache', () => {
  test.beforeEach(async ({ page }) => {
    await openHost(page, { databaseName: `cache-${Date.now()}` });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT, price REAL)');
  });

  test('a repeated statement runs with fresh parameters each time', async ({ page }) => {
    for (const name of ['A', 'B', 'C']) {
      await exec(page, 'INSERT INTO product (name) VALUES (@name)', [{ name: '@name', value: name }]);
    }

    const rows = (await query(page, 'SELECT name FROM product ORDER BY id')).rows;
    expect(rows).toEqual([['A'], ['B'], ['C']]);

    // The same SELECT text, now with a parameter that changed and then a parameter that was cleared.
    const one = await query(page, 'SELECT name FROM product WHERE name = @n', [{ name: '@n', value: 'B' }]);
    expect(one.rows).toEqual([['B']]);
    const none = await query(page, 'SELECT name FROM product WHERE name = @n', [{ name: '@n', value: 'Z' }]);
    expect(none.rows).toEqual([]);
  });

  test('a cached statement survives a schema change', async ({ page }) => {
    await exec(page, "INSERT INTO product (name) VALUES ('A')");
    expect((await query(page, 'SELECT * FROM product')).columnNames).toEqual(['id', 'name', 'price']);

    await exec(page, 'ALTER TABLE product ADD COLUMN sku TEXT');

    expect((await query(page, 'SELECT * FROM product')).columnNames).toEqual(['id', 'name', 'price', 'sku']);
  });

  test('a failing statement does not poison its cached handle', async ({ page }) => {
    await exec(page, 'CREATE UNIQUE INDEX ux ON product (name)');
    await exec(page, "INSERT INTO product (name) VALUES ('dup')");

    const error = await executeExpectingFailure(page, [{
      commandText: 'INSERT INTO product (name) VALUES (@n)',
      parameters: [{ name: '@n', value: 'dup' }],
    }]);
    expect(error.sqliteCode).toBe(19);

    // Same text again, now with a value that works: the reset in between must have cleared the error.
    const ok = await exec(page, 'INSERT INTO product (name) VALUES (@n)', [{ name: '@n', value: 'fresh' }]);
    expect(ok.recordsAffected).toBe(1);
  });

  test('many distinct statements do not exhaust the cache', async ({ page }) => {
    for (let i = 0; i < 200; i++) {
      await exec(page, `INSERT INTO product (name) VALUES ('n${i}') -- ${i}`);
    }

    expect((await query(page, 'SELECT COUNT(*) FROM product')).rows).toEqual([[200]]);
  });
});

test.describe('engine-reported changes', () => {
  test.beforeEach(async ({ page }) => {
    await openHost(page, { databaseName: `changes-${Date.now()}` });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, 'CREATE TABLE line (id INTEGER PRIMARY KEY, product_id INTEGER REFERENCES product(id) ON DELETE CASCADE)');
    await recordNotifications(page);
  });

  test('a write names the tables it changed, including through a cascade', async ({ page }) => {
    await exec(page, "INSERT INTO product (name) VALUES ('A')");
    await exec(page, 'INSERT INTO line (product_id) VALUES (1)');
    await notifications(page);

    await exec(page, 'DELETE FROM product WHERE id = 1');

    expect(await notifications(page)).toEqual([['line', 'product']]);
  });

  test('a read and a write that changed nothing report no tables', async ({ page }) => {
    await query(page, 'SELECT * FROM product');
    await exec(page, 'DELETE FROM product WHERE id = 42');

    expect(await notifications(page)).toEqual([]);
  });

  test('a truncating DELETE, which the update hook does not see, is still reported', async ({ page }) => {
    await exec(page, "INSERT INTO product (name) VALUES ('A')");
    await notifications(page);

    await exec(page, 'DELETE FROM line');
    await exec(page, 'DELETE FROM product');

    expect(await notifications(page)).toEqual([['product']]);
  });

  test('DDL names the table it touched', async ({ page }) => {
    await exec(page, 'CREATE TABLE extra (id INTEGER PRIMARY KEY)');

    expect(await notifications(page)).toEqual([['extra']]);
  });

  test('nothing is reported until the transaction commits, and nothing at all on rollback', async ({ page }) => {
    await exec(page, 'BEGIN');
    await exec(page, "INSERT INTO product (name) VALUES ('A')");
    expect(await notifications(page)).toEqual([]);
    await exec(page, 'COMMIT');
    expect(await notifications(page)).toEqual([['product']]);

    await exec(page, 'BEGIN');
    await exec(page, "INSERT INTO product (name) VALUES ('B')");
    await exec(page, 'ROLLBACK');
    expect(await notifications(page)).toEqual([]);
  });

  test('the cascade also reaches the notification channel', async ({ page }) => {
    await exec(page, "INSERT INTO product (name) VALUES ('A')");
    await exec(page, 'INSERT INTO line (product_id) VALUES (1)');

    const tables = await page.evaluate(async () => {
      const seen = new Promise(resolve => {
        globalThis.host.onNotify(payload => resolve(payload.tables));
      });
      await globalThis.host.execute([{ commandText: 'DELETE FROM product WHERE id = 1', resultKind: 'nonQuery' }]);
      return await seen;
    });

    expect(tables.sort()).toEqual(['line', 'product']);
  });
});

test.describe('counters', () => {
  test.beforeEach(async ({ page }) => {
    await openHost(page, { databaseName: `counters-${Date.now()}` });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
  });

  test('a SELECT after an INSERT does not count the insert twice', async ({ page }) => {
    const result = await query(
      page,
      "INSERT INTO product (name) VALUES ('x'); SELECT id FROM product WHERE rowid = last_insert_rowid();");

    expect(result.recordsAffected).toBe(1);
    expect(result.rows).toEqual([[1]]);
  });

  test('a read-only batch reports zero affected rows', async ({ page }) => {
    await exec(page, "INSERT INTO product (name) VALUES ('x')");

    expect((await query(page, 'SELECT * FROM product')).recordsAffected).toBe(0);
  });

  test('last insert rowid is carried on the result', async ({ page }) => {
    await exec(page, "INSERT INTO product (name) VALUES ('a')");
    const second = await exec(page, "INSERT INTO product (name) VALUES ('b')");

    expect(second.lastInsertRowId).toBe(2);
  });

  test('a row limit stops the reader early', async ({ page }) => {
    await exec(page, "INSERT INTO product (name) VALUES ('a'), ('b'), ('c')");

    const [limited] = await execute(page, [{ commandText: 'SELECT name FROM product ORDER BY id', resultKind: 'reader', maxRows: 2 }]);
    expect(limited.rows).toEqual([['a'], ['b']]);

    const [schemaOnly] = await execute(page, [{ commandText: 'SELECT id, name FROM product', resultKind: 'reader', maxRows: 0 }]);
    expect(schemaOnly.columnNames).toEqual(['id', 'name']);
    expect(schemaOnly.rows).toEqual([]);
  });
});

test.describe('open-time configuration', () => {
  test('foreign keys are enforced by default', async ({ page }) => {
    await openHost(page, { databaseName: `fk-${Date.now()}` });
    await exec(page, 'CREATE TABLE parent (id INTEGER PRIMARY KEY)');
    await exec(page, 'CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id))');

    const error = await executeExpectingFailure(page, [{ commandText: 'INSERT INTO child (parent_id) VALUES (99)' }]);

    expect(error).not.toBeNull();
    expect(error.sqliteCode).toBe(19);
    expect((await query(page, 'PRAGMA foreign_keys')).rows).toEqual([[1]]);
  });

  test('foreign keys stay off when the caller opts out', async ({ page }) => {
    await openHost(page, { databaseName: `nofk-${Date.now()}`, foreignKeys: false });

    expect((await query(page, 'PRAGMA foreign_keys')).rows).toEqual([[0]]);
  });

  test('open-time statements run after the defaults', async ({ page }) => {
    await openHost(page, { databaseName: `onopen-${Date.now()}`, onOpenSql: ['PRAGMA foreign_keys = OFF'] });

    expect((await query(page, 'PRAGMA foreign_keys')).rows).toEqual([[0]]);
  });

  test('an extension module can add a function', async ({ page }) => {
    await openHost(page, {
      databaseName: `ext-${Date.now()}`,
      extensions: [{ moduleUrl: '/extension.js', registerExport: 'register' }],
    });

    expect((await query(page, "SELECT shout('hi')")).rows).toEqual([['HI!']]);
  });
});

test.describe('snapshots', () => {
  test('export returns a SQLite image of the open database', async ({ page }) => {
    await openHost(page, { databaseName: `export-${Date.now()}` });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('kept')");

    const header = await page.evaluate(async () => {
      const image = await globalThis.host.export();
      return { length: image.byteLength, magic: new TextDecoder().decode(image.subarray(0, 15)) };
    });

    expect(header.magic).toBe('SQLite format 3');
    expect(header.length).toBeGreaterThanOrEqual(4096);
  });
});
