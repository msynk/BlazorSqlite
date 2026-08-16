import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { raiseFunctionTableLimit } from '../../src/BlazorSqlite.Js/wwwroot/blazor-sqlite-wasm-table.js';

const engineDir = join(
  dirname(fileURLToPath(import.meta.url)),
  '../../src/BlazorSqlite.Js/wwwroot/engine');

const builds = ['wa-sqlite.wasm', 'wa-sqlite-jspi.wasm', 'wa-sqlite-async.wasm'];

function readTableLimits(bytes) {
  let offset = 8;
  while (offset < bytes.length) {
    const id = bytes[offset++];
    let size = 0;
    let shift = 0;
    while (true) {
      const byte = bytes[offset++];
      size |= (byte & 0x7f) << shift;
      if ((byte & 0x80) === 0) {
        break;
      }

      shift += 7;
    }

    if (id === 4) {
      const payload = bytes.subarray(offset, offset + size);
      // count=1, funcref, limits=1, min, max — all LEB after the two tag bytes.
      let i = 0;
      const skipLeb = () => {
        let n = 0;
        let s = 0;
        while (true) {
          const b = payload[i++];
          n |= (b & 0x7f) << s;
          if ((b & 0x80) === 0) {
            return n >>> 0;
          }

          s += 7;
        }
      };

      skipLeb();
      i++; // funcref
      i++; // limits flag
      return { min: skipLeb(), max: skipLeb() };
    }

    offset += size;
  }

  throw new Error('No table section.');
}

for (const name of builds) {
  test(`${name} is valid after the table maximum is raised`, () => {
    const original = new Uint8Array(readFileSync(join(engineDir, name)));
    const before = readTableLimits(original);
    assert.equal(before.min, before.max, 'the vendored pin is a fixed-size table');

    const patched = raiseFunctionTableLimit(original, 16);
    assert.notEqual(patched, original);
    assert.ok(WebAssembly.validate(patched));

    const after = readTableLimits(patched);
    assert.equal(after.min, before.min);
    assert.equal(after.max, before.min + 16);
    assert.deepEqual(readTableLimits(original), before);
  });
}
