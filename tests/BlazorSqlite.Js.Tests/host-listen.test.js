import assert from 'node:assert/strict';
import { test } from 'node:test';
import { listen } from '../../src/BlazorSqlite/wwwroot/blazor-sqlite-host.js';

// `listen` is the seam between the host's notifications and the .NET transport. It cannot be
// exercised from the browser suite, which has no .NET side, so its filtering is pinned here.
// A host is only `onNotify` as far as this function is concerned.
function fakeHost() {
  const listeners = new Set();
  return {
    onNotify(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    emit(payload, info) {
      for (const listener of listeners) {
        listener(payload, info);
      }
    },
  };
}

function fakeTarget() {
  const calls = [];
  return {
    calls,
    invokeMethodAsync(method, ...args) {
      calls.push({ method, args });
      return Promise.resolve();
    },
  };
}

const write = (databaseName, tables) => ({ kind: 'notify', databaseName, tables });

test('another tab\'s write reaches .NET', () => {
  const host = fakeHost();
  const target = fakeTarget();
  listen(host, target, 'app.db');

  host.emit(write('app.db', ['product']), { local: false });

  assert.deepEqual(target.calls, [{ method: 'OnTablesChanged', args: [['product']] }]);
});

// The .NET command layer raises its own writes the moment they complete. Relaying them again would
// re-run every live query twice per write.
test('this tab\'s own write is not relayed', () => {
  const host = fakeHost();
  const target = fakeTarget();
  listen(host, target, 'app.db');

  host.emit(write('app.db', ['product']), { local: true });

  assert.deepEqual(target.calls, []);
});

// The broadcast channel is origin-wide, so it carries every database's traffic.
test('a write to a different database is not relayed', () => {
  const host = fakeHost();
  const target = fakeTarget();
  listen(host, target, 'app.db');

  host.emit(write('other.db', ['product']), { local: false });

  assert.deepEqual(target.calls, []);
});

test('the method name is overridable and unsubscribing stops the relay', () => {
  const host = fakeHost();
  const target = fakeTarget();
  const stop = listen(host, target, 'app.db', 'Custom');

  host.emit(write('app.db', ['product']), { local: false });
  stop();
  host.emit(write('app.db', ['customer']), { local: false });

  assert.deepEqual(target.calls, [{ method: 'Custom', args: [['product']] }]);
});

test('a notification with no tables still calls through with an empty list', () => {
  const host = fakeHost();
  const target = fakeTarget();
  listen(host, target, 'app.db');

  host.emit({ kind: 'notify', databaseName: 'app.db' }, { local: false });

  assert.deepEqual(target.calls, [{ method: 'OnTablesChanged', args: [[]] }]);
});
