using BlazorSqlite.Data;
using BlazorSqlite.Testing;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

public sealed class LiveQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReRuns_WhenAWatchedTableIsWritten()
    {
        var transport = new InProcessSqliteTransport();
        await using var connection = new BlazorSqliteConnection(transport, "live.db");
        await connection.OpenAsync(Ct);
        await using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)";
        await create.ExecuteNonQueryAsync(Ct);

        var seen = new List<int>();
        await using var live = new LiveQuery<int>(
            connection,
            async ct =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM product";
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            },
            ["product"]);

        live.Changed += (_, count) => seen.Add(count);
        Assert.Equal(0, await live.RefreshAsync(Ct));

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO product (name) VALUES ('Kept')";
        await insert.ExecuteNonQueryAsync(Ct);

        await WaitUntilAsync(() => seen.Contains(1));
        Assert.Equal(1, live.Current);
    }

    /// <summary>
    /// A write from another tab reaches the application only through the transport, so the
    /// connection has to relay it. Without this the cross-tab half of a live query is JavaScript
    /// plumbing with nothing on the other end.
    /// </summary>
    [Fact]
    public async Task ReRuns_WhenTheTransportReportsAnotherTabsWrite()
    {
        var transport = new RemoteWriteTransport();
        await using var connection = new BlazorSqliteConnection(transport, "live-remote.db");
        await connection.OpenAsync(Ct);

        var runs = 0;
        await using var live = new LiveQuery<int>(
            connection,
            _ => Task.FromResult(Interlocked.Increment(ref runs)),
            ["product"]);

        Assert.Equal(1, await live.RefreshAsync(Ct));

        transport.ReportRemoteWrite("product");

        await WaitUntilAsync(() => live.Current == 2);
    }

    [Fact]
    public async Task Ignores_ARemoteWriteToAnUnwatchedTable()
    {
        var transport = new RemoteWriteTransport();
        await using var connection = new BlazorSqliteConnection(transport, "live-remote.db");
        await connection.OpenAsync(Ct);

        await using var live = new LiveQuery<int>(
            connection,
            _ => Task.FromResult(1),
            ["product"]);

        await live.RefreshAsync(Ct);
        var changed = 0;
        live.Changed += (_, _) => Interlocked.Increment(ref changed);

        transport.ReportRemoteWrite("customer");

        await Task.Delay(100, Ct);
        Assert.Equal(0, changed);
    }

    /// <summary>
    /// The connection unsubscribes on dispose; otherwise the transport - which outlives every
    /// connection made against it - keeps every one of them alive.
    /// </summary>
    [Fact]
    public async Task DisposedConnection_StopsRelayingRemoteWrites()
    {
        var transport = new RemoteWriteTransport();
        var connection = new BlazorSqliteConnection(transport, "live-remote.db");
        await connection.OpenAsync(Ct);

        var seen = 0;
        connection.TablesChanged += (_, _) => Interlocked.Increment(ref seen);
        await connection.DisposeAsync();

        transport.ReportRemoteWrite("product");

        Assert.Equal(0, seen);
    }

    /// <summary>A transport that can be told to speak for another tab.</summary>
    private sealed class RemoteWriteTransport : ISqliteTransport
    {
        public event EventHandler<SqliteTablesChangedEventArgs>? TablesChanged;

        public void ReportRemoteWrite(params string[] tables)
            => TablesChanged?.Invoke(this, new SqliteTablesChangedEventArgs(tables));

        public Task OpenAsync(string databaseName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SqliteCommandResult>> ExecuteAsync(
            IReadOnlyList<SqliteCommandRequest> batch,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SqliteCommandResult>>(
                [.. batch.Select(_ => new SqliteCommandResult())]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
