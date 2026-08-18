// Soak coverage: many tabs writing at once, and a worker killed mid-transaction a thousand times over.
//
// Tagged @soak and excluded from an ordinary run by playwright.config.js, because both tests are minutes
// of wall clock rather than seconds. Run them with `npm run soak`.
//
// The loops live inside page.evaluate rather than being driven a statement at a time from here: a
// thousand round trips through the Playwright protocol would measure the protocol, and tabs told to
// write "at the same time" from Node would in fact take turns.
//
// Sizes are environment-tunable so a smaller run can be used while investigating a failure:
//   BLAZORSQLITE_SOAK_TABS    tabs in the concurrent-write test        (default 8)
//   BLAZORSQLITE_SOAK_ROWS    rows each of those tabs commits          (default 50)
//   BLAZORSQLITE_SOAK_KILLS   mid-transaction kills per storage        (default 1000)

import { expect, test } from '@playwright/test';
import { exec, openHost, query } from './host.js';

const STORAGES = [
  {
    label: 'OPFS',
    prefix: 'opfs',
    options: {
      requiredBuild: 'synchronous',
      vfs: {
        moduleUrl: '/_content/BlazorSqlite.Storage.Opfs/opfs-vfs.js',
        registerExport: 'register',
      },
      limits: { supportsMultiDatabaseTransactions: false, canChangePageSize: true },
    },
  },
  {
    label: 'IndexedDB',
    prefix: 'idb',
    options: {
      requiredBuild: 'asyncCapable',
      vfs: {
        moduleUrl: '/_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js',
        registerExport: 'register',
      },
      limits: { supportsMultiDatabaseTransactions: true, canChangePageSize: false },
    },
  },
];

// Both ends of the range in the reference targets, deduplicated in case they were tuned to meet.
const tabCounts = [...new Set([4, size('BLAZORSQLITE_SOAK_TABS', 8)])];
const rowsPerTab = size('BLAZORSQLITE_SOAK_ROWS', 50);
const kills = size('BLAZORSQLITE_SOAK_KILLS', 1000);

// Killed workers are reported to Node in batches so a long run shows progress instead of going silent.
const KILL_BATCH = 25;

// One soak test already saturates a machine's storage layer; running two at once would turn a genuine
// contention failure into a scheduling artefact.
test.describe.configure({ mode: 'serial' });

for (const storage of STORAGES) {
  test.describe(`@soak ${storage.label}`, () => {
    test(`survives ${kills} kills mid-transaction`, async ({ page }) => {
      test.setTimeout(kills * 2_000 + 120_000);

      const databaseName = uniqueName(storage.prefix, 'kill');
      await prepare(page, databaseName, storage.options);

      let committed = 0;
      let openRetries = 0;

      for (let done = 0; done < kills; done += KILL_BATCH) {
        const batch = Math.min(KILL_BATCH, kills - done);
        const outcome = await killBatch(page, {
          databaseName,
          options: storage.options,
          iterations: batch,
          startIndex: done,
        });

        committed += outcome.committed;
        openRetries += outcome.openRetries;
        test.info().annotations.push({
          type: 'progress',
          description: `${done + batch}/${kills} kills, ${openRetries} open retries`,
        });
      }

      expect(committed).toBe(kills);

      await openHost(page, { databaseName, ...storage.options });

      const kept = await query(page, "SELECT count(*) FROM ledger WHERE label LIKE 'kept-%'");
      const ghosts = await query(page, "SELECT count(*) FROM ledger WHERE label LIKE 'ghost-%'");
      const distinct = await query(page, 'SELECT count(DISTINCT label) FROM ledger');
      const integrity = await query(page, 'PRAGMA integrity_check');

      // Every committed row is there exactly once, no row from an unfinished transaction survived, and
      // the file the kills left behind is still a valid SQLite database.
      expect(kept.rows).toEqual([[kills]]);
      expect(ghosts.rows).toEqual([[0]]);
      expect(distinct.rows).toEqual([[kills]]);
      expect(integrity.rows).toEqual([['ok']]);
    });

    for (const tabs of tabCounts) {
      test(`loses no write across ${tabs} tabs`, async ({ browser }) => {
        test.setTimeout(tabs * rowsPerTab * 1_000 + 120_000);

        const databaseName = uniqueName(storage.prefix, `tabs-${tabs}`);
        const context = await browser.newContext();

        try {
          const pages = [];
          for (let index = 0; index < tabs; index++) {
            pages.push(await context.newPage());
          }

          await prepare(pages[0], databaseName, storage.options);

          const outcomes = await Promise.all(pages.map((tab, index) => writeBatch(tab, {
            databaseName,
            options: storage.options,
            tab: index,
            rows: rowsPerTab,
          })));

          expect(outcomes.map(outcome => outcome.written)).toEqual(pages.map(() => rowsPerTab));
          test.info().annotations.push({
            type: 'progress',
            description: `${outcomes.reduce((total, o) => total + o.retries, 0)} write retries`,
          });

          const reader = pages[0];
          await openHost(reader, { databaseName, ...storage.options });

          const total = await query(reader, 'SELECT count(*) FROM ledger');
          const perTab = await query(
            reader,
            'SELECT tab, count(*) FROM ledger GROUP BY tab ORDER BY tab');
          const integrity = await query(reader, 'PRAGMA integrity_check');

          // A lost write shows up as a short count; a write applied twice shows up as a long one. Both
          // are visible per tab, so a failure names the tab that lost the race.
          expect(total.rows).toEqual([[tabs * rowsPerTab]]);
          expect(perTab.rows).toEqual(
            Array.from({ length: tabs }, (_, index) => [index, rowsPerTab]));
          expect(integrity.rows).toEqual([['ok']]);
        } finally {
          await context.close();
        }
      });
    }
  });
}

