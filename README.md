# BlazorSqlite

EF Core on SQLite-WASM for Blazor WebAssembly. Same entities and migrations on the client as on the server. Storage is a separate public contract: InMemory, OPFS, IndexedDB, and Cache Storage.

**Runtime:** .NET 10 LTS / EF Core 10. **License:** MIT (wa-sqlite is MIT; this repo matches it).

## Packages

| Package | Role |
|---|---|
| `BlazorSqlite` | `UseBlazorSqlite(connection)`, ADO.NET + live queries, worker transport, selection and migration, the storage provider contract, the in-memory backend, and the browser assets (engine, worker, host) under `_content/BlazorSqlite` |
| `BlazorSqlite.Storage.Opfs` | Persistent, sync engine, multi-connection |
| `BlazorSqlite.Storage.IndexedDb` | Persistent, JSPI/Asyncify, widest reach |
| `BlazorSqlite.Storage.CacheStorage` | Persistent, besql migration path |
| `BlazorSqlite.Testing` | In-process transport for browser-free unit tests, plus the conformance kit every storage provider must pass |

## Sample

The sample in `samples/` is a hosted Blazor WASM app: stock `UseSqlite` on the server, `UseBlazorSqlite` in the browser, one `AppDbContext` and one migration set.

```
dotnet run --project samples/BlazorSqlite.Samples.Server
```

## Composition

```csharp
var session = await factory.OpenAsync("app.db");
options.UseBlazorSqlite(session.Connection);
```

Selection is sticky: existing data outranks preference. `StorageMigrationMode.AutomaticOnOpen` copies, checks the SQLite header, then flips the binding. `KeepExisting` is the default.

## Live queries

Table-level. A write re-runs any live query that reads that table, including across tabs.

```csharp
await using var live = ctx.Orders.Where(o => o.Open).AsLiveQuery();
live.Changed += (_, rows) => InvokeAsync(StateHasChanged);
```

## What this release does not claim

- Arch 2 (`BlazorSqlite.Strict`) - slipped post-1.0. Browsers forbid `Atomics.wait` on the main thread; a spin-wait would freeze the UI; multithreaded WASM is not Blazor's default. See `docs/implementation-plan.md` §12 M8 notes.
- Firefox/Safari CI - Playwright's CDN is geo-blocked here; Chrome/Edge use installed browsers. Set `BLAZORSQLITE_BROWSERS=all` after `playwright install`.
- Soak: 4–8 tabs, mid-commit kill × 1000, and §2 latency numbers. Those are the manual benchmark suite, not CI gates.

## Tests

Do not rely on `dotnet test` (MTP has reported “Zero tests ran”). Run the test exe:

```
./tests/BlazorSqlite.Storage.Tests/bin/Debug/net10.0/BlazorSqlite.Storage.Tests.exe
./tests/BlazorSqlite.Interop.Tests/bin/Debug/net10.0/BlazorSqlite.Interop.Tests.exe
./tests/BlazorSqlite.EntityFrameworkCore.Tests/bin/Debug/net10.0/BlazorSqlite.EntityFrameworkCore.Tests.exe
```

Browser: `npx playwright test --project=chrome` from `tests/BlazorSqlite.Browser.Tests`.

## Benchmarks

Open `tests/BlazorSqlite.Benchmarks` with the Playwright test server (or any static host that maps `/_content/...` the same way). Manual, not a CI gate.

## Packing

```
dotnet pack -c Release
```

Packages and symbols land in `artifacts/packages` (gitignored). Only the five `src/` projects are packable; tests and samples set `IsPackable=false`.

Shared metadata - licence, project and repository URLs, tags, readme wiring, `.snupkg` symbols - lives in the `Packaging` group of `Directory.Build.props`. Each package carries its own `README.md` from its project folder, which is what nuget.org renders.

Version comes from `VersionPrefix` in `Directory.Build.props`. Override per build rather than editing it for a one-off:

```
dotnet pack -c Release -p:VersionPrefix=0.2.0
dotnet pack -c Release --version-suffix preview.1     # 0.1.0-preview.1
```

Set `CI=true` on the build agent. That turns on `ContinuousIntegrationBuild`, which normalises the paths baked into the assemblies; it is deliberately off locally, where it would be wrong. Source stepping works through the SourceLink support built into the SDK - no package reference - so the repository URL and commit are stamped into the nuspec automatically.

Publishing:

```
dotnet nuget push "artifacts/packages/*.nupkg" -s https://api.nuget.org/v3/index.json -k <key>
```

The `.snupkg` files go along with them; nuget.org picks them up from the same push.
