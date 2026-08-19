import { expect, test } from '@playwright/test';
import { exec, executeExpectingFailure, openHost, query } from './host.js';

const OPFS_VFS = {
  moduleUrl: '/_content/BlazorSqlite.Storage.Opfs/opfs-vfs.js',
  registerExport: 'register',
};

const OPFS_LIMITS = {
  supportsMultiDatabaseTransactions: false,
  canChangePageSize: true,
};

test.describe('OPFS persistence', () => {
  test('survives a worker restart', async ({ page }) => {
    const databaseName = uniqueName('persist');
    await openOpfs(page, databaseName);
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('Kept')");

    await page.evaluate(async () => {
      await globalThis.host.close();
      globalThis.host.dispose();
    });

    await openOpfs(page, databaseName);
    expect((await query(page, 'SELECT name FROM product')).rows).toEqual([['Kept']]);
  });

  test('an uncommitted write does not survive a killed worker', async ({ page }) => {
    const databaseName = uniqueName('crash');
    await openOpfs(page, databaseName);
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(page, "INSERT INTO product (name) VALUES ('Committed')");
    await exec(page, 'BEGIN');
    await exec(page, "INSERT INTO product (name) VALUES ('Ghost')");

    await page.evaluate(() => globalThis.host.dispose());

    await openOpfs(page, databaseName);
    expect((await query(page, 'SELECT name FROM product ORDER BY id')).rows)
      .toEqual([['Committed']]);
  });

  test('two workers can open the same database', async ({ page }) => {
    const databaseName = uniqueName('shared');
    await openOpfs(page, databaseName);
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
    }, { databaseName, requiredBuild: 'synchronous', vfs: OPFS_VFS, limits: OPFS_LIMITS });

    expect(second).toEqual([['FromFirst']]);
  });

  test('ATTACH is rejected rather than corrupting silently', async ({ page }) => {
    await openOpfs(page, uniqueName('attach'));

    const error = await executeExpectingFailure(page, [{
      commandText: "ATTACH 'other.db' AS other",
      resultKind: 'nonQuery',
    }]);

    expect(error).not.toBeNull();
    expect(error.message).toMatch(/ATTACH/i);
    expect(error.message).toMatch(/transaction/i);
  });
});

test.describe('OPFS admin', () => {
  test('import, export, list, and delete', async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

    const name = uniqueName('admin');
    const image = Array.from({ length: 64 }, (_, i) => (i * 7) % 251);

    const outcome = await page.evaluate(async ({ name, image }) => {
      const admin = await import('/_content/BlazorSqlite.Storage.Opfs/opfs-admin.js');
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

  test('probe reports available in a secure Chrome context', async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

    const probe = await page.evaluate(async () => {
      const admin = await import('/_content/BlazorSqlite.Storage.Opfs/opfs-admin.js');
      return await admin.probe();
    });

    expect(probe.available).toBe(true);
    expect(probe.environment.hasGetDirectory).toBe('true');
    expect(probe.environment.secureContext).toBe('true');
  });
});

test.describe('OPFS multi-tab', () => {
  test('a second tab sees a committed write', async ({ browser }) => {
    const databaseName = uniqueName('tabs');
    const context = await browser.newContext();
    const first = await context.newPage();
    const second = await context.newPage();

    try {
      await openOpfs(first, databaseName);
      await exec(first, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
      await exec(first, "INSERT INTO product (name) VALUES ('Shared')");

      await openOpfs(second, databaseName);
      expect((await query(second, 'SELECT name FROM product')).rows).toEqual([['Shared']]);
    } finally {
      await context.close();
    }
  });
});

/**
 * The harder order: the reader opened while the file was still empty and has to notice, at each
 * read transaction, that the other tab grew it.
 */
test('a tab that opened first sees a write that grew the file', async ({ browser }) => {
  const databaseName = uniqueName('grow');
  const context = await browser.newContext();
  const reader = await context.newPage();
  const writer = await context.newPage();

  try {
    await openOpfs(reader, databaseName);
    expect((await query(reader, "SELECT name FROM sqlite_master WHERE type = 'table'")).rows).toEqual([]);

    await openOpfs(writer, databaseName);
    await exec(writer, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
    await exec(writer, "INSERT INTO product (name) VALUES ('Grown')");

    expect((await query(reader, 'SELECT name FROM product')).rows).toEqual([['Grown']]);
  } finally {
    await context.close();
  }
});

function uniqueName(label) {
  return `opfs-${label}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function openOpfs(page, databaseName) {
  return openHost(page, {
    databaseName,
    requiredBuild: 'synchronous',
    vfs: OPFS_VFS,
    limits: OPFS_LIMITS,
  });
}
