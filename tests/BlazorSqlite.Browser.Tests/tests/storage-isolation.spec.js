import { expect, test } from '@playwright/test';
import { exec, openHost, query } from './host.js';

const OPFS = {
  requiredBuild: 'synchronous',
  vfs: {
    moduleUrl: '/_content/BlazorSqlite.Storage.Opfs/opfs-vfs.js',
    registerExport: 'register',
  },
};

const IDB = {
  requiredBuild: 'asyncCapable',
  vfs: {
    moduleUrl: '/_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js',
    registerExport: 'register',
  },
};

/**
 * The reason sticky binding exists, shown at the storage layer: the same filename on a different
 * VFS is a different, empty database. Switching backends without the binding would look like
 * success and lose the rows.
 */
test.describe('storage isolation', () => {
  test('an IndexedDB write is invisible on OPFS under the same name', async ({ page }) => {
    const databaseName = `iso-idb-${Date.now()}-${Math.random().toString(36).slice(2)}`;

    await openHost(page, { databaseName, ...IDB });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('OnlyInIndexedDb')");
    await page.evaluate(async () => {
      await globalThis.host.close();
      globalThis.host.dispose();
    });

    await openHost(page, { databaseName, ...OPFS });
    const tables = await query(
      page,
      "SELECT name FROM sqlite_master WHERE type='table' AND name='product'");
    expect(tables.rows).toEqual([]);
  });

  test('an OPFS write is invisible on IndexedDB under the same name', async ({ page }) => {
    const databaseName = `iso-opfs-${Date.now()}-${Math.random().toString(36).slice(2)}`;

    await openHost(page, { databaseName, ...OPFS });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('OnlyInOpfs')");
    await page.evaluate(async () => {
      await globalThis.host.close();
      globalThis.host.dispose();
    });

    await openHost(page, { databaseName, ...IDB });
    const tables = await query(
      page,
      "SELECT name FROM sqlite_master WHERE type='table' AND name='product'");
    expect(tables.rows).toEqual([]);
  });
});
