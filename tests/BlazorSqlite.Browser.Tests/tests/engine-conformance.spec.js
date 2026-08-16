import { expect, test } from '@playwright/test';
import { exec, executeExpectingFailure, openHost, query } from './host.js';

/**
 * The engine-layer rules, run against the worker's built-in memory VFS - the InMemory provider's
 * actual engine path. The C# kit runs the same rules against the in-process transport; a divergence
 * between the two is a bug in one of them.
 */
test.describe('engine conformance (in-memory VFS)', () => {
  test.beforeEach(async ({ page }) => {
    await openHost(page, { databaseName: 'conformance.db' });
    await exec(page, 'CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)');
  });

  test('a rolled-back write is invisible', async ({ page }) => {
    await exec(page, "BEGIN; INSERT INTO product (name) VALUES ('Ghost'); ROLLBACK;");

    expect((await query(page, 'SELECT COUNT(*) FROM product')).rows).toEqual([[0]]);
  });

  test('a committed write is visible', async ({ page }) => {
    await exec(page, "BEGIN; INSERT INTO product (name) VALUES ('Kept'); COMMIT;");

    expect((await query(page, 'SELECT name FROM product')).rows).toEqual([['Kept']]);
  });

  test('journal_mode=WAL is rejected rather than silently ignored', async ({ page }) => {
    const error = await executeExpectingFailure(page, [{
      commandText: 'PRAGMA journal_mode=WAL',
      resultKind: 'nonQuery',
    }]);

    expect(error).not.toBeNull();
    expect(error.message).toMatch(/WAL/i);
    expect(error.message).toMatch(/shared-memory/i);
  });
});
