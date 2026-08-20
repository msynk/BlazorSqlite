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
        var transport = new BlazorSqliteInProcessTransport();
        await using var connection = new BlazorSqliteConnection(transport, "live.db");
        await connection.OpenAsync(Ct);
        await using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)";
        await create.ExecuteNonQueryAsync(Ct);

        var seen = new List<int>();
        await using var live = new BlazorSqliteLiveQuery<int>(
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
        await using var live = new BlazorSqliteLiveQuery<int>(
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

        await using var live = new BlazorSqliteLiveQuery<int>(
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

    /// <summary>
    /// A consumer that is busy with one item while the next refresh lands must not wait for a
    /// further write to see it - and when it catches up it gets the newest snapshot, not a stale
    /// intermediate one.
    /// </summary>
    [Fact]
    public async Task WithCancellation_CatchesUpOnRefreshesItWasNotWaitingFor()
    {
        var transport = new RemoteWriteTransport();
        await using var connection = new BlazorSqliteConnection(transport, "live-enumerate.db");
        await connection.OpenAsync(Ct);

        var runs = 0;
        await using var live = new BlazorSqliteLiveQuery<int>(
            connection,
            _ => Task.FromResult(Interlocked.Increment(ref runs)),
            ["product"]);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
#pragma warning disable xUnit1051 // The enumerator's own token is the subject: it is cancelled below.
        await using var enumerator = live.WithCancellation(cts.Token).GetAsyncEnumerator(cts.Token);
#pragma warning restore xUnit1051

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, enumerator.Current);

        // Two refreshes complete while nobody is awaiting MoveNextAsync.
        await live.RefreshAsync(Ct);
        await live.RefreshAsync(Ct);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(3, enumerator.Current);

        // Nothing new now, so the next item has to wait for the next refresh.
        var pending = enumerator.MoveNextAsync();
        Assert.False(pending.IsCompleted);

        await live.RefreshAsync(Ct);
        Assert.True(await pending);
        Assert.Equal(4, enumerator.Current);

        await cts.CancelAsync();
        Assert.False(await enumerator.MoveNextAsync());
    }

    /// <summary>A transport that can be told to speak for another tab.</summary>
    private sealed class RemoteWriteTransport : IBlazorSqliteTransport
    {
        public event EventHandler<BlazorSqliteTablesChangedEventArgs>? TablesChanged;

        public void ReportRemoteWrite(params string[] tables)
            => TablesChanged?.Invoke(this, new BlazorSqliteTablesChangedEventArgs(tables));

        public Task OpenAsync(string databaseName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BlazorSqliteCommandResult>> ExecuteAsync(
            IReadOnlyList<BlazorSqliteCommandRequest> batch,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BlazorSqliteCommandResult>>(
                [.. batch.Select(_ => new BlazorSqliteCommandResult())]);

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
