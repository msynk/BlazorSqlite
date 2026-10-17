# Extending

## SQL functions from JavaScript

User-defined functions must live where the engine lives, which is the worker. An extension module is an ES module exporting `register`:

```js
// wwwroot/my-functions.js
export function register({ module, sqlite3, db, databaseName }) {
  sqlite3.create_function(db, 'slugify', 1, 1 /* SQLITE_UTF8 */, 0, (ctx, values) => {
    const text = sqlite3.value_text(values[0]);
    sqlite3.result(ctx, text.toLowerCase().replace(/[^a-z0-9]+/g, '-'));
  });
}
```

```csharp
builder.Services.AddBlazorSqlite(sqlite => sqlite
    // ...
    .AddExtensionModule("/my-functions.js"));
```

```csharp
modelBuilder.HasDbFunction(() => Sql.Slugify(default!)).HasName("slugify");
```

The module receives the Emscripten `module`, the wa-sqlite `sqlite3` API, the open `db` handle, and the database name - the same handles the built-in `ef_*` set uses (`blazor-sqlite-functions.js` is a worked example, including aggregates and a collation). The URL must be importable from the worker: root-relative paths such as `/my-functions.js` or `/_content/MyPackage/functions.js`.

Functions cannot call back into .NET: the worker has no access to the Blazor runtime, and a synchronous SQL callback could not await it if it did.

## Open-time statements

```csharp
.OnOpen("PRAGMA cache_size = -16000")
.OnOpen("PRAGMA temp_store = MEMORY")
```

These run after the provider's own statements and after `PRAGMA foreign_keys`, outside the SQL guards. They cannot enable WAL.

## A custom transport

`ISqliteTransport` is the seam between the ADO.NET layer and wherever the engine runs. `BlazorSqlite.Testing.InProcessSqliteTransport` runs Microsoft.Data.Sqlite in-process for desktop tests; `WorkerSqliteTransport` drives the worker. A transport that can also:

- report what it wrote - set `SqliteCommandResult.ChangedTables`; otherwise the connection infers tables from the SQL;
- hear other writers - implement `ISqliteChangeNotifier`;
- snapshot an open database - implement `ISqliteSnapshotSource`.

Register it through `ISqliteTransportFactory` to replace the worker for every session, which is how the EF test suite runs the whole composition on desktop .NET.

## A custom storage backend

See [Storage](storage.md#writing-a-provider).
