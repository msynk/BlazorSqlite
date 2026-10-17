// The dedicated worker that owns one SQLite database.
//
// It exists because the storage APIs worth using are worker-only: OPFS synchronous access handles do
// not exist on the main thread. Everything here is request/response over postMessage, correlated by
// id, and strictly serialized - one SQLite connection cannot execute two statements at once, so
// overlapping requests queue rather than interleave.
//
// Two execute paths share one engine loop. `executeBinary` is what the .NET transport uses: the
// batch arrives as a transferred buffer, rows are written straight from the statement into a
// reply buffer, and that buffer is transferred back. `execute` is the JavaScript-facing path with
// structured-clone-friendly tagged rows. Both are driven by `runBatch` with a different row sink.

import { loadEngine } from './blazor-sqlite-engine.js';
import { registerFunctions } from './blazor-sqlite-functions.js';
import {
  BinaryWriter,
  ResultWriter,
  beginReply,
  decodeBatchBinary,
  decodeParameter,
  encodeErrorReply,
  encodeRow,
} from './blazor-sqlite-wire.js';
import * as SQLite from './engine/sqlite-api.js';

/**
 * @typedef {object} Session
 * @property {any} sqlite3
 * @property {any} module
 * @property {number} db
 * @property {string} build
 * @property {string} databaseName
 * @property {{supportsMultiDatabaseTransactions: boolean, canChangePageSize: boolean}} limits
 * @property {StatementCache} statements
 * @property {Set<string>} changed tables written since the current batch began, from the update hook
 * @property {(stmt: number) => number} stmtReadonly
 * @property {(db: number) => bigint} lastInsertRowId
 */

/** @type {Session | null} */
let session = null;

// Requests are chained so that each runs to completion before the next begins. Without this a second
// execute could start stepping while the first is suspended awaiting storage.
let queue = Promise.resolve();

