# Changelog

## 0.2.0

### Performance

- **Binary wire format.** SQL batches and results cross the worker and Blazor interop boundaries as single byte arrays, transferred rather than cloned, with no JSON, base64, or string-encoded integers. Rows are written straight from the live statement into the reply buffer.
- **Prepared-statement cache** in the worker (64 entries, LRU, reset per use, cleared on DDL).
- **Cache Storage VFS** no longer enumerates every cache key on delete and truncate - which happened on every commit in DELETE journal mode - and flushes pages in parallel.
- Parameter binding builds a lookup once per request; SQL guards skip their regular expressions unless the text mentions `pragma` or `attach`.

### Correctness

- `PRAGMA foreign_keys = ON` on every open (configurable), matching Microsoft.Data.Sqlite and the server-side EF behaviour.
- Change tracking from `sqlite3_update_hook`: live queries see triggers and cascades; reads and no-op writes raise nothing.
- Affected-row counts exclude read-only statements, so an insert followed by a select is counted once.
- Cross-tab change notifications now reach .NET (`ISqliteChangeNotifier` on the worker transport).
- Transactions roll back when disposed without a commit; nested `BeginTransaction` fails with a message pointing at savepoints.

### Features

- `AddBlazorSqlite(...)` builder, `IBlazorSqliteSessionFactory`, `BlazorSqliteDatabase` (shared session with `Closing`/`Opened`, `WhileClosedAsync`, `ExportAsync`).
- `AddBlazorSqliteDbContext<T>()` with `IBlazorSqliteDbContextFactory<T>` (migrate or ensure-created once per session, first-use hook).
- `DbBatch` support, savepoints, `CommandBehavior.SingleRow`/`SchemaOnly`, multi-result readers, `LastInsertRowId`, `BlazorSqliteConstraintException`.
- In-place export of an open database through `sqlite3_serialize` (`connection.ExportAsync()`).
- JavaScript extension modules for user-defined functions and collations; `OnOpen` statements.
- Live queries coalesce bursts of writes and report failed refreshes through `RefreshFailed`.
- `EnsureDeletedAsync` drops views as well as tables and vacuums.

### Housekeeping

- Package versions bumped to patched 10.0.13 releases; packaging metadata, symbols, and deterministic builds; GitHub Actions CI.
- Documentation under `docs/`.

## 0.1.0

Initial release.
