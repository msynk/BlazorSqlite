// Registers wa-sqlite's OPFSCoopSyncVFS against the engine the worker just loaded.
//
// Chosen over AccessHandlePoolVFS because it allows more than one connection and leaves a real
// inspectable file in OPFS - export is a read, not a reconstruction. The VFS itself is vendored
// and import-rewritten; this file is the stable register() the .NET provider points at.

import { OPFSCoopSyncVFS } from './OPFSCoopSyncVFS.js';

export const VFS_NAME = 'opfs-coop-sync';

/**
 * @param {object} options
 * @param {object} options.module the Emscripten module
 * @param {object} options.sqlite3 the wa-sqlite JavaScript API
 * @returns {Promise<{vfsName: string}>}
 */
export async function register({ module, sqlite3 }) {
  const vfs = await OPFSCoopSyncVFS.create(VFS_NAME, module);
  sqlite3.vfs_register(vfs, /* makeDefault */ false);
  return { vfsName: VFS_NAME };
}