self.addEventListener('message', event => {
  const request = event.data;
  const port = event.ports?.[0] ?? self;

  queue = queue.then(async () => {
    if (request.kind === 'executeBinary') {
      // Never throws across the boundary: the reply itself carries success or failure, so the
      // .NET side decodes exactly one shape.
      const reply = await executeBinary(request.batch);
      port.postMessage({ id: request.id, ok: true, result: reply }, [reply.buffer]);
      return;
    }

    try {
      const result = await dispatch(request);
      port.postMessage({ id: request.id, ok: true, result });
    } catch (error) {
      port.postMessage({ id: request.id, ok: false, error: describe(error) });
    }
  });
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
      return await close();

    case 'version':
      return { version: requireSession().sqlite3.libversion(), build: session.build };

    case 'serialize':
      return serialize(request.schema ?? 'main');

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

async function open({ databaseName, requiredBuild, vfs, limits, onOpenSql, extensions }) {
  if (session) {
    if (session.databaseName !== databaseName) {
      throw new Error(
        `This worker already owns '${session.databaseName}'. A worker hosts one database, so `
        + `opening '${databaseName}' needs its own worker.`);
    }

    return { build: session.build, vfsName: session.vfsName, reused: true };
  }

  const engine = await loadEngine({ requiredBuild, databaseName, vfs });
  const { sqlite3, module } = engine;

  // The VFS name matters: passing it explicitly means a provider that registers without claiming the
  // default still gets used, and a bug in registration surfaces as "no such vfs" here rather than as
  // a database silently opened on the wrong storage.
  const db = await sqlite3.open_v2(
    databaseName,
    SQLite.SQLITE_OPEN_CREATE | SQLite.SQLITE_OPEN_READWRITE,
    engine.vfsName ?? undefined);

  // EF Core's SQLite provider only installs these on a real SqliteConnection. We are not one, so
  // every open has to - miss it and every decimal comparison, aggregate, and REGEXP is wrong.
  registerFunctions(module, sqlite3, db);

  // Change tracking comes from the engine, not from parsing SQL: the update hook names every table
  // a row was inserted into, updated, or deleted from, including through triggers and cascades.
  const changed = new Set();
  sqlite3.update_hook(db, (_type, _dbName, table) => {
    changed.add(table);
  });

  const stmtReadonly = module.cwrap('sqlite3_stmt_readonly', 'number', ['number']);
  const lastInsertRowId = db => {
    const lo32 = module.ccall('sqlite3_last_insert_rowid', 'number', ['number'], [db]);
    const hi32 = module.getTempRet0();
    return (BigInt(hi32) << 32n) | (BigInt(lo32) & 0xffffffffn);
  };

  session = {
    sqlite3,
    module,
    db,
    build: engine.build,
    vfsName: engine.vfsName,
    databaseName,
    limits: {
      supportsMultiDatabaseTransactions: limits?.supportsMultiDatabaseTransactions !== false,
      canChangePageSize: limits?.canChangePageSize !== false,
    },
    statements: new StatementCache(sqlite3, db),
    changed,
    stmtReadonly,
    lastInsertRowId,
  };

  // Configuration SQL from the provider (cache_size for batch-atomic and the like) and from the
  // transport (foreign_keys). Run here rather than through execute() so a guard meant for application
  // SQL cannot block them, and before extensions so an extension sees the configured connection.
  for (const sql of [...(engine.onOpenSql ?? []), ...(onOpenSql ?? [])]) {
    await runConfigurationSql(sqlite3, db, sql);
  }

  // Extension modules add user-defined functions, collations, or virtual tables. They receive the
  // same handles the built-in function set does.
  for (const extension of extensions ?? []) {
    const imported = await import(extension.moduleUrl);
    const register = imported[extension.registerExport ?? 'register'];
    if (typeof register !== 'function') {
      throw new Error(
        `'${extension.moduleUrl}' does not export a function named '${extension.registerExport ?? 'register'}'.`);
    }

    await register({ module, sqlite3, db, databaseName });
  }

  return { build: engine.build, vfsName: engine.vfsName, reused: false };
}

async function runConfigurationSql(sqlite3, db, sql) {
  for await (const stmt of sqlite3.statements(db, sql)) {
    while (await sqlite3.step(stmt) === SQLite.SQLITE_ROW) {
      // Drained: these statements are configuration, not queries.
    }
  }
}

async function close() {
  if (!session) {
    return { closed: false };
  }

  const current = session;
  session = null;
  await current.statements.clear();
  await current.sqlite3.close(current.db);
  return { closed: true };
}

/**
 * A consistent snapshot of the open database as a SQLite file image, taken through
 * `sqlite3_serialize` so it reflects every committed write without closing the connection.
 */
function serialize(schema) {
  const { module, db } = requireSession();
  const sizePtr = module._sqlite3_malloc(8);
  try {
    const pointer = module.ccall(
      'sqlite3_serialize',
      'number',
      ['number', 'string', 'number', 'number'],
      [db, schema, sizePtr, 0]);

    const lo32 = module.getValue(sizePtr, 'i32');
    const hi32 = module.getValue(sizePtr + 4, 'i32');
    const size = hi32 * 0x100000000 + (lo32 >>> 0);

    if (!pointer) {
      if (size === 0) {
        // A database with no pages yet serializes to nothing, which is a valid (empty) image.
        return new Uint8Array(0);
      }

      throw new Error(`sqlite3_serialize could not snapshot schema '${schema}'.`);
    }

    try {
      return module.HEAPU8.slice(pointer, pointer + size);
    } finally {
      module._sqlite3_free(pointer);
    }
  } finally {
    module._sqlite3_free(sizePtr);
  }
}

/**
 * Runs a batch in one round trip, returning one result per request in order.
 *
 * Batching is the whole point of the coarse transport contract: in the browser the cost that dominates
 * is crossing this boundary, not executing the SQL.
 */
async function execute(batch) {
  const results = [];

  await runBatch(batch, {
    begin: () => {
      results.push({
        columnNames: [],
        columnTypes: [],
        rows: [],
        recordsAffected: 0,
        lastInsertRowId: 0n,
        changedTables: [],
      });
    },
    columns: (names, storageClasses) => {
      const result = results[results.length - 1];
      result.columnNames = names;
      result.columnTypes = storageClasses.map(code => STORAGE_CLASS[code] ?? null);
    },
    beginRows: () => {},
    row: (sqlite3, stmt, columnCount) => {
      results[results.length - 1].rows.push(encodeRow(sqlite3, stmt, columnCount));
    },
    end: (recordsAffected, lastInsertRowId, changedTables) => {
      const result = results[results.length - 1];
      result.recordsAffected = recordsAffected;
      result.lastInsertRowId = lastInsertRowId;
      result.changedTables = [...changedTables];
    },
  });

  // Tagged rows are plain data, but the BigInt row id is not JSON-safe for every caller.
  for (const result of results) {
    result.lastInsertRowId = Number(result.lastInsertRowId);
  }

  return results;
}

/**
 * The .NET path: a binary batch in, a binary reply out, with no object graph in between.
 *
 * @param {Uint8Array} bytes
 * @returns {Promise<Uint8Array>}
 */
async function executeBinary(bytes) {
  const writer = new BinaryWriter(Math.max(4096, bytes.byteLength * 4));
  beginReply(writer);

  let batch;
  try {
    batch = decodeBatchBinary(bytes);
  } catch (error) {
    return encodeErrorReply(describe(error));
  }

  const countOffset = writer.reserveU32();
  const result = new ResultWriter(writer);
  let completed = 0;

  try {
    await runBatch(batch, {
      begin: () => {
        completed++;
      },
      columns: (names, storageClasses) => result.columns(names, storageClasses),
      beginRows: () => result.beginRows(),
      row: (sqlite3, stmt) => result.row(sqlite3, stmt),
      end: (recordsAffected, lastInsertRowId, changedTables) =>
        result.finish(recordsAffected, lastInsertRowId, changedTables),
    });
  } catch (error) {
    // A failure part-way through discards the partial reply: the .NET side wants one exception,
    // not half a result set followed by one.
    return encodeErrorReply(describe(error));
  }

  writer.patchU32(countOffset, completed);
  return writer.finish();
}

/**
 * The engine loop shared by both execute paths.
 *
 * The sink receives, per request: `begin()`, then `columns(names, classes)` once the row-producing
 * statement is known (or with empty arrays if none), then `beginRows()`, then `row(...)` per row,
 * then `end(recordsAffected, lastInsertRowId, changedTables)`.
 */
async function runBatch(batch, sink) {
  const current = requireSession();
  const { sqlite3, db } = current;
  const notifyTables = new Set();
  let schemaChanged = false;

  for (const request of batch) {
    rejectUnsupportedSql(request.commandText);
    sink.begin();

    current.changed.clear();
    const wantsRows = request.resultKind !== 'nonQuery';
    const maxRows = request.resultKind === 'scalar'
      ? 1
      : (Number.isInteger(request.maxRows) && request.maxRows >= 0 ? request.maxRows : Infinity);

    const prepared = await current.statements.acquire(request.commandText);
    let recordsAffected = 0;
    let captured = false;

    try {
      for (const stmt of prepared.statements) {
        try {
          bind(sqlite3, stmt, request.parameters ?? []);
          const columnCount = sqlite3.column_count(stmt);
          const producesRows = columnCount > 0;

          // Rows are taken from the first statement that produces any, and later statements still
          // run. This is what EF's insert-then-select pattern needs: the INSERT reports its changes
          // and the following SELECT is the result set the reader consumes.
          if (wantsRows && producesRows && !captured) {
            captured = true;
            const names = sqlite3.column_names(stmt);
            let storageClasses = null;
            let rows = 0;

            try {
              while (rows < maxRows && await sqlite3.step(stmt) === SQLite.SQLITE_ROW) {
                if (storageClasses === null) {
                  // Storage classes of the first row, not declared types: the engine is compiled
                  // with SQLITE_OMIT_DECLTYPE, so declared types do not exist in this build.
                  storageClasses = readStorageClasses(sqlite3, stmt, columnCount);
                  sink.columns(names, storageClasses);
                  sink.beginRows();
                }

                sink.row(sqlite3, stmt, columnCount);
                rows++;
              }
            } finally {
              if (storageClasses === null) {
                sink.columns(names, new Array(columnCount).fill(null));
                sink.beginRows();
              }
            }
          } else {
            while (await sqlite3.step(stmt) === SQLite.SQLITE_ROW) {
              // Drained deliberately: a statement whose rows nobody asked for still has to run.
            }
          }

          // Read-only statements leave sqlite3_changes() at the previous write's value, which must
          // not be counted twice - this is the same rule Microsoft.Data.Sqlite applies.
          if (!current.stmtReadonly(stmt)) {
            recordsAffected += sqlite3.changes(db);
          }
        } finally {
          // Whether the statement finished or threw, its bindings and cursor must be released: to
          // free locks, and so a cached statement starts clean next time.
          await prepared.release(stmt);
        }
      }

      if (!captured) {
        sink.columns([], []);
        sink.beginRows();
      }
    } finally {
      await prepared.dispose();
    }

    if (isSchemaChange(request.commandText)) {
      schemaChanged = true;
      for (const name of extractDdlTables(request.commandText)) {
        current.changed.add(name);
      }
    }

    for (const table of current.changed) {
      notifyTables.add(table);
    }

    sink.end(recordsAffected, current.lastInsertRowId(db), current.changed);
  }

  if (schemaChanged) {
    // Cached statements compiled against the old schema would re-prepare themselves on step, but
    // their column metadata was read before stepping. Dropping them is cheap and always right.
    await current.statements.clear();
  }

  if (notifyTables.size > 0) {
    self.postMessage({ kind: 'notify', databaseName: current.databaseName, tables: [...notifyTables] });
  }
}

/**
 * Prepared-statement cache keyed by SQL text.
 *
 * EF Core and most applications send the same statement text over and over with different
 * parameters. Preparing is a full parse and plan; resetting a prepared statement is nearly free.
 * Only single-statement texts are cached, because a multi-statement batch is almost never repeated
 * verbatim and would need every handle kept alive together.
 */
class StatementCache {
  static CAPACITY = 64;
  static SQLITE_PREPARE_PERSISTENT = 0x01;

  #sqlite3;
  #db;
  /** @type {Map<string, number>} insertion order is recency: Map iteration gives the oldest first */
  #entries = new Map();
  /** @type {Set<number>} handles currently lent out, which must not be evicted or lent twice */
  #inUse = new Set();

  constructor(sqlite3, db) {
    this.#sqlite3 = sqlite3;
    this.#db = db;
  }

  /**
   * @param {string} sql
   * @returns {Promise<{statements: number[], release(stmt: number): Promise<void>, dispose(): Promise<void>}>}
   */
  async acquire(sql) {
    const sqlite3 = this.#sqlite3;
    const cached = this.#entries.get(sql);

    if (cached !== undefined && !this.#inUse.has(cached)) {
      // Refresh recency.
      this.#entries.delete(sql);
      this.#entries.set(sql, cached);
      this.#inUse.add(cached);
      return {
        statements: [cached],
        release: stmt => this.#reset(stmt),
        dispose: async () => {
          this.#inUse.delete(cached);
        },
      };
    }

    const statements = [];
    try {
      for await (const stmt of sqlite3.statements(this.#db, sql, {
        unscoped: true,
        flags: StatementCache.SQLITE_PREPARE_PERSISTENT,
      })) {
        statements.push(stmt);
      }
    } catch (error) {
      for (const stmt of statements) {
        await sqlite3.finalize(stmt);
      }

      throw error;
    }

    if (statements.length === 1 && cached === undefined) {
      const stmt = statements[0];
      this.#entries.set(sql, stmt);
      this.#inUse.add(stmt);
      await this.#evict();
      return {
        statements,
        release: s => this.#reset(s),
        dispose: async () => {
          this.#inUse.delete(stmt);
        },
      };
    }

    return {
      statements,
      release: async () => {
        // Finalized in dispose; nothing to release per statement.
      },
      dispose: async () => {
        for (const stmt of statements) {
          await sqlite3.finalize(stmt);
        }
      },
    };
  }

  async #reset(stmt) {
    try {
      await this.#sqlite3.reset(stmt);
    } catch {
      // reset() repeats the error code of a failed step, which has already been thrown.
    }

    try {
      this.#sqlite3.clear_bindings(stmt);
    } catch {
      // A statement that cannot clear is finalized on the next schema change or eviction.
    }
  }

  async #evict() {
    while (this.#entries.size > StatementCache.CAPACITY) {
      let evicted = false;
      for (const [sql, stmt] of this.#entries) {
        if (this.#inUse.has(stmt)) {
          continue;
        }

        this.#entries.delete(sql);
        await this.#sqlite3.finalize(stmt);
        evicted = true;
        break;
      }

      if (!evicted) {
        return;
      }
    }
  }

  async clear() {
    const entries = [...this.#entries.values()];
    this.#entries.clear();
    for (const stmt of entries) {
      if (!this.#inUse.has(stmt)) {
        await this.#sqlite3.finalize(stmt);
      }
    }
  }
}

