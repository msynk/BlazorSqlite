import { expect, test } from '@playwright/test';
import { openHost, query } from './host.js';

test.describe('engine loading and build negotiation', () => {
  test('loads the synchronous build and reports the pinned SQLite version', async ({ page }) => {
    const { version, build } = await openHost(page);

    expect(build).toBe('synchronous');

    // Pinned through the vendored artifacts, so a silent engine change fails here rather than showing
    // up as different SQL behaviour much later.
    expect(version).toBe('3.53.0');
  });

  test('picks an async-capable build only when the VFS needs one', async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

    const selection = await page.evaluate(() => {
      const { selectBuild, supportsJspi } = globalThis.BlazorSqlite.engine;
      return {
        jspi: supportsJspi(),
        synchronous: selectBuild('synchronous').id,
        asyncCapable: selectBuild('asyncCapable').id,
      };
    });

    expect(selection.synchronous).toBe('synchronous');

    // The point of vendoring three builds: whichever async build this browser gets, it must be the
    // cheaper one when the browser can take it.
    expect(selection.asyncCapable).toBe(selection.jspi ? 'jspi' : 'asyncify');
  });

  /**
   * Loading the async build is not hypothetical work: it is what the IndexedDB and Cache Storage
   * providers will run on, and a build that cannot execute a plain query is worth discovering now
   * rather than behind a VFS.
   */
  test('the async-capable build loads and executes', async ({ page }) => {
    const { version, build } = await openHost(page, {
      databaseName: 'async.db',
      requiredBuild: 'asyncCapable',
    });

    expect(['jspi', 'asyncify']).toContain(build);
    expect(version).toBe('3.53.0');

    expect((await query(page, 'SELECT 6 * 7')).rows).toEqual([[42]]);
  });

  test('rejects an unknown build requirement instead of guessing', async ({ page }) => {
    await page.goto('/index.html');
    await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

    const error = await page.evaluate(() => {
      try {
        globalThis.BlazorSqlite.engine.selectBuild('whatever');
        return null;
      } catch (e) {
        return e.message;
      }
    });

    expect(error).toContain('whatever');
  });

  test('a worker owns one database and says so when asked for another', async ({ page }) => {
    await openHost(page, { databaseName: 'first.db' });

    const error = await page.evaluate(async () => {
      try {
        await globalThis.host.send({
          kind: 'open',
          databaseName: 'second.db',
          requiredBuild: 'synchronous',
        });
        return null;
      } catch (e) {
        return e.message;
      }
    });

    expect(error).toContain('first.db');
    expect(error).toContain('own worker');
  });

  test('reopening the same database reuses the session', async ({ page }) => {
    await openHost(page, { databaseName: 'same.db' });

    const result = await page.evaluate(() => globalThis.host.send({
      kind: 'open',
      databaseName: 'same.db',
      requiredBuild: 'synchronous',
    }));

    expect(result.reused).toBe(true);
  });
});
