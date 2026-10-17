# BlazorSqlite

SQLite in the browser for Blazor WebAssembly, with EF Core on top. The same entities and migrations run on the client as on the server. Storage is a public contract with four first-party backends: OPFS, IndexedDB, Cache Storage, and in-memory.

**Runtime:** .NET 10 / EF Core 10. **Engine:** SQLite 3.53 via [wa-sqlite](https://github.com/rhashimoto/wa-sqlite) 1.1.2 in a dedicated worker. **License:** MIT.

```csharp
// Program.cs
builder.Services.AddBlazorSqlite(sqlite => sqlite
    .AddStorageProvider<OpfsStorageProvider>()
    .AddStorageProvider<IndexedDbStorageProvider>()
    .UseStorage(s => s.Prefer("opfs").Fallback("indexeddb"))
    .AddDatabase("app.db"));

builder.Services.AddBlazorSqliteDbContext<AppDbContext>();

// A component
@inject IBlazorSqliteDbContextFactory<AppDbContext> Contexts

await using var db = await Contexts.CreateDbContextAsync();
var open = await db.Orders.Where(o => o.Status == OrderStatus.Open).ToListAsync();
```

## Why this library

- **EF Core, unmodified.** `UseBlazorSqlite` is the stock SQLite provider over a BlazorSqlite connection. Migrations scaffolded against the server apply in the browser. Decimal arithmetic, `REGEXP`, and the `EF_DECIMAL` collation are provided in the worker so query results match the server's.
- **Fast interop.** SQL batches and result sets cross the worker and Blazor boundaries as one binary buffer each, transferred rather than copied. No JSON, no base64, 64-bit integers intact. Prepared statements are cached in the worker, so EF's repeated statement texts are parsed once.
- **Honest storage.** Selection never substitutes a backend silently: existing data outranks preference, a non-persistent fallback needs an opt-in, and a failed open names every candidate and why it was rejected. Cross-backend migration verifies the copy before flipping the binding.
- **Live queries.** A write re-runs any live query that reads the table, including writes from another tab. Change tracking comes from the engine's update hook, so triggers and cascades count too.
- **Complete ADO.NET.** Commands, batches (`DbBatch`), transactions with savepoints and rollback-on-dispose, multi-result readers, typed exceptions by SQLite result code, and in-place export of an open database.

## Packages

| Package | Role |
|---|---|
| `BlazorSqlite.EntityFrameworkCore` | `UseBlazorSqlite`, `AddBlazorSqliteDbContext`, async-safe migrations |
| `BlazorSqlite.Interop` | `AddBlazorSqlite`, session factory, worker transport, selection, migration |
| `BlazorSqlite.Data` | ADO.NET surface and live queries, transport-agnostic |
| `BlazorSqlite.Js` | The worker, the engine builds, and the JavaScript API |
| `BlazorSqlite.Storage.Abstractions` | The storage provider contract |
| `BlazorSqlite.Storage.Opfs` | Persistent, synchronous engine, multi-connection |
| `BlazorSqlite.Storage.IndexedDb` | Persistent, JSPI/Asyncify, widest reach |
| `BlazorSqlite.Storage.CacheStorage` | Persistent, besql migration path |
| `BlazorSqlite.Storage.InMemory` | Volatile reference backend |
| `BlazorSqlite.Storage.ConformanceTests` | The suite every provider must pass |
| `BlazorSqlite.Testing` | In-process transport for desktop tests |

## Documentation

- [Getting started](docs/getting-started.md) - registration, contexts, raw ADO.NET, the shared database
- [Storage](docs/storage.md) - backends, selection rules, migration, admin, writing a provider
- [Live queries](docs/live-queries.md) - EF and ADO.NET live queries, cross-tab notifications
- [Architecture and performance](docs/architecture.md) - the worker, the binary wire format, the statement cache, change tracking
- [Limits](docs/limits.md) - what the browser cannot do and how the library tells you
- [Extending](docs/extending.md) - SQL functions from JavaScript, custom transports

## Sample

`samples/` is a hosted Blazor WASM app: stock `UseSqlite` on the server, `UseBlazorSqlite` in the browser, one `AppDbContext` and one migration set.

```
dotnet run --project samples/BlazorSqlite.Samples.Server
```

## Tests

Run the Microsoft.Testing.Platform executables directly rather than `dotnet test`:

```
dotnet build BlazorSqlite.slnx
./tests/BlazorSqlite.Storage.Tests/bin/Debug/net10.0/BlazorSqlite.Storage.Tests.exe
./tests/BlazorSqlite.Interop.Tests/bin/Debug/net10.0/BlazorSqlite.Interop.Tests.exe
./tests/BlazorSqlite.EntityFrameworkCore.Tests/bin/Debug/net10.0/BlazorSqlite.EntityFrameworkCore.Tests.exe
node --test tests/BlazorSqlite.Js.Tests/*.test.js
```

Browser tests run against real engines with Playwright, using installed Chrome and Edge by default:

```
cd tests/BlazorSqlite.Browser.Tests
npm ci
npx playwright test --project=chrome
```

Set `BLAZORSQLITE_BROWSERS=all` after `npx playwright install` to add Firefox and WebKit, which exercise the Asyncify build.

## Benchmarks

`tests/BlazorSqlite.Benchmarks` is a manual harness. Serve the browser-test server (`node tests/BlazorSqlite.Browser.Tests/server.js`) and open `/benchmarks.html`.

## Not in this release

- A synchronous tier (`SaveChanges()`, `ToList()`). The engine runs in a worker and the browser main thread cannot block on it; synchronous members throw `BlazorSqliteSynchronousApiNotSupportedException` naming the async overload.
- WAL journal mode. No web VFS can provide the shared memory it needs.
- Server synchronization. The sample's two sides share a model, not data.
