// Block-based Cache Storage VFS. Commits rely on SQLite's journal - the Cache API has no
// multi-entry atomic write, so we do not claim SQLITE_IOCAP_BATCH_ATOMIC.
//
// Pages are 4096-byte cache entries. Dirty pages flush on xSync. Cross-tab writers are
// coordinated by WebLocksMixin. A database that exists only in besql's `bit-Besql` cache is
// imported losslessly on first open and then served from our own layout.

import { FacadeVFS } from '/_content/BlazorSqlite/engine/FacadeVFS.js';
import * as VFS from '/_content/BlazorSqlite/engine/VFS.js';
import { WebLocksMixin } from '/_content/BlazorSqlite/engine/WebLocksMixin.js';

export const VFS_NAME = 'cache-storage';
export const CACHE_NAME = 'blazor-sqlite';
export const BESQL_CACHE = 'bit-Besql';
export const PAGE_SIZE = 4096;

class File {
  constructor(path, flags) {
    this.path = path;
    this.flags = flags;
    this.size = 0;
    this.dirty = new Map();
  }
}

export class CacheStorageVFS extends WebLocksMixin(FacadeVFS) {
  /** @type {Map<number, File>} */
  mapIdToFile = new Map();

  /** @type {Cache | null} */
  #cache = null;

  static async create(name, module) {
    const vfs = new CacheStorageVFS(name, module, { lockPolicy: 'shared' });
    await vfs.isReady();
    return vfs;
  }

  constructor(name, module, options) {
    super(name, module, options);
  }

  async isReady() {
    await super.isReady();
    this.#cache = await caches.open(CACHE_NAME);
  }

  getFilename(fileId) {
    return this.mapIdToFile.get(fileId).path;
  }

  async jOpen(zName, fileId, flags, pOutFlags) {
    const path = pathOf(zName || Math.random().toString(36).slice(2));
    let size = await this.#readSize(path);

    if (size === null) {
      const imported = await importBesqlIfPresent(fileNameOf(path));
      if (imported) {
        await this.#writeImage(path, imported);
        size = imported.byteLength;
      }
    }

    if (size === null) {
      if (!(flags & VFS.SQLITE_OPEN_CREATE)) {
        return VFS.SQLITE_CANTOPEN;
      }

      size = 0;
      await this.#writeSize(path, 0);
    }

    const file = new File(path, flags);
    file.size = size;
    this.mapIdToFile.set(fileId, file);
    pOutFlags.setInt32(0, flags, true);
    return VFS.SQLITE_OK;
  }

  async jDelete(zName) {
    await this.#deletePath(pathOf(zName));
    return VFS.SQLITE_OK;
  }

  async jAccess(zName, flags, pResOut) {
    const size = await this.#readSize(pathOf(zName));
    pResOut.setInt32(0, size === null ? 0 : 1, true);
    return VFS.SQLITE_OK;
  }

  async jClose(fileId) {
    const file = this.mapIdToFile.get(fileId);
    this.mapIdToFile.delete(fileId);
    if (!file) {
      return VFS.SQLITE_OK;
    }

    if (file.flags & VFS.SQLITE_OPEN_DELETEONCLOSE) {
      await this.#deletePath(file.path);
      return VFS.SQLITE_OK;
    }

    await this.#flush(file);
    return VFS.SQLITE_OK;
  }

  async jRead(fileId, pData, iOffset) {
    const file = this.mapIdToFile.get(fileId);
    if (iOffset >= file.size) {
      pData.fill(0);
      return VFS.SQLITE_IOERR_SHORT_READ;
    }

    let remaining = Math.min(pData.byteLength, file.size - iOffset);
    let written = 0;

    while (written < remaining) {
      const pageIndex = Math.floor((iOffset + written) / PAGE_SIZE);
      const pageOffset = (iOffset + written) % PAGE_SIZE;
      const page = await this.#readPage(file, pageIndex);
      const n = Math.min(PAGE_SIZE - pageOffset, remaining - written);
      pData.set(page.subarray(pageOffset, pageOffset + n), written);
      written += n;
    }

    if (written < pData.byteLength) {
      pData.fill(0, written);
      return VFS.SQLITE_IOERR_SHORT_READ;
    }

    return VFS.SQLITE_OK;
  }

  async jWrite(fileId, pData, iOffset) {
    const file = this.mapIdToFile.get(fileId);
    let written = 0;

    while (written < pData.byteLength) {
      const pageIndex = Math.floor((iOffset + written) / PAGE_SIZE);
      const pageOffset = (iOffset + written) % PAGE_SIZE;
      const n = Math.min(PAGE_SIZE - pageOffset, pData.byteLength - written);
      const page = pageOffset === 0 && n === PAGE_SIZE
        ? new Uint8Array(PAGE_SIZE)
        : await this.#readPage(file, pageIndex);
      page.set(pData.subarray(written, written + n), pageOffset);
      file.dirty.set(pageIndex, page);
      written += n;
    }

    file.size = Math.max(file.size, iOffset + pData.byteLength);
    return VFS.SQLITE_OK;
  }

