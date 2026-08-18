import { expect, test } from '@playwright/test';
import { openHost } from './host.js';

/**
 * EF Core gates SQL translations on the engine's version, and it reads that version from
 * `BrowserSqlitePclProvider.sqlite3_libversion()` - a stub, because WASM has no e_sqlite3 to ask.
 * A stub that claims more than the vendored engine delivers is how EF ends up emitting SQL the
 * engine cannot run, at query time, in the browser only.
 */
test('the engine reports the version the .NET stub claims', async ({ page }) => {
  const reported = await openHost(page);

  // Keep in step with BrowserSqlitePclProvider.EngineVersion.
  expect(reported.version).toBe('3.53.0');
});
