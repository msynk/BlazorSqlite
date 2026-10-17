# Getting started

## Install

Reference `BlazorSqlite.EntityFrameworkCore` (which brings `BlazorSqlite.Interop`, `BlazorSqlite.Data`, and `BlazorSqlite.Js`) plus the storage packages you want. Every storage package is optional; register at least one.

```
dotnet add package BlazorSqlite.EntityFrameworkCore
dotnet add package BlazorSqlite.Storage.Opfs
dotnet add package BlazorSqlite.Storage.IndexedDb
```

The engine (`.wasm` and the worker) is served as static web assets under `_content/BlazorSqlite.Js/`. Nothing needs to be added to `index.html`.

## Register

```csharp
using BlazorSqlite.Storage.IndexedDb;
using BlazorSqlite.Storage.InMemory;
using BlazorSqlite.Storage.Opfs;

builder.Services.AddBlazorSqlite(sqlite => sqlite
    .AddStorageProvider<OpfsStorageProvider>()
    .AddStorageProvider<IndexedDbStorageProvider>()
    .AddStorageProvider<InMemoryStorageProvider>()
    .UseStorage(s => s
        .Prefer(OpfsStorageProvider.ProviderName)
        .Fallback(IndexedDbStorageProvider.ProviderName)
        .Fallback(InMemoryStorageProvider.ProviderName)
        .AllowNonPersistentFallback())
    .AddDatabase("app.db"));

builder.Services.AddBlazorSqliteDbContext<AppDbContext>(options =>
{
    options.Schema = BlazorSqliteSchemaStrategy.Migrate;          // the default
    options.OnFirstUse = (db, ct) => DemoData.SeedIfEmptyAsync(db, ct);
});
```

`AddBlazorSqlite` registers:

| Service | What it is |
|---|---|
| `IBlazorSqliteSessionFactory` | Resolves a backend, starts a worker, opens the database |
| `BlazorSqliteDatabase` | The one shared session for the database named in `AddDatabase` |
| `StorageProviderResolver`, `StorageMigrator`, `IStorageBindingStore` | Selection, migration, and the sticky binding (in `localStorage` by default) |
| `ISqliteTransportFactory` | The worker transport |

Builder options:

- `EnableForeignKeys(false)` - skip `PRAGMA foreign_keys = ON` on open. On by default, as with Microsoft.Data.Sqlite.
- `OnOpen("PRAGMA cache_size = -16000")` - statements to run after every open.
- `AddExtensionModule("/my-functions.js")` - JavaScript that registers SQL functions; see [Extending](extending.md).
- `UseBindingStore<T>()` - replace where sticky bindings are remembered.
- `UseWorker(hostModuleUrl, workerUrl)` - host the worker from a different path.

## Use EF Core

```csharp
@inject IBlazorSqliteDbContextFactory<AppDbContext> Contexts

await using var db = await Contexts.CreateDbContextAsync();
db.Products.Add(new Product { Name = "Widget", Price = 19.99m });
await db.SaveChangesAsync();
```

The factory opens the database on first use, runs the schema step once per session, and hands out contexts bound to the shared connection. Disposing a context does not close the database. The factory also implements `IDbContextFactory<TContext>`; its synchronous `CreateDbContext()` works once the database has been opened.

Use the async EF API throughout: `ToListAsync`, `SaveChangesAsync`, `MigrateAsync`. Synchronous members throw `BlazorSqliteSynchronousApiNotSupportedException` naming the async overload, because the engine runs in a worker the main thread cannot wait on.

### Without the factory

```csharp
var session = await sessionFactory.OpenAsync("app.db");
var options = new DbContextOptionsBuilder<AppDbContext>().UseBlazorSqlite(session).Options;
await using var db = new AppDbContext(options);
```

`UseBlazorSqlite` is `UseSqlite(connection)` plus replacements for the two EF services that would otherwise open the connection synchronously. SQL generation and type mapping are the stock provider's.

## Use ADO.NET

```csharp
@inject BlazorSqliteDatabase Database

var session = await Database.OpenAsync();
await using var command = session.Connection.CreateCommand();
command.CommandText = "SELECT name FROM product WHERE price > @min";
command.Parameters.AddWithValue("@min", 10m);

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine(reader.GetString(0));
}
```

What the connection offers beyond the basics:

- **Batches.** `connection.CreateBatch()` runs several commands in one worker round trip; `ExecuteReaderAsync` on a batch walks every result set with `NextResultAsync`.
- **Transactions.** `BeginTransactionAsync()` issues `BEGIN IMMEDIATE` (as Microsoft.Data.Sqlite does); any isolation level other than `Serializable` begins deferred. Disposing without committing rolls back. Savepoints: `SaveAsync`, `ReleaseAsync`, `RollbackAsync(name)`.
- **Counters.** `command.LastInsertRowId` and `connection.LastInsertRowId` after any execution.
- **Export.** `connection.ExportAsync()` returns a consistent SQLite file image of the open database.
- **Typed errors.** `BlazorSqliteConstraintException`, `BlazorSqliteConcurrencyException`, `BlazorSqliteQuotaExceededException`, `BlazorSqliteCorruptDatabaseException`, all deriving from `BlazorSqliteException` with `SqliteErrorCode`.

## The shared database

`BlazorSqliteDatabase` owns the session for the lifetime of the app:

```csharp
await Database.OpenAsync();                // idempotent
Database.Resolution?.Provider.Name;         // "opfs", "indexeddb", ...
await Database.CloseAsync();               // raises Closing first, then terminates the worker
await Database.WhileClosedAsync(ct => provider.Admin.ImportAsync("app.db", bytes, ct));
```

Subscribe to `Closing` to drop contexts and live queries before the worker goes away, and to `Opened` to reload afterwards. The sample's `SessionLifecycle` component is one way to wire that into pages.
