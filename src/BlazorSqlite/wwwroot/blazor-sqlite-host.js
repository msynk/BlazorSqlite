// The main-thread half of the transport: spawns the worker that owns a database and correlates
// requests with responses. This is the surface the .NET transport calls.

// Everything this module reaches for inherits the cache-busting query it was imported with, so an
// upgrade cannot pair a fresh host with a worker - or a wire module - the browser still holds from
// an older version.
const VERSION = new URL(import.meta.url).search;

const DEFAULT_WORKER_URL = new URL('./blazor-sqlite-worker.js' + VERSION, import.meta.url).href;

// Re-exported so a JavaScript caller can work in plain values without importing the wire module and
// without inventing a second, divergent encoding. The .NET transport does the same work in C#. The
// import is dynamic only to carry VERSION: a `export ... from` would resolve to the bare path.
const wire = await import('./blazor-sqlite-wire.js' + VERSION);

export const decodeValue = wire.decodeValue;
export const encodeValue = wire.encodeValue;
export const WireType = wire.WireType;

/**
 * Starts a worker for one database.
 *
 * @param {object} options
 * @param {string} options.databaseName
 * @param {string} options.requiredBuild `'synchronous'` or `'asyncCapable'`
 * @param {{moduleUrl: string, registerExport?: string}} [options.vfs]
 * @param {{supportsMultiDatabaseTransactions?: boolean, canChangePageSize?: boolean}} [options.limits]
 * @param {string} [options.workerUrl] overridable so an application can host the worker itself
 * @returns {Promise<SqliteHost>}
 */
export async function open({ databaseName, requiredBuild, vfs, limits, workerUrl }) {
  const host = createHost(workerUrl);
  await host.send({ kind: 'open', databaseName, requiredBuild, vfs, limits });
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

/**
 * Forwards committed writes to a .NET object, which is how a live query re-runs - for this tab's
 * own writes and for another tab's alike.
 *
 * Both are forwarded because the worker is the one that knows: its update hook names every table a
 * row landed in, cascades and triggers included, and it waits for the commit. The .NET transport
 * declares `ReportsLocalWrites` so the command layer does not raise the same write again from the
 * SQL text. Writes to a different database on the same origin are dropped: the broadcast channel is
 * origin-wide, so it carries every database's traffic, and the name is the only thing that
 * separates them.
 *
 * @param {SqliteHost} host
 * @param {{invokeMethodAsync: (name: string, ...args: unknown[]) => Promise<unknown>}} target
 * @param {string} databaseName
 * @param {string} [method] the [JSInvokable] method to call with the table names
 */
export function listen(host, target, databaseName, method = 'OnTablesChanged') {
  return host.onNotify(payload => {
    if (payload.databaseName !== databaseName) {
      return;
    }

    // Fire and forget: a disposed .NET reference must not wedge the notification pipeline.
    target.invokeMethodAsync(method, payload.tables ?? []).catch(() => {});
  });
}

const CHANGE_CHANNEL = 'blazor-sqlite-changes';

class SqliteHost {
  #worker;
  #pending = new Map();
  #nextId = 1;
  #disposed = false;
  #fatal = null;
  #listeners = new Set();
  #channel = typeof BroadcastChannel === 'function' ? new BroadcastChannel(CHANGE_CHANNEL) : null;

  constructor(workerUrl) {
    this.#worker = new Worker(workerUrl, { type: 'module' });
    this.#worker.addEventListener('message', event => this.#settle(event.data));
    this.#channel?.addEventListener('message', event => this.#emit(event.data, { local: false }));

    // A worker that dies with requests outstanding must not leave them pending forever - and one
    // that died must not swallow later requests either. The worker script wraps every request in
    // its own catch, so an error event here means the script itself failed to load or link: there
    // is nobody on the other end, and every request from now on gets told so instead of hanging.
    this.#worker.addEventListener('error', event => {
      this.#fatal = new Error(`The SQLite worker failed: ${event.message ?? 'unknown error'}`);
      this.#failAll(this.#fatal);
    });

    // A reply that cannot be deserialized never reaches #settle, so its request would wait for an
    // answer that has already been thrown away. The id is gone with it, so every outstanding
    // request fails - the same trade the error handler makes, and better than hanging.
    this.#worker.addEventListener('messageerror', () => this.#failAll(
      new Error('A reply from the SQLite worker could not be deserialized.')));
  }

  /**
   * Subscribes to write notifications.
   *
   * The listener is called as `listener(payload, { local })`. `local` is true for a write this
   * host's own worker performed and false for one another tab broadcast, which is the distinction a
   * caller that already knows about its own writes needs in order not to react twice.
   */
  onNotify(listener) {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  #emit(payload, { local }) {
    if (!payload || payload.kind !== 'notify') {
      return;
    }

    if (local) {
      this.#channel?.postMessage(payload);
    }

    for (const listener of this.#listeners) {
      listener(payload, { local });
    }
  }

  #settle(response) {
    if (response?.kind === 'notify') {
      this.#emit(response, { local: true });
      return;
    }

    const pending = this.#pending.get(response?.id);
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

  send(request) {
    if (this.#disposed) {
      return Promise.reject(new Error('This SQLite host has been disposed.'));
    }

    if (this.#fatal) {
      return Promise.reject(this.#fatal);
    }

    const id = this.#nextId++;

    return new Promise((resolve, reject) => {
      this.#pending.set(id, { resolve, reject });
      this.#worker.postMessage({ ...resolveModuleUrls(request), id });
    });
  }

  /**
   * Executes a batch in one round trip.
   *
   * @param {ReadonlyArray<{commandText: string, parameters?: ReadonlyArray<{name: string, value: unknown}>, resultKind?: 'nonQuery'|'scalar'|'reader'}>} batch
   */
  execute(batch) {
    return this.send({ kind: 'execute', batch });
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
    this.#channel?.close();
    this.#worker.terminate();
  }
}

/**
 * Makes a storage provider's VFS module URL absolute before it reaches the worker.
 *
 * Providers name their module the way every other Blazor asset is named - relative to the document,
 * as `./_content/<package>/<file>.js` - so an application served under a sub-path still finds it.
 * The worker cannot resolve that itself: a relative import there is relative to the worker script,
 * which lives in this package's own `_content` folder. Resolving here, where the document base is
 * known, keeps that detail out of every provider.
 */
function resolveModuleUrls(request) {
  const moduleUrl = request?.vfs?.moduleUrl;
  if (typeof moduleUrl !== 'string' || moduleUrl.length === 0) {
    return request;
  }

  const base = globalThis.document?.baseURI ?? globalThis.location?.href ?? import.meta.url;
  return { ...request, vfs: { ...request.vfs, moduleUrl: new URL(moduleUrl, base).href } };
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
