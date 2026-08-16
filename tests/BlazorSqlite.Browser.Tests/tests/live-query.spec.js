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
