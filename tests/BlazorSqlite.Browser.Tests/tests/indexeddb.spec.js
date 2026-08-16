import { expect, test } from '@playwright/test';
import { exec, executeExpectingFailure, openHost, query } from './host.js';

const IDB_VFS = {
  moduleUrl: '/_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js',
  registerExport: 'register',
};

const IDB_LIMITS = {
  supportsMultiDatabaseTransactions: true,
  canChangePageSize: false,
};

test.describe('IndexedDB persistence', () => {
  test('opens on an async-capable build, not the synchronous one', async ({ page }) => {
    const { build } = await openIdb(page, uniqueName('build'));
    expect(['jspi', 'asyncify']).toContain(build);
  });

  test('survives a worker restart', async ({ page }) => {
    const databaseName = uniqueName('persist');
    await openIdb(page, databaseName);
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('Kept')");

    await page.evaluate(async () => {
      await globalThis.host.close();
      globalThis.host.dispose();
    });

    await openIdb(page, databaseName);
    expect((await query(page, 'SELECT name FROM product')).rows).toEqual([['Kept']]);
  });

  test('an uncommitted write does not survive a killed worker', async ({ page }) => {
    const databaseName = uniqueName('crash');
    await openIdb(page, databaseName);
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('Committed')");
    await exec(page, 'BEGIN');
    await exec(page, "INSERT INTO product (name) VALUES ('Ghost')");

    await page.evaluate(() => globalThis.host.dispose());

    await openIdb(page, databaseName);
    expect((await query(page, 'SELECT name FROM product ORDER BY id')).rows)
      .toEqual([['Committed']]);
  });

  test('two workers can open the same database', async ({ page }) => {
    const databaseName = uniqueName('shared');
    await openIdb(page, databaseName);
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('FromFirst')");

    const second = await page.evaluate(async options => {
      const { decodeValue } = globalThis.BlazorSqlite.host;
      const host = await globalThis.BlazorSqlite.host.open(options);
      try {
        const result = await host.execute([{
          commandText: 'SELECT name FROM product',
          resultKind: 'reader',
        }]);
        return result[0].rows.map(row =>
          row.v.map((value, i) => decodeValue(row.t[i], value)));
      } finally {
        host.dispose();
      }
    }, { databaseName, requiredBuild: 'asyncCapable', vfs: IDB_VFS, limits: IDB_LIMITS });

    expect(second).toEqual([['FromFirst']]);
  });

  test('PRAGMA page_size assignment is rejected', async ({ page }) => {
    await openIdb(page, uniqueName('pagesize'));

    const error = await executeExpectingFailure(page, [{
      commandText: 'PRAGMA page_size=8192',
      resultKind: 'nonQuery',
    }]);

    expect(error).not.toBeNull();
    expect(error.message).toMatch(/page_size/i);
    expect(error.message).toMatch(/fixed/i);
  });

  test('the default page size is 4096', async ({ page }) => {
    await openIdb(page, uniqueName('defaultpage'));
    expect((await query(page, 'PRAGMA page_size')).rows).toEqual([[4096]]);
  });

  test('cache_size is raised so batch-atomic writes can keep the journal in cache', async ({ page }) => {
    await openIdb(page, uniqueName('cache'));
    const [[cacheSize]] = (await query(page, 'PRAGMA cache_size')).rows;
    expect(cacheSize).toBe(-8000);
  });

  test('the synchronous build cannot host this VFS', async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

    const error = await page.evaluate(async options => {
      try {
        await globalThis.BlazorSqlite.host.open(options);
        return null;
      } catch (e) {
        return e.message;
      }
    }, {
      databaseName: uniqueName('sync'),
      requiredBuild: 'synchronous',
      vfs: IDB_VFS,
    });

    expect(error).not.toBeNull();
  });
});

test.describe('IndexedDB admin', () => {
  test('import, export, list, and delete', async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

    const name = uniqueName('admin');
    const image = Array.from({ length: 64 }, (_, i) => (i * 7) % 251);

    const outcome = await page.evaluate(async ({ name, image }) => {
      const admin = await import('/_content/BlazorSqlite.Storage.IndexedDb/idb-admin.js');
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

  test('export of an engine-created database is a SQLite file image', async ({ page }) => {
    const databaseName = uniqueName('export');
    await openIdb(page, databaseName);
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('Migrated')");
    await page.evaluate(async () => {
      await globalThis.host.close();
      globalThis.host.dispose();
    });

    const header = await page.evaluate(async name => {
      const admin = await import('/_content/BlazorSqlite.Storage.IndexedDb/idb-admin.js');
      const bytes = await admin.exportDatabase(name);
      return String.fromCharCode(...bytes.subarray(0, 15));
    }, databaseName);

    expect(header).toBe('SQLite format 3');
  });

  test('probe reports available when indexedDB exists', async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

    const probe = await page.evaluate(async () => {
      const admin = await import('/_content/BlazorSqlite.Storage.IndexedDb/idb-admin.js');
      return await admin.probe();
    });

    expect(probe.available).toBe(true);
    expect(probe.environment.hasIndexedDB).toBe('true');
  });
});

test.describe('IndexedDB multi-tab', () => {
  test('a second tab sees a committed write', async ({ browser }) => {
    const databaseName = uniqueName('tabs');
    const context = await browser.newContext();
    const first = await context.newPage();
    const second = await context.newPage();

    try {
      await openIdb(first, databaseName);
      await exec(first, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
      await exec(first, "INSERT INTO product (name) VALUES ('Shared')");

      await openIdb(second, databaseName);
      expect((await query(second, 'SELECT name FROM product')).rows).toEqual([['Shared']]);
    } finally {
      await context.close();
    }
  });
});

function uniqueName(label) {
  return `idb-${label}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function openIdb(page, databaseName) {
  return openHost(page, {
    databaseName,
    requiredBuild: 'asyncCapable',
    vfs: IDB_VFS,
    limits: IDB_LIMITS,
  });
}
