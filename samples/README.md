# Shared-model sample

Same entities, `AppDbContext`, and migrations on both sides. The WASM app is two things at once:
a landing page that documents the library, and a workshop catalog that runs it - switch storage
backends, edit products / customers / orders, and inspect the SQL EF generates.

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

## The site

Two shells, one layout (`Layout/MainLayout.razor`). `/` is a landing page and runs full-bleed:
hero, install command, a stat strip fed by the database in your tab, capability grid, quick start,
backend matrix, type map, and the limits this release does not claim. Every other route is
documentation and gets the docs shell - sidebar, session bar, content - with the sidebar
collapsing to a drawer under 1024px.

| Page | What it demonstrates |
|---|---|
| Overview | Landing page, plus a live catalog and the CLR → SQLite type map |
| Catalog | Category / product grids (`decimal`, `TimeSpan`, `DateOnly?`, unique SKU) |
| Customers | GUID, `DateOnly`, VIP flag, decimal credit limit |
| Orders | Master-detail, string-backed enum, `DateTimeOffset`, restrict FK |
| SQL and live queries | `Include`, `ef_compare`, `REGEXP`, `ToQueryString()`, `sqlite_version()` |
| Storage backends | Move or recreate the database on OPFS / IndexedDB / Cache Storage / memory |
| Database files | `IBlazorSqliteStorageAdmin` list / export / import / delete |
| Limits | Sync APIs throw; WAL / `ATTACH` / `PRAGMA page_size` are guarded |

`SiteMap.cs` is the single navigation model: the sidebar and the overview's demo grid both read
from it, so adding a page cannot leave one of them behind.

Every panel ships the code that drives it. Each demo card carries a copyable,
syntax-highlighted snippet of the actual call it makes - the snippets live in
`BlazorSqlite.Samples.Client/Snippets.cs`, so they stay next to the sample they document.

## Design system

`design-system/blazorsqlite-samples/MASTER.md` records the direction (Minimalism & Swiss Style,
IBM Plex Sans + JetBrains Mono, subtle motion, standard density) and, at the end, the two points
where the shipped site departs from it and why.

The tokens are three layers, in `wwwroot/css/`:

```
css/tokens.css       primitives -> semantic roles, light and dark
css/base.css         reset, type scale, the app shell (topbar, sidebar, footer)
css/components.css   cards, callouts, tables, forms, code blocks, session bar
css/landing.css      the overview page only
```

Nothing outside `tokens.css` uses a raw hex. Every foreground/background pair clears 4.5:1 in
both themes.

The theme switch is Auto / light / dark. The stored value is the *preference*; the boot script in
`index.html` resolves it - reading `prefers-color-scheme` for Auto - and stamps the resolved value
on `<html>` before first paint, which is why the stylesheet needs one `[data-theme="dark"]` block
and no duplicated media-query table. Icons are inline SVG in `Components/Icon.razor`: a sample
that demonstrates an offline-first database should not download an icon font, and emoji are not
icons.

## The session bar

The bar under the header on every demo page is the storage switch. **Move data** exports the
SQLite image, imports it on the target, checks the header, flips the sticky binding, and deletes
the source. **Start empty** binds a fresh file. Existing data outranks configured preference
(OPFS → IndexedDB → Cache Storage → in-memory). The two sides do not sync - that is post-1.0.

If you already had `app.db` from an earlier run, migrations add the new tables. Delete from
**Database files** (or use **Start empty**) to get the seeded workshop catalog.

`tests/BlazorSqlite.EntityFrameworkCore.Tests` still proves the load-bearing claim: migrations
scaffolded against stock SQLite apply through `UseBlazorSqlite`.