function uniqueName(prefix, label) {
  return `${prefix}-soak-${label}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

/** Creates the table the soak tests write to, then closes cleanly so the loops start from a good file. */
async function prepare(page, databaseName, options) {
  await openHost(page, { databaseName, ...options });
  await exec(
    page,
    'CREATE TABLE ledger (id INTEGER PRIMARY KEY, tab INTEGER NOT NULL, label TEXT NOT NULL)');
  await page.evaluate(async () => {
    await globalThis.host.close();
    globalThis.host.dispose();
  });
}

/**
 * Commits one row, opens a transaction, writes a row into it, then kills the worker without committing
 * or closing - the closest a test can get to the tab being killed by the browser or the user.
 */
function killBatch(page, { databaseName, options, iterations, startIndex }) {
  return page.evaluate(async ({ databaseName, options, iterations, startIndex }) => {
    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
    const outcome = { committed: 0, openRetries: 0 };

    // A killed worker releases its lock on the file asynchronously, so the next open can arrive while
    // the previous one is still letting go. That is a retry, not a failure.
    const openWithRetry = async () => {
      let lastError;
      for (let attempt = 0; attempt < 60; attempt++) {
        try {
          return await globalThis.BlazorSqlite.host.open({ databaseName, ...options });
        } catch (error) {
          lastError = error;
          outcome.openRetries++;
          await sleep(25 + attempt * 5);
        }
      }

      throw lastError;
    };

    const run = (host, commandText) =>
      host.execute([{ commandText, resultKind: 'nonQuery' }]);

    for (let index = 0; index < iterations; index++) {
      const iteration = startIndex + index;
      const host = await openWithRetry();

      try {
        await run(host, `INSERT INTO ledger (tab, label) VALUES (0, 'kept-${iteration}')`);
        outcome.committed++;

        await run(host, 'BEGIN');
        await run(host, `INSERT INTO ledger (tab, label) VALUES (0, 'ghost-${iteration}')`);
      } finally {
        host.dispose();
      }
    }

    return outcome;
  }, { databaseName, options, iterations, startIndex });
}

/**
 * Commits `rows` rows from one tab, retrying the statements another tab is currently holding the
 * database against. Contention is expected here; losing a row to it is not.
 */
function writeBatch(page, { databaseName, options, tab, rows }) {
  return page.goto('/index.html')
    .then(() => page.waitForFunction(() => globalThis.BlazorSqliteReady === true))
    .then(() => page.evaluate(async ({ databaseName, options, tab, rows }) => {
      const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
      const outcome = { written: 0, retries: 0 };

      const withRetry = async action => {
        let lastError;
        for (let attempt = 0; attempt < 60; attempt++) {
          try {
            return await action();
          } catch (error) {
            lastError = error;
            outcome.retries++;
            await sleep(25 + attempt * 5);
          }
        }

        throw lastError;
      };

      const host = await withRetry(() => globalThis.BlazorSqlite.host.open({ databaseName, ...options }));

      try {
        for (let row = 0; row < rows; row++) {
          await withRetry(() => host.execute([{
            commandText: `INSERT INTO ledger (tab, label) VALUES (${tab}, 'tab-${tab}-row-${row}')`,
            resultKind: 'nonQuery',
          }]));
          outcome.written++;
        }
      } finally {
        await host.close();
        host.dispose();
      }

      return outcome;
    }, { databaseName, options, tab, rows }));
}

function size(variable, fallback) {
  const raw = Number(process.env[variable]);
  return Number.isInteger(raw) && raw > 0 ? raw : fallback;
}
