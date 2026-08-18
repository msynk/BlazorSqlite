// The dedicated worker that owns one SQLite database.
//
// It exists because the storage APIs worth using are worker-only: OPFS synchronous access handles do
// not exist on the main thread. Everything here is request/response over postMessage, correlated by
// id, and strictly serialized - one SQLite connection cannot execute two statements at once, so
// overlapping requests queue rather than interleave.

import { loadEngine } from './blazor-sqlite-engine.js';
import { registerFunctions, resetAggregateState } from './blazor-sqlite-functions.js';
import { decodeParameter, encodeRow } from './blazor-sqlite-wire.js';
import * as SQLite from './engine/sqlite-api.js';

/** @type {{sqlite3: any, db: number, build: string, databaseName: string, limits: {supportsMultiDatabaseTransactions: boolean, canChangePageSize: boolean}} | null} */
let session = null;

// Requests are chained so that each runs to completion before the next begins. Without this a second
// execute could start stepping while the first is suspended awaiting storage.
let queue = Promise.resolve();

self.addEventListener('message', event => {
  const request = event.data;
  const port = event.ports?.[0] ?? self;

  const run = async () => {
    try {
      const result = await dispatch(request);
      port.postMessage({ id: request.id, ok: true, result });
    } catch (error) {
      port.postMessage({ id: request.id, ok: false, error: describe(error) });
    }
  };

  // The chain has to survive its own failure. `then(run)` alone would skip every later request
  // once one link rejected - and postMessage can still throw after the catch above, on a result
  // the structured clone algorithm refuses. A wedged worker answers nothing, ever again.
  queue = queue.then(run, run).catch(() => {});
});

/**
 * Errors cross the boundary as data, so the .NET side can raise a typed exception carrying the SQLite
 * result code rather than a flattened string.
 */
function describe(error) {
  return {
    message: error?.message ?? String(error),
    // wa-sqlite raises SQLiteError with the primary result code; anything else has no code.
    sqliteCode: typeof error?.code === 'number' ? error.code : null,
    name: error?.name ?? 'Error',
  };
}

async function dispatch(request) {
  switch (request.kind) {
    case 'open':
      return await open(request);

    case 'execute':
      return await execute(request.batch ?? []);

    case 'close':
      return close();

    case 'version':
      return { version: requireSession().sqlite3.libversion(), build: session.build };

    default:
      throw new Error(`Unknown request kind '${request.kind}'.`);
  }
}

function requireSession() {
  if (!session) {
    throw new Error('No database is open in this worker.');
  }

  return session;
}

async function open({ databaseName, requiredBuild, vfs, limits }) {
  if (session) {
    if (session.databaseName !== databaseName) {
      throw new Error(
        `This worker already owns '${session.databaseName}'. A worker hosts one database, so `
        + `opening '${databaseName}' needs its own worker.`);
    }

    return { build: session.build, reused: true };
  }

  const engine = await loadEngine({ requiredBuild, databaseName, vfs });

  // The VFS name matters: passing it explicitly means a provider that registers without claiming the
  // default still gets used, and a bug in registration surfaces as "no such vfs" here rather than as
  // a database silently opened on the wrong storage.
  const db = await engine.sqlite3.open_v2(
    databaseName,
    SQLite.SQLITE_OPEN_CREATE | SQLite.SQLITE_OPEN_READWRITE,
    engine.vfsName ?? undefined);

  // EF Core's SQLite provider only installs these on a real SqliteConnection. We are not one, so
  // every open has to - miss it and every decimal comparison, aggregate, and REGEXP is wrong.
  registerFunctions(engine.module, engine.sqlite3, db);

  // SQLite defaults foreign keys OFF, and Microsoft.Data.Sqlite turns them ON for every connection
  // it opens. We are not one, so without this the same model enforces its relationships on the
  // server and silently does not in the browser: an orphaned row inserts cleanly here and is
  // rejected there, and ON DELETE CASCADE never fires. Product-wide rather than a provider pragma,
  // and still overridable - EF's own table-rebuild migrations toggle it off and back on.
  const onOpenSql = ['PRAGMA foreign_keys=ON', ...(engine.onOpenSql ?? [])];

  // Run here rather than through execute() so a guard meant for application SQL cannot block them.
  for (const sql of onOpenSql) {
    for await (const stmt of engine.sqlite3.statements(db, sql)) {
      while (await engine.sqlite3.step(stmt) === SQLite.SQLITE_ROW) {
        // Drained: these statements are configuration, not queries.
      }
    }
  }

  session = {
    sqlite3: engine.sqlite3,
    db,
    build: engine.build,
    databaseName,
    limits: {
      supportsMultiDatabaseTransactions: limits?.supportsMultiDatabaseTransactions !== false,
      canChangePageSize: limits?.canChangePageSize !== false,
    },
  };

  return { build: engine.build, vfsName: engine.vfsName, reused: false };
}

