// The main-thread half of the transport: spawns the worker that owns a database and correlates
// requests with responses. This is the surface the .NET transport calls.

const DEFAULT_WORKER_URL = new URL('./blazor-sqlite-worker.js', import.meta.url).href;

// Re-exported so a JavaScript caller can work in plain values without importing the wire module and
// without inventing a second, divergent encoding. The .NET transport does the same work in C#.
export {
  decodeReplyBinary,
  decodeValue,
  encodeBatchBinary,
  encodeValue,
  WireType,
} from './blazor-sqlite-wire.js';

/**
 * Starts a worker for one database.
 *
 * @param {object} options
 * @param {string} options.databaseName
 * @param {string} options.requiredBuild `'synchronous'` or `'asyncCapable'`
 * @param {{moduleUrl: string, registerExport?: string}} [options.vfs]
 * @param {{supportsMultiDatabaseTransactions?: boolean, canChangePageSize?: boolean}} [options.limits]
 * @param {string[]} [options.onOpenSql] configuration statements run once the database is open
 * @param {{moduleUrl: string, registerExport?: string}[]} [options.extensions] function/collation modules
 * @param {string} [options.workerUrl] overridable so an application can host the worker itself
 * @returns {Promise<SqliteHost>}
 */
export async function open({ databaseName, requiredBuild, vfs, limits, onOpenSql, extensions, workerUrl }) {
  const host = createHost(workerUrl);
  await host.send({ kind: 'open', databaseName, requiredBuild, vfs, limits, onOpenSql, extensions });
  return host;
}

/**
 * Creates a host without opening a database.
 *
 * Exists for the .NET transport: Blazor can hold an <c>IJSObjectReference</c> to the host and then
 * drive every operation - including open - through <c>call</c>, so a failure never has to throw
 * across the interop boundary.
 *
 * @param {string} [workerUrl]
 */
export function createHost(workerUrl) {
  return new SqliteHost(workerUrl ?? DEFAULT_WORKER_URL);
}

const CHANGE_CHANNEL = 'blazor-sqlite-changes';

class SqliteHost {
  #worker;
  #pending = new Map();
  #nextId = 1;
  #disposed = false;
  #listeners = new Set();
  #channel = typeof BroadcastChannel === 'function' ? new BroadcastChannel(CHANGE_CHANNEL) : null;
  #databaseName = null;

