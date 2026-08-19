import { expect, test } from '@playwright/test';

/**
 * A Blazor application is routinely published under a sub-path - GitHub Pages, an IIS virtual
 * directory - where `<base href="/app/">` puts every static asset under `/app/_content/...`. The .NET
 * providers therefore name their modules relative to the document, and the host resolves those names
 * against the document base before the worker imports them. This page lives under /nested/ and serves
 * the library from /nested/_content/, so a root-relative URL anywhere in the chain - the VFS module,
 * the engine's base classes the vendored VFS imports, the worker itself - fails here.
 */
test.describe('hosting under a sub-path', () => {
  const providers = [
    { name: 'opfs', vfsName: 'opfs-coop-sync', requiredBuild: 'synchronous', moduleUrl: './_content/BlazorSqlite.Storage.Opfs/opfs-vfs.js' },
    { name: 'indexeddb', vfsName: 'idb-batch-atomic', requiredBuild: 'asyncCapable', moduleUrl: './_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js' },
    { name: 'cache-storage', vfsName: 'cache-storage', requiredBuild: 'asyncCapable', moduleUrl: './_content/BlazorSqlite.Storage.CacheStorage/cache-register.js' },
  ];

  for (const provider of providers) {
    test(`${provider.name} opens through a document-relative module URL`, async ({ page }) => {
      await page.goto('/nested/index.html');
      await page.waitForFunction(() => globalThis.BlazorSqliteReady === true);

      const result = await page.evaluate(async ({ requiredBuild, moduleUrl }) => {
        const host = globalThis.BlazorSqlite.host.createHost();
        try {
          const opened = await host.call({
            kind: 'open',
            databaseName: `sub-path-${Date.now()}.db`,
            requiredBuild,
            vfs: { moduleUrl, registerExport: 'register' },
          });

          const queried = await host.call({
            kind: 'execute',
            batch: [{ commandText: 'SELECT 6 * 7', resultKind: 'reader', parameters: [] }],
          });

          return { opened, queried };
        } finally {
          host.dispose();
        }
      }, provider);

      expect(result.opened).toEqual({
        ok: true,
        result: { build: expect.any(String), vfsName: provider.vfsName, reused: false },
      });
      expect(result.queried.ok).toBe(true);
      expect(result.queried.result[0].rows[0].v).toEqual([42]);
    });
  }
});
