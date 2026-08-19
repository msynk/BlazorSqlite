import { expect, test } from '@playwright/test';
import { exec, openHost, query } from './host.js';

const CACHE_VFS = {
  moduleUrl: '/_content/BlazorSqlite.Storage.CacheStorage/cache-register.js',
  registerExport: 'register',
};

const CACHE_LIMITS = {
  supportsMultiDatabaseTransactions: true,
  canChangePageSize: false,
};

test.describe('Cache Storage persistence', () => {
  test('survives a worker restart', async ({ page }) => {
    const databaseName = uniqueName('persist');
    await openCache(page, databaseName);
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('Kept')");
    await page.evaluate(async () => {
      await globalThis.host.close();
      globalThis.host.dispose();
    });

    await openCache(page, databaseName);
    expect((await query(page, 'SELECT name FROM product')).rows).toEqual([['Kept']]);
  });

  test('an uncommitted write does not survive a killed worker', async ({ page }) => {
    const databaseName = uniqueName('crash');
    await openCache(page, databaseName);
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('Committed')");
    await exec(page, 'BEGIN');
    await exec(page, "INSERT INTO product (name) VALUES ('Ghost')");
    await page.evaluate(() => globalThis.host.dispose());

    await openCache(page, databaseName);
    expect((await query(page, 'SELECT name FROM product ORDER BY id')).rows)
      .toEqual([['Committed']]);
  });

  test('opens a database that besql left in bit-Besql', async ({ page }) => {
    const databaseName = uniqueName('besql');
    await openCache(page, databaseName);
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('FromBesql')");
    await page.evaluate(async () => {
      await globalThis.host.close();
      globalThis.host.dispose();
    });

    await page.evaluate(async name => {
      const admin = await import('/_content/BlazorSqlite.Storage.CacheStorage/cache-admin.js');
      const image = await admin.exportDatabase(name);
      await admin.deleteDatabase(name);
      const besql = await caches.open('bit-Besql');
      await besql.put(`/data/cache/${name}`, new Response(image));
    }, databaseName);

    await openCache(page, databaseName);
    expect((await query(page, 'SELECT name FROM product')).rows).toEqual([['FromBesql']]);
  });
});

test.describe('Cache Storage admin', () => {
  test('import, export, list, and delete', async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

    const name = uniqueName('admin');
    const image = Array.from({ length: 64 }, (_, i) => (i * 7) % 251);
    image.splice(0, 16, ...[...('SQLite format 3\0')].map(c => c.charCodeAt(0)));

    const outcome = await page.evaluate(async ({ name, image }) => {
      const admin = await import('/_content/BlazorSqlite.Storage.CacheStorage/cache-admin.js');
      await admin.importDatabase(name, new Uint8Array(image));
      const exported = Array.from(await admin.exportDatabase(name));
      const listed = await admin.list();
      const existed = await admin.exists(name);
      await admin.deleteDatabase(name);
      return {
        exported,
        listed: listed.includes(name),
        existed,
        existsAfterDelete: await admin.exists(name),
      };
    }, { name, image });

    expect(outcome.exported).toEqual(image);
    expect(outcome.listed).toBe(true);
    expect(outcome.existed).toBe(true);
    expect(outcome.existsAfterDelete).toBe(false);
  });
});

test.describe('Cache Storage multi-tab', () => {
  test('a second tab sees a committed write', async ({ browser }) => {
    const databaseName = uniqueName('tabs');
    const context = await browser.newContext();
    const first = await context.newPage();
    const second = await context.newPage();

    try {
      await openCache(first, databaseName);
      await exec(first, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
      await exec(first, "INSERT INTO product (name) VALUES ('Shared')");

      await openCache(second, databaseName);
      expect((await query(second, 'SELECT name FROM product')).rows).toEqual([['Shared']]);
    } finally {
      await context.close();
    }
  });

  /**
   * The harder order: the reader opened while the file was still empty. Its VFS cached a size of
   * zero, and every read transaction since has to notice that the other tab grew the file.
   */
  test('a tab that opened first sees a write that grew the file', async ({ browser }) => {
    const databaseName = uniqueName('grow');
    const context = await browser.newContext();
    const reader = await context.newPage();
    const writer = await context.newPage();

    try {
      await openCache(reader, databaseName);
      expect((await query(reader, "SELECT name FROM sqlite_master WHERE type = 'table'")).rows).toEqual([]);

      await openCache(writer, databaseName);
      await exec(writer, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
      await exec(writer, "INSERT INTO product (name) VALUES ('Grown')");

      expect((await query(reader, 'SELECT name FROM product')).rows).toEqual([['Grown']]);
    } finally {
      await context.close();
    }
  });
});

function uniqueName(label) {
  return `cache-${label}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function openCache(page, databaseName) {
  return openHost(page, {
    databaseName,
    requiredBuild: 'asyncCapable',
    vfs: CACHE_VFS,
    limits: CACHE_LIMITS,
  });
}