function close() {
  if (!session) {
    return { closed: false };
  }

  const { sqlite3, db } = session;
  session = null;
  sqlite3.close(db);
  return { closed: true };
}

/**
 * Runs a batch in one round trip, returning one result per request in order.
 *
 * Batching is the whole point of the coarse transport contract: in the browser the cost that dominates
 * is crossing this boundary, not executing the SQL.
 */
async function execute(batch) {
  const { sqlite3, db } = requireSession();
  const results = [];

  for (const request of batch) {
    results.push(await executeOne(sqlite3, db, request));
  }

  notifyIfWrite(batch);
  return results;
}

async function executeOne(sqlite3, db, request) {
  rejectUnsupportedSql(request.commandText);

  const wantsRows = request.resultKind !== 'nonQuery';

  let columnNames = [];
  let columnTypes = [];
  let rows = [];
  let recordsAffected = 0;
  let captured = false;

  try {
    for await (const stmt of sqlite3.statements(db, request.commandText)) {
      bind(sqlite3, stmt, request.parameters ?? []);

      const producesRows = sqlite3.column_count(stmt) > 0;

      // Rows are taken from the first statement that produces any, and later statements still run.
      // This is what EF's insert-then-select pattern needs: the INSERT reports its changes and the
      // following SELECT is the result set the reader consumes.
      if (wantsRows && producesRows && !captured) {
        captured = true;
        columnNames = sqlite3.column_names(stmt);
        ({ rows, columnTypes } = await readRows(sqlite3, stmt, columnNames.length, request.resultKind));
      } else {
        while (await sqlite3.step(stmt) === SQLite.SQLITE_ROW) {
          // Drained deliberately: a statement whose rows nobody asked for still has to run.
        }
      }

      recordsAffected += sqlite3.changes(db);
    }
  } finally {
    // Aggregate state is keyed by an address SQLite frees with the statement and reuses, so it is
    // dropped at the statement boundary rather than left to the final callbacks to clean up.
    resetAggregateState();
  }

  return { columnNames, columnTypes, rows, recordsAffected };
}

function notifyIfWrite(batch) {
  const tables = new Set();
  for (const request of batch) {
    if (!looksLikeWrite(request.commandText)) {
      continue;
    }

    for (const name of extractTables(request.commandText)) {
      tables.add(name);
    }
  }

  if (tables.size === 0 || !session) {
    return;
  }

  self.postMessage({
    kind: 'notify',
    databaseName: session.databaseName,
    tables: [...tables],
  });
}

// Every statement, not just the first: a batch is routinely `BEGIN; INSERT …; COMMIT;`, and looking
// only at the leading keyword would call that a read. Mirrors SqliteTableNames.LooksLikeWrite.
function looksLikeWrite(sql) {
  return typeof sql === 'string' && /(?:^|;)\s*(?:insert|update|delete|replace|create|drop|alter)\b/i.test(sql);
}