  constructor(workerUrl) {
    this.#worker = new Worker(workerUrl, { type: 'module' });
    this.#worker.addEventListener('message', event => this.#settle(event.data));
    this.#channel?.addEventListener('message', event => this.#emit(event.data, { local: false }));

    // A worker that dies with requests outstanding must not leave them pending forever.
    this.#worker.addEventListener('error', event => this.#failAll(
      new Error(`The SQLite worker failed: ${event.message ?? 'unknown error'}`)));
  }

  /**
   * Subscribes to table-change notifications. The listener receives the payload and a second
   * argument saying whether the write happened in this host (`local: true`) or arrived from
   * another tab over the broadcast channel (`local: false`).
   *
   * @param {(payload: {kind: 'notify', databaseName: string, tables: string[]}, origin: {local: boolean}) => void} listener
   * @returns {() => void} unsubscribe
   */
  onNotify(listener) {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  /**
   * Forwards cross-tab notifications for this host's database to a .NET object.
   *
   * Local writes are not forwarded: the .NET connection already learns about those from the
   * execute reply, synchronously and in order, and a second asynchronous notification would only
   * re-run every live query twice.
   *
   * @param {{invokeMethodAsync(method: string, ...args: unknown[]): Promise<unknown>}} dotNetObject
   * @param {string} methodName a `[JSInvokable]` method taking `string[] tables`
   */
  subscribeDotNet(dotNetObject, methodName) {
    return this.onNotify((payload, { local }) => {
      if (local || (this.#databaseName !== null && payload.databaseName !== this.#databaseName)) {
        return;
      }

      dotNetObject.invokeMethodAsync(methodName, payload.tables).catch(() => {
        // The .NET side may be disposing; a dropped notification is not an error worth surfacing.
      });
    });
  }

  #emit(payload, { local }) {
    if (!payload || payload.kind !== 'notify') {
      return;
    }

    if (local) {
      this.#channel?.postMessage(payload);
    }

    for (const listener of this.#listeners) {
      try {
        listener(payload, { local });
      } catch (error) {
        console.error('A BlazorSqlite change listener threw.', error);
      }
    }
  }

  #settle(response) {
    if (response?.kind === 'notify') {
      this.#emit(response, { local: true });
      return;
    }

    const pending = this.#pending.get(response.id);
    if (!pending) {
      return;
    }

    this.#pending.delete(response.id);

    if (response.ok) {
      pending.resolve(response.result);
    } else {
      pending.reject(toError(response.error));
    }
  }

  #failAll(error) {
    for (const pending of this.#pending.values()) {
      pending.reject(error);
    }

    this.#pending.clear();
  }

  /**
   * @param {object} request
   * @param {Transferable[]} [transfer] buffers to move rather than copy into the worker
   */
  send(request, transfer) {
    if (this.#disposed) {
      return Promise.reject(new Error('This SQLite host has been disposed.'));
    }

    if (request.kind === 'open' && typeof request.databaseName === 'string') {
      this.#databaseName = request.databaseName;
    }

    const id = this.#nextId++;

    return new Promise((resolve, reject) => {
      this.#pending.set(id, { resolve, reject });
      this.#worker.postMessage({ ...request, id }, transfer ?? []);
    });
  }

  /**
   * Executes a batch in one round trip, with tagged rows in the reply.
   *
   * @param {ReadonlyArray<{commandText: string, parameters?: ReadonlyArray<{name: string, type: number, value: unknown}>, resultKind?: 'nonQuery'|'scalar'|'reader', maxRows?: number}>} batch
   */
  execute(batch) {
    return this.send({ kind: 'execute', batch });
  }

  /**
   * Executes a binary-encoded batch and returns the binary reply, transferring both buffers so
   * neither is copied. Never rejects for a SQL failure: the reply carries it.
   *
   * @param {Uint8Array} batch encoded by `encodeBatchBinary` or by `SqliteWireFormat` on the .NET side
   * @returns {Promise<Uint8Array>}
   */
  async executeBinary(batch) {
    const bytes = batch instanceof Uint8Array ? batch : new Uint8Array(batch);
    const transferable = bytes.byteOffset === 0 && bytes.byteLength === bytes.buffer.byteLength
      ? [bytes.buffer]
      : [];
    const reply = await this.send({ kind: 'executeBinary', batch: bytes }, transferable);
    return reply instanceof Uint8Array ? reply : new Uint8Array(reply);
  }

  /**
   * Runs any worker request and returns the outcome as data rather than by throwing.
   *
   * This exists for .NET callers, and the reason is a hard limit rather than a preference: Blazor's JS
   * interop reduces a thrown JavaScript error to its message, so the SQLite result code would be lost -
   * and that code is how EF tells a unique-constraint violation from a busy database. An envelope keeps
   * it.
   */
  async call(request) {
    try {
      return { ok: true, result: await this.send(request) };
    } catch (error) {
      return {
        ok: false,
        error: {
          message: error.message,
          sqliteCode: error.sqliteCode ?? null,
          name: error.name ?? 'Error',
        },
      };
    }
  }

  version() {
    return this.send({ kind: 'version' });
  }

  /**
   * A consistent snapshot of the open database as a SQLite file image.
   *
   * @param {string} [schema] `'main'` unless an attached database is wanted
   * @returns {Promise<Uint8Array>}
   */
  export(schema = 'main') {
    return this.send({ kind: 'serialize', schema });
  }

  async close() {
    await this.send({ kind: 'close' });
  }

  /**
   * Terminates the worker. Outstanding requests are rejected rather than abandoned, because a caller
   * awaiting a query that will never answer is worse than a caller seeing the shutdown.
   */
  dispose() {
    if (this.#disposed) {
      return;
    }

    this.#disposed = true;
    this.#failAll(new Error('The SQLite host was disposed while the request was in flight.'));
    this.#listeners.clear();
    this.#channel?.close();
    this.#worker.terminate();
  }
}

/**
 * Rebuilds an error from the worker, preserving the SQLite result code so callers can distinguish a
 * constraint violation from a busy database.
 */
function toError({ message, sqliteCode, name }) {
  const error = new Error(message);
  error.name = name ?? 'Error';

  if (sqliteCode !== null && sqliteCode !== undefined) {
    error.sqliteCode = sqliteCode;
  }

  return error;
}
