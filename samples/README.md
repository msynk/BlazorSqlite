# Shared-model sample

Same entities, `AppDbContext`, and migrations on both sides. The WASM UI is a workshop catalog
that lives in the browser: switch storage backends, edit products / customers / orders, and
inspect the SQL EF generates.

```
BlazorSqlite.Samples.Domain     entities (catalog, customers, orders)
BlazorSqlite.Samples.Data       DbContext + IEntityTypeConfiguration<T> + migrations + demo seed
BlazorSqlite.Samples.Server     stock UseSqlite("Data Source=app.db") + hosts the WASM client
BlazorSqlite.Samples.Client     Blazor WASM, BlazorSqliteSessionFactory + UseBlazorSqlite(session.Connection)
```

```
dotnet run --project samples/BlazorSqlite.Samples.Server
```

Then open http://localhost:5288.

| Page | What it demonstrates |
|---|---|
| Overview | KPIs, live catalog, CLR → SQLite type map |
| Catalog | Category / product grids (`decimal`, `TimeSpan`, `DateOnly?`, unique SKU) |
| Customers | GUID, `DateOnly`, VIP flag, decimal credit limit |
| Orders | Master-detail, string-backed enum, `DateTimeOffset`, restrict FK |
| Storage | Move or recreate the database on OPFS / IndexedDB / Cache Storage / memory |
| SQL | `Include`, `ef_compare`, `REGEXP`, `ToQueryString()`, `sqlite_version()` |
| Admin | `IBlazorSqliteStorageAdmin` list / export / import / delete |
| Limits | Sync APIs throw; WAL / `ATTACH` / `PRAGMA page_size` are guarded |

The header bar is the storage switch. **Move data** exports the SQLite image, imports it on the
target, checks the header, flips the sticky binding, and deletes the source. **Empty on this
backend** binds a fresh file. Existing data outranks configured preference (OPFS → IndexedDB →
Cache Storage → in-memory). The two sides do not sync — that is post-1.0.

If you already had `app.db` from an earlier run, migrations add the new tables. Delete from Admin
(or use **Empty on this backend**) to get the seeded workshop catalog.

`tests/BlazorSqlite.EntityFrameworkCore.Tests` still proves the load-bearing claim: migrations
scaffolded against stock SQLite apply through `UseBlazorSqlite`.
