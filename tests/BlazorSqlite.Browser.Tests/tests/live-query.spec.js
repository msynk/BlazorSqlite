import { expect, test } from '@playwright/test';
import { exec, openHost } from './host.js';

test.describe('live queries', () => {
  test('a write notifies the host with the table name', async ({ page }) => {
    await openHost(page, { databaseName: `live-${Date.now()}` });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');

    const tables = await page.evaluate(async () => {
      const seen = new Promise(resolve => {
        globalThis.host.onNotify(payload => resolve(payload.tables));
      });
      await globalThis.host.execute([{
        commandText: "INSERT INTO product (name) VALUES ('Live')",
        resultKind: 'nonQuery',
      }]);
      return await seen;
    });

    expect(tables).toContain('product');
  });

  /**
   * Nothing goes out while a transaction is open: another tab that re-ran on the INSERT could not
   * see the row and would never hear about the commit. The COMMIT batch is what carries the name.
   */
  test('a transaction is reported once, at commit, with every table it wrote', async ({ page }) => {
    await openHost(page, { databaseName: `live-tx-${Date.now()}` });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, 'CREATE TABLE customer (id INTEGER PRIMARY KEY, name TEXT)');

    const notifications = await page.evaluate(async () => {
      const seen = [];
      globalThis.host.onNotify(payload => seen.push([...payload.tables].sort()));

      const run = sql => globalThis.host.execute([{ commandText: sql, resultKind: 'nonQuery' }]);
      await run('BEGIN');
      await run("INSERT INTO product (name) VALUES ('Live')");
      await run("INSERT INTO customer (name) VALUES ('Live')");
      const beforeCommit = seen.length;
      await run('COMMIT');
      return { beforeCommit, seen };
    });

    expect(notifications.beforeCommit).toBe(0);
    expect(notifications.seen).toEqual([['customer', 'product']]);
  });

  test('a rolled-back transaction is not reported', async ({ page }) => {
    await openHost(page, { databaseName: `live-rollback-${Date.now()}` });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');

    const count = await page.evaluate(async () => {
      let seen = 0;
      globalThis.host.onNotify(() => seen++);

      const run = sql => globalThis.host.execute([{ commandText: sql, resultKind: 'nonQuery' }]);
      await run('BEGIN');
      await run("INSERT INTO product (name) VALUES ('Ghost')");
      await run('ROLLBACK');
      return seen;
    });

    expect(count).toBe(0);
  });

  /**
   * The update hook sees what the SQL text cannot: nothing in this DELETE names the child table,
   * yet its rows are gone and a live query over it has to know.
   */
  test('a cascading delete names the child table', async ({ page }) => {
    await openHost(page, { databaseName: `live-cascade-${Date.now()}` });
    await exec(page, 'CREATE TABLE parent (id INTEGER PRIMARY KEY)');
    await exec(page, 'CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE)');
    await exec(page, 'INSERT INTO parent (id) VALUES (1)');
    await exec(page, 'INSERT INTO child (id, parent_id) VALUES (1, 1)');

    const tables = await page.evaluate(async () => {
      const seen = new Promise(resolve => {
        globalThis.host.onNotify(payload => resolve([...payload.tables].sort()));
      });
      await globalThis.host.execute([{ commandText: 'DELETE FROM parent WHERE id = 1', resultKind: 'nonQuery' }]);
      return await seen;
    });

    expect(tables).toEqual(['child', 'parent']);
  });

  test('a second tab hears the write over BroadcastChannel', async ({ browser }) => {
    const databaseName = `live-tab-${Date.now()}`;
    const context = await browser.newContext();
    const first = await context.newPage();
    const second = await context.newPage();

    try {
      await openHost(first, { databaseName });
      await openHost(second, { databaseName: `${databaseName}-other` });
      await exec(first, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');

      const notified = second.evaluate(() => new Promise(resolve => {
        globalThis.host.onNotify(payload => resolve(payload.tables));
      }));

      await exec(first, "INSERT INTO product (name) VALUES ('CrossTab')");
      expect(await notified).toContain('product');
    } finally {
      await context.close();
    }
  });
});
