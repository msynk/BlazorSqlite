# BlazorSqlite

EF Core on SQLite-WASM for Blazor WebAssembly. The same entities and the same migrations on the
client as on the server.

This package is everything you need except a persistent backend: the EF Core integration, the
ADO.NET surface with live queries, the worker transport, the storage provider contract, a volatile
in-memory backend, and the browser assets (SQLite engine builds, worker host, JavaScript API) served
under `_content/BlazorSqlite`.

**Runtime:** .NET 10 / EF Core 10. **License:** MIT (bundles [wa-sqlite](https://github.com/rhashimoto/wa-sqlite), also MIT).

## Install

```
dotnet add package BlazorSqlite
```

Add a persistent backend — without one you get the in-memory provider, which does not survive a
reload:

| Package | Notes |
|---|---|
| `BlazorSqlite.Storage.Opfs` | Sync engine (smallest download), multi-connection, worker-only |
| `BlazorSqlite.Storage.IndexedDb` | Widest browser reach, runs anywhere, JSPI/Asyncify |
| `BlazorSqlite.Storage.CacheStorage` | Imports a database left behind by besql |

## Register

```csharp
builder.Services.AddSingleton<IStorageBindingStore>(sp =>
    new LocalStorageBindingStore(sp.GetRequiredService<IJSRuntime>()));

builder.Services.AddSingleton(sp =>
{
    var js = sp.GetRequiredService<IJSRuntime>();
    var providers = new IBlazorSqliteStorageProvider[]
    {
        new OpfsStorageProvider(js),
        new IndexedDbStorageProvider(js),
        new InMemoryStorageProvider(),
    };
    var bindings = sp.GetRequiredService<IStorageBindingStore>();

    return new BlazorSqliteSessionFactory(
        new StorageProviderResolver(providers, bindings),
        new WorkerSqliteTransportFactory(js),
        BlazorSqliteStorageSelectionBuilder.Create(s => s
            .Prefer(OpfsStorageProvider.ProviderName)
            .Fallback(IndexedDbStorageProvider.ProviderName)
            .Fallback(InMemoryStorageProvider.ProviderName)
            .AllowNonPersistentFallback()),
        bindings);
});
```

## Use

```csharp
var session = await factory.OpenAsync("app.db");
options.UseBlazorSqlite(session.Connection);
```

Selection is sticky: existing data outranks preference, so a database already written on one backend
is reopened there rather than silently starting empty somewhere else. `StorageMigrationMode.KeepExisting`
is the default; `AutomaticOnOpen` copies the image, verifies the SQLite header, then flips the binding.

## Live queries

Table-level, and they fire across tabs. A write re-runs any live query that reads that table.

```csharp
await using var live = ctx.Orders.Where(o => o.Open).AsLiveQuery();
live.Changed += (_, rows) => InvokeAsync(StateHasChanged);
```

## Async only

The browser cannot block on storage, so the synchronous ADO.NET surface throws
`BlazorSqliteSynchronousApiNotSupportedException` rather than deadlocking or lying. Use the `Async`
overloads throughout — `ToListAsync`, `SaveChangesAsync`, `MigrateAsync`.

## Links

- [Source and issues](https://github.com/msynk/BlazorSqlite)
- Writing your own storage backend: implement `IBlazorSqliteStorageProvider` and inherit the
  conformance kit from `BlazorSqlite.Testing`.