  async jTruncate(fileId, size) {
    const file = this.mapIdToFile.get(fileId);
    file.size = size;
    const firstDropped = Math.ceil(size / PAGE_SIZE);
    for (const index of [...file.dirty.keys()]) {
      if (index >= firstDropped) {
        file.dirty.delete(index);
      }
    }

    await this.#dropPagesFrom(file.path, firstDropped);
    await this.#writeSize(file.path, size);
    return VFS.SQLITE_OK;
  }

  async jSync(fileId) {
    await this.#flush(this.mapIdToFile.get(fileId));
    return VFS.SQLITE_OK;
  }

  jFileSize(fileId, pSize64) {
    pSize64.setBigInt64(0, BigInt(this.mapIdToFile.get(fileId).size), true);
    return VFS.SQLITE_OK;
  }

  jDeviceCharacteristics() {
    return VFS.SQLITE_IOCAP_UNDELETABLE_WHEN_OPEN;
  }

  async #flush(file) {
    for (const [index, page] of file.dirty) {
      await this.#cache.put(pageUrl(file.path, index), bytesResponse(page));
    }

    file.dirty.clear();
    await this.#writeSize(file.path, file.size);
  }

  async #readPage(file, index) {
    const dirty = file.dirty.get(index);
    if (dirty) {
      return dirty;
    }

    const match = await this.#cache.match(pageUrl(file.path, index));
    if (!match) {
      return new Uint8Array(PAGE_SIZE);
    }

    return new Uint8Array(await match.arrayBuffer());
  }

  async #readSize(path) {
    const match = await this.#cache.match(metaUrl(path));
    if (!match) {
      return null;
    }

    const meta = await match.json();
    return typeof meta.size === 'number' ? meta.size : 0;
  }

  async #writeSize(path, size) {
    await this.#cache.put(metaUrl(path), jsonResponse({ size }));
  }

  async #writeImage(path, bytes) {
    await this.#deletePath(path);
    for (let offset = 0; offset < bytes.byteLength; offset += PAGE_SIZE) {
      const page = new Uint8Array(PAGE_SIZE);
      page.set(bytes.subarray(offset, offset + PAGE_SIZE));
      await this.#cache.put(pageUrl(path, offset / PAGE_SIZE), bytesResponse(page));
    }

    await this.#writeSize(path, bytes.byteLength);
  }

  async #deletePath(path) {
    const keys = await this.#cache.keys();
    const prefix = `/blazor-sqlite/db/${encodeURIComponent(path)}`;
    await Promise.all(keys
      .filter(request => new URL(request.url).pathname.startsWith(prefix))
      .map(request => this.#cache.delete(request)));
  }

  async #dropPagesFrom(path, firstDropped) {
    const keys = await this.#cache.keys();
    await Promise.all(keys
      .filter(request => {
        const url = new URL(request.url);
        const match = url.pathname.match(/\/p\/(\d+)$/);
        return url.pathname.includes(`/db/${encodeURIComponent(path)}/`)
          && match
          && Number(match[1]) >= firstDropped;
      })
      .map(request => this.#cache.delete(request)));
  }
}

export function pathOf(databaseName) {
  return new URL(databaseName, 'file://').pathname;
}

export function fileNameOf(path) {
  return path.startsWith('/') ? path.slice(1) : path;
}

export function metaUrl(path) {
  return `https://blazor-sqlite.invalid/blazor-sqlite/db/${encodeURIComponent(path)}/meta`;
}

export function pageUrl(path, index) {
  return `https://blazor-sqlite.invalid/blazor-sqlite/db/${encodeURIComponent(path)}/p/${index}`;
}

export async function importBesqlIfPresent(fileName) {
  if (typeof caches === 'undefined' || !fileName) {
    return null;
  }

  try {
    const cache = await caches.open(BESQL_CACHE);
    const match = await cache.match(`/data/cache/${fileName}`);
    if (!match) {
      return null;
    }

    return new Uint8Array(await match.arrayBuffer());
  } catch {
    return null;
  }
}

function bytesResponse(bytes) {
  return new Response(bytes, { headers: { 'content-type': 'application/octet-stream' } });
}

function jsonResponse(value) {
  return new Response(JSON.stringify(value), { headers: { 'content-type': 'application/json' } });
}