function extractTables(sql) {
  const names = [];
  const pattern = /\b(?:from|join|into|update|table)\s+(?:if\s+(?:not\s+)?exists\s+)?["`]?([A-Za-z_][\w]*)/gi;
  let match;
  while ((match = pattern.exec(sql))) {
    if (!match[1].toLowerCase().startsWith('sqlite_')) {
      names.push(match[1]);
    }
  }

  return names;
}

/**
 * Binds by asking the statement what it wants, rather than handing the engine a dictionary and hoping
 * the keys line up.
 *
 * The alternative - `bind_collection` with a name-keyed object - leaves an unmatched parameter bound to
 * NULL, which turns a misspelled parameter name into wrong results instead of an error. Names are
 * matched with or without their sigil, because ADO.NET callers write `p0` as often as `@p0`, and a
 * statement using positional `?` markers reports no name at all and is matched by position.
 *
 * @param {ReadonlyArray<{name: string, value: unknown}>} parameters
 */
/**
 * WAL is not a VFS-specific limitation: no web build of SQLite can provide the shared memory it
 * needs. Rejected here as well as in the .NET command layer so a JavaScript caller cannot enable it
 * either - and so a "success" that actually left DELETE mode cannot be mistaken for WAL.
 */
function rejectUnsupportedSql(sql) {
  if (typeof sql !== 'string') {
    return;
  }

  if (/pragma\s+journal_mode\s*=\s*['"`]?wal\b/i.test(sql)) {
    throw new Error(
      'WAL mode is not available in the browser: WebAssembly has no shared-memory primitives for it, '
      + 'and no web VFS implements it. BlazorSqlite uses DELETE or TRUNCATE journaling.');
  }

  const limits = session?.limits;
  if (limits && !limits.supportsMultiDatabaseTransactions && /(?:^|;)\s*attach(?:\s+database)?\b/i.test(sql)) {
    throw new Error(
      'ATTACH is not available on this storage backend: it cannot run a transaction that spans '
      + 'more than one database. Open each database on its own connection.');
  }

  if (limits && !limits.canChangePageSize && /pragma\s+page_size\s*=/i.test(sql)) {
    throw new Error(
      'PRAGMA page_size cannot be changed on this storage backend: the page size is fixed to the '
      + 'backend\'s block size.');
  }
}

function bind(sqlite3, stmt, parameters) {
  const count = sqlite3.bind_parameter_count(stmt);

  for (let i = 1; i <= count; i++) {
    const declared = sqlite3.bind_parameter_name(stmt, i);
    const parameter = declared
      ? parameters.find(p => p.name === declared || p.name === declared.slice(1))
      : parameters[i - 1];

    if (!parameter) {
      throw new Error(
        `No value was supplied for parameter ${declared ?? `#${i}`}. Supplied: `
        + `${parameters.map(p => p.name).join(', ') || '(none)'}.`);
    }

    sqlite3.bind(stmt, i, decodeParameter(parameter));
  }
}

async function readRows(sqlite3, stmt, columnCount, resultKind) {
  const rows = [];
  let columnTypes = null;

  while (await sqlite3.step(stmt) === SQLite.SQLITE_ROW) {
    // Storage classes of the first row, not declared types: the engine is compiled with
    // SQLITE_OMIT_DECLTYPE, so declared types do not exist in this build. Recovering them would take
    // a from-source build without that define.
    columnTypes ??= readColumnTypes(sqlite3, stmt, columnCount);

    rows.push(encodeRow(sqlite3, stmt, columnCount));

    if (resultKind === 'scalar') {
      // One value is all the caller will read, and the remaining rows may be expensive.
      break;
    }
  }

  return { rows, columnTypes: columnTypes ?? new Array(columnCount).fill(null) };
}

const STORAGE_CLASS = Object.freeze({
  [SQLite.SQLITE_INTEGER]: 'INTEGER',
  [SQLite.SQLITE_FLOAT]: 'REAL',
  [SQLite.SQLITE_TEXT]: 'TEXT',
  [SQLite.SQLITE_BLOB]: 'BLOB',
  [SQLite.SQLITE_NULL]: null,
});

function readColumnTypes(sqlite3, stmt, columnCount) {
  const types = new Array(columnCount);
  for (let i = 0; i < columnCount; i++) {
    types[i] = STORAGE_CLASS[sqlite3.column_type(stmt, i)] ?? null;
  }

  return types;
}
