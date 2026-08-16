import { CacheStorageVFS, VFS_NAME } from './cache-vfs.js';

export { VFS_NAME };

export async function register({ module, sqlite3 }) {
  const vfs = await CacheStorageVFS.create(VFS_NAME, module);
  sqlite3.vfs_register(vfs, /* makeDefault */ false);
  return { vfsName: VFS_NAME };
}
