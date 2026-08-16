// The wire format shared by the worker and the .NET transport.
//
// JSON cannot carry two things SQLite returns routinely: integers outside the range a JavaScript number
// holds exactly, and blobs. Both would fail quietly - a 64-bit key losing its low bits, a blob arriving
// as an array of numbers - so every value travels tagged with its SQLite storage class.
//
// The tags are SQLite's own type codes, which is why this module can label a value straight from
// sqlite3_column_type with no translation table. The .NET half mirrors this file exactly; see
// SqliteWireFormat.
//
// Encoding has to happen here rather than on the main thread because the distinction between INTEGER 1
// and REAL 1.0 exists only while the statement is live. By the time a row is a plain JavaScript array,
// both are the number 1.

export const WireType = Object.freeze({
  integer: 1,
  real: 2,
  text: 3,
  blob: 4,
  null: 5,
});

/** Integers beyond this cannot be represented exactly as a JavaScript number. */
const MAX_EXACT_INTEGER = 9007199254740991n; // 2^53 - 1

/**
 * Reads the current row of a live statement into tagged form.
 *
 * @param {number} columnCount from `column_count`, so a row of no columns is still well-formed
 * @returns {{t: number[], v: unknown[]}}
 */
export function encodeRow(sqlite3, stmt, columnCount) {
  const t = new Array(columnCount);
  const v = new Array(columnCount);

  for (let i = 0; i < columnCount; i++) {
    const type = sqlite3.column_type(stmt, i);
    t[i] = type;

    switch (type) {
      case WireType.null:
        v[i] = null;
        break;

      case WireType.integer: {
        // wa-sqlite hands back a number when the value fits exactly and a BigInt when it does not,
        // which is the same boundary this format cares about.
        const value = sqlite3.column(stmt, i);
        v[i] = typeof value === 'bigint' ? String(value) : value;
        break;
      }

      case WireType.real:
        v[i] = sqlite3.column_double(stmt, i);
        break;

      case WireType.text:
        v[i] = sqlite3.column_text(stmt, i);
        break;

      case WireType.blob:
        // Encoded immediately: column_blob can alias WebAssembly memory, which the next engine call
        // may move.
        v[i] = toBase64(sqlite3.column_blob(stmt, i));
        break;

      default:
        throw new Error(`Column ${i} has unknown SQLite type ${type}.`);
    }
  }

  return { t, v };
}

/**
 * Turns a tagged parameter back into the value the engine binds.
 *
 * @param {{type: number, value: unknown}} parameter
 */
export function decodeParameter({ type, value }) {
  switch (type) {
    case WireType.null:
      return null;

    // A large integer arrives as a decimal string; BigInt is what makes the engine bind it as int64.
    case WireType.integer:
      return typeof value === 'string' ? BigInt(value) : value;

    case WireType.real:
    case WireType.text:
      return value;

    case WireType.blob:
      return fromBase64(/** @type {string} */(value));

    default:
      throw new Error(`A parameter arrived with unknown wire type ${type}.`);
  }
}

/**
 * Tags a plain JavaScript value, for callers driving the library from JavaScript rather than .NET.
 *
 * @returns {{type: number, value: unknown}}
 */
export function encodeValue(value) {
  if (value === null || value === undefined) {
    return { type: WireType.null, value: null };
  }

  switch (typeof value) {
    case 'boolean':
      return { type: WireType.integer, value: value ? 1 : 0 };

    case 'number':
      return Number.isInteger(value)
        ? { type: WireType.integer, value }
        : { type: WireType.real, value };

    case 'bigint':
      return {
        type: WireType.integer,
        value: value > MAX_EXACT_INTEGER || value < -MAX_EXACT_INTEGER ? String(value) : Number(value),
      };

    case 'string':
      return { type: WireType.text, value };

    default:
      if (value instanceof Uint8Array) {
        return { type: WireType.blob, value: toBase64(value) };
      }

      throw new Error(`Cannot put a value of type ${typeof value} on the wire.`);
  }
}

/** Untags a value, giving back a `bigint` only where a number would lose precision. */
export function decodeValue(type, value) {
  switch (type) {
    case WireType.null:
      return null;

    case WireType.integer:
      return typeof value === 'string' ? BigInt(value) : value;

    case WireType.blob:
      return fromBase64(value);

    default:
      return value;
  }
}

// Chunked because spreading a large array into String.fromCharCode overflows the argument limit, which
// shows up only once a blob gets big - exactly where it is least welcome.
const CHUNK = 0x8000;

function toBase64(bytes) {
  let binary = '';
  for (let i = 0; i < bytes.length; i += CHUNK) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + CHUNK));
  }

  return btoa(binary);
}

function fromBase64(text) {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }

  return bytes;
}
