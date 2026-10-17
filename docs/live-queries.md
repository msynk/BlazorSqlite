# Live queries

A live query re-executes when any table it reads is written, by this tab or another one.

## With EF Core

```csharp
await using var live = db.Orders
    .Include(o => o.Customer)
    .Where(o => o.Status == OrderStatus.Open)
    .AsLiveQuery();

live.Changed += (_, rows) => InvokeAsync(StateHasChanged);
live.RefreshFailed += (_, ex) => logger.LogWarning(ex, "Live query failed");
var rows = await live.RefreshAsync();   // first run; later runs are automatic
```

`AsLiveQuery()` resolves the connection from the query's context and the tables from `ToQueryString()`. Pass a `BlazorSqliteConnection` explicitly when the query's provider does not expose its context.

## With ADO.NET

```csharp
await using var live = new LiveQuery<long>(
    connection,
    async ct => Convert.ToInt64(await countCommand.ExecuteScalarAsync(ct)),
    tables: ["product"]);
```

An empty table list means every write triggers a refresh.

## Async stream

```csharp
await foreach (var rows in live.WithCancellation(cancellationToken))
{
    Render(rows);
}
```

## How changes are detected

The worker installs `sqlite3_update_hook` on every connection. Each execute reports the tables whose rows were inserted, updated, or deleted - including through triggers and `ON DELETE CASCADE` - and DDL statements add the table they name. The connection raises `TablesChanged` from that report, synchronously after the command completes.

Another tab's writes arrive over a `BroadcastChannel` named `blazor-sqlite-changes`, filtered to the same database name, and raise the same event.

Transports that cannot report changes (a custom `ISqliteTransport` without `ChangedTables`) fall back to a regular-expression scan of the SQL. That scan is deliberately coarse: it can produce extra refreshes, never a missed one.

## Coalescing

A burst of writes during a refresh produces exactly one further refresh after it, not one per write. Each `Changed` event therefore carries the latest state, and the engine is never asked to re-run a query it is about to re-run again.

## Lifetime

Dispose a live query when the component goes away, and before the session closes (`BlazorSqliteDatabase.Closing` is the signal). A disposed query unsubscribes from the connection and cancels pending stream waiters.