function isSchemaChange(sql) {
  return typeof sql === 'string' && /\b(?:create|drop|alter|vacuum|reindex)\b/i.test(sql);
}

/** Tables named by DDL, which the update hook does not see. */
function extractDdlTables(sql) {
  const names = [];
  const pattern = /\b(?:table|into|from|update|join)\s+(?:if\s+(?:not\s+)?exists\s+)?(?:["`[]?[A-Za-z_]\w*["`\]]?\s*\.\s*)?["`[]?([A-Za-z_]\w*)/gi;
  let match;
  while ((match = pattern.exec(sql))) {
    if (!match[1].toLowerCase().startsWith('sqlite_')) {
      names.push(match[1]);
    }
  }

  return names;
}

/**
 * WAL is not a VFS-specific limitation: no web build of SQLite can provide the shared memory it
 * needs. Rejected here as well as in the .NET command layer so a JavaScript caller cannot enable it
 * either - and so a "success" that actually left DELETE mode cannot be mistaken for WAL.
 */
function rejectUnsupportedSql(sql) {
  if (typeof sql !== 'string') {
    return;
  }

  // Cheap pre-check: the guarded statements all mention one of these words, and most SQL does not.
  const lower = sql.length < 4096 ? sql.toLowerCase() : null;
  if (lower !== null && !lower.includes('pragma') && !lower.includes('attach')) {
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

/**
 * Binds by asking the statement what it wants, rather than handing the engine a dictionary and hoping
 * the keys line up.
 *
 * The alternative - `bind_collection` with a name-keyed object - leaves an unmatched parameter bound to
 * NULL, which turns a misspelled parameter name into wrong results instead of an error. Names are
 * matched with or without their sigil, because ADO.NET callers write `p0` as often as `@p0`, and a
 * statement using positional `?` markers reports no name at all and is matched by position.
 *
 * @param {ReadonlyArray<{name: string, type: number, value: unknown}>} parameters
 */
function bind(sqlite3, stmt, parameters) {
  const count = sqlite3.bind_parameter_count(stmt);
  if (count === 0) {
    return;
  }

  const byName = parameters.length > 4 ? new Map(parameters.map(p => [p.name, p])) : null;
  const lookup = name => byName ? byName.get(name) : parameters.find(p => p.name === name);

  for (let i = 1; i <= count; i++) {
    const declared = sqlite3.bind_parameter_name(stmt, i);
    const parameter = declared
      ? (lookup(declared) ?? lookup(declared.slice(1)))
      : parameters[i - 1];

    if (!parameter) {
      throw new Error(
        `No value was supplied for parameter ${declared ?? `#${i}`}. Supplied: `
        + `${parameters.map(p => p.name).join(', ') || '(none)'}.`);
    }

    sqlite3.bind(stmt, i, decodeParameter(parameter));
  }
}

const STORAGE_CLASS = Object.freeze({
  [SQLite.SQLITE_INTEGER]: 'INTEGER',
  [SQLite.SQLITE_FLOAT]: 'REAL',
  [SQLite.SQLITE_TEXT]: 'TEXT',
  [SQLite.SQLITE_BLOB]: 'BLOB',
  [SQLite.SQLITE_NULL]: null,
});

function readStorageClasses(sqlite3, stmt, columnCount) {
  const classes = new Array(columnCount);
  for (let i = 0; i < columnCount; i++) {
    const code = sqlite3.column_type(stmt, i);
    classes[i] = code === SQLite.SQLITE_NULL ? null : code;
  }

  return classes;
}
