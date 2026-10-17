using System.Data;
using System.Data.Common;
using BlazorSqlite.Data;
using BlazorSqlite.Testing;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// The ADO.NET behaviours an application relies on without thinking about them: a transaction that
/// rolls back when abandoned, savepoints, batches, multi-result readers, and parameter names with or
/// without their sigil. Run against the in-process engine, which reports changes through the same
/// update hook the worker uses.
/// </summary>
public sealed class AdoNetSurfaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(InProcessSqliteTransport Transport, BlazorSqliteConnection Connection)> OpenAsync()
    {
        var transport = new InProcessSqliteTransport();
        var connection = new BlazorSqliteConnection(transport, "ado.db");
        await connection.OpenAsync(Ct);
        await ExecAsync(connection, "CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        return (transport, connection);
    }

    private static async Task<int> ExecAsync(BlazorSqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> CountAsync(BlazorSqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM product";
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Transactions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Transaction_DisposedWithoutCommit_RollsBack()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        await using (var tx = await connection.BeginTransactionAsync(Ct))
        {
            await ExecAsync(connection, "INSERT INTO product (name) VALUES ('Ghost')");
            Assert.Equal(1, await CountAsync(connection));
        }

        Assert.Equal(0, await CountAsync(connection));

        // The connection is free for a new transaction, which is the observable proof of the rollback.
        await using var next = await connection.BeginTransactionAsync(Ct);
        await next.CommitAsync(Ct);
    }

    [Fact]
    public async Task Transaction_BeginsImmediate_ForSerializable_AndDeferredOtherwise()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        await using (var tx = await connection.BeginTransactionAsync(Ct))
        {
            await tx.RollbackAsync(Ct);
        }

        await using (var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct))
        {
            await tx.RollbackAsync(Ct);
        }

        Assert.Contains("BEGIN IMMEDIATE", transport.ExecutedCommands);
        Assert.Contains("BEGIN", transport.ExecutedCommands);
    }

    [Fact]
    public async Task Transaction_NestedBegin_IsRefusedWithAClearMessage()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        await using var tx = await connection.BeginTransactionAsync(Ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.BeginTransactionAsync(Ct));

        Assert.Contains("savepoint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Savepoints_RollBackPartOfATransaction()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        await using var tx = await connection.BeginTransactionAsync(Ct);
        Assert.True(tx.SupportsSavepoints);

        await ExecAsync(connection, "INSERT INTO product (name) VALUES ('Kept')");
        await tx.SaveAsync("before_ghost", Ct);
        await ExecAsync(connection, "INSERT INTO product (name) VALUES ('Ghost')");
        await tx.RollbackAsync("before_ghost", Ct);
        await tx.ReleaseAsync("before_ghost", Ct);
        await tx.CommitAsync(Ct);

        Assert.Equal(1, await CountAsync(connection));
    }

    [Fact]
    public async Task Transaction_CommitTwice_Throws()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        await using var tx = await connection.BeginTransactionAsync(Ct);
        await tx.CommitAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Batches and readers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Batch_RunsEveryCommandInOneRoundTrip_AndReportsPerCommandCounts()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        Assert.True(connection.CanCreateBatch);
        await using var batch = connection.CreateBatch();
        for (var i = 0; i < 3; i++)
        {
            var command = batch.CreateBatchCommand();
            command.CommandText = "INSERT INTO product (name) VALUES (@name)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = $"Item {i}";
            command.Parameters.Add(parameter);
            batch.BatchCommands.Add(command);
        }

        var before = transport.ExecutedCommands.Count;
        Assert.Equal(3, await batch.ExecuteNonQueryAsync(Ct));
        Assert.All(batch.BatchCommands, c => Assert.Equal(1, c.RecordsAffected));
        Assert.Equal(3, transport.ExecutedCommands.Count - before);
        Assert.Equal(3, await CountAsync(connection));
    }

    [Fact]
    public async Task Batch_Reader_WalksEveryResultSet()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;
        await ExecAsync(connection, "INSERT INTO product (name) VALUES ('A'), ('B')");

        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new BlazorSqliteBatchCommand { CommandText = "SELECT name FROM product ORDER BY id" });
        batch.BatchCommands.Add(new BlazorSqliteBatchCommand { CommandText = "SELECT COUNT(*) FROM product" });

        await using var reader = await batch.ExecuteReaderAsync(Ct);
        var names = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            names.Add(reader.GetString(0));
        }

        Assert.Equal(["A", "B"], names);
        Assert.True(await reader.NextResultAsync(Ct));
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync(Ct));
    }

    [Fact]
    public async Task Reader_SingleRow_LimitsTheResult()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;
        await ExecAsync(connection, "INSERT INTO product (name) VALUES ('A'), ('B'), ('C')");

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM product ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, Ct);

        Assert.True(await reader.ReadAsync(Ct));
        Assert.False(await reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task Reader_SchemaOnly_ReturnsColumnsWithoutRows()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;
        await ExecAsync(connection, "INSERT INTO product (name) VALUES ('A')");

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM product";
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, Ct);

        Assert.Equal(2, reader.FieldCount);
        Assert.Equal("name", reader.GetName(1));
        Assert.False(reader.HasRows);
    }

    [Fact]
    public async Task Reader_GetFieldType_WorksBeforeRead_FromTheColumnType()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM product";
        await using var reader = await command.ExecuteReaderAsync(Ct);

        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
        Assert.Equal(1, reader.GetOrdinal("NAME"));
    }

    [Fact]
    public async Task Command_LastInsertRowId_IsAvailableWithoutAQuery()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO product (name) VALUES ('A')";
        await command.ExecuteNonQueryAsync(Ct);
        await command.ExecuteNonQueryAsync(Ct);

        Assert.Equal(2L, command.LastInsertRowId);
        Assert.Equal(2L, connection.LastInsertRowId);
    }

    [Fact]
    public async Task Parameters_MatchWithOrWithoutTheirSigil()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO product (name) VALUES (@name)";
        command.Parameters.AddWithValue("@name", "Sigil");

        Assert.True(command.Parameters.Contains("name"));
        Assert.Same(command.Parameters["@name"], command.Parameters["name"]);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task Command_RequiresAnOpenConnection()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var connection = new BlazorSqliteConnection(transport, "closed.db");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteScalarAsync(Ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Change tracking and snapshots
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TablesChanged_ComesFromTheEngine_SoCascadesAreSeen()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;
        await ExecAsync(connection, "PRAGMA foreign_keys = ON");
        await ExecAsync(connection, "CREATE TABLE line (id INTEGER PRIMARY KEY, product_id INTEGER REFERENCES product(id) ON DELETE CASCADE)");
        await ExecAsync(connection, "INSERT INTO product (name) VALUES ('A')");
        await ExecAsync(connection, "INSERT INTO line (product_id) VALUES (1)");

        var seen = new List<IReadOnlySet<string>>();
        connection.TablesChanged += (_, e) => seen.Add(e.Tables);

        await ExecAsync(connection, "DELETE FROM product WHERE id = 1");

        var tables = Assert.Single(seen);
        Assert.Contains("product", tables);
        Assert.Contains("line", tables);
    }

    [Fact]
    public async Task TablesChanged_IsNotRaised_ForAReadOrAWriteThatChangedNothing()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        var raised = 0;
        connection.TablesChanged += (_, _) => raised++;

        await CountAsync(connection);
        await ExecAsync(connection, "DELETE FROM product WHERE id = 999");

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task RemoteChanges_ReachTheConnection_WhenTheTransportHearsThem()
    {
        var transport = new NotifyingTransport();
        await using var connection = new BlazorSqliteConnection(transport, "remote.db");
        await connection.OpenAsync(Ct);

        IReadOnlySet<string>? seen = null;
        connection.TablesChanged += (_, e) => seen = e.Tables;

        transport.Raise("Orders");

        Assert.NotNull(seen);
        Assert.Contains("orders", seen);
    }

    [Fact]
    public async Task Export_SnapshotsTheOpenDatabase()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;
        await ExecAsync(connection, "INSERT INTO product (name) VALUES ('Exported')");

        var image = await connection.ExportAsync(Ct);

        Assert.True(image.Length >= 4096);
        Assert.Equal("SQLite format 3\0"u8.ToArray(), image[..16]);
    }

    // ---------------------------------------------------------------------------------------------
    // Live queries
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task LiveQuery_CoalescesABurstOfWrites_IntoFewRefreshes()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        var refreshes = 0;
        var gate = new SemaphoreSlim(0);
        await using var live = new LiveQuery<long>(
            connection,
            async ct =>
            {
                Interlocked.Increment(ref refreshes);
                await gate.WaitAsync(ct);
                return await CountAsync(connection);
            },
            ["product"]);

        var first = live.RefreshAsync(Ct);
        gate.Release();
        await first;

        for (var i = 0; i < 10; i++)
        {
            await ExecAsync(connection, "INSERT INTO product (name) VALUES ('x')");
        }

        // One refresh is in flight (blocked on the gate); the other nine writes fold into one more.
        gate.Release(2);
        await WaitUntilAsync(() => live.Current == 10);

        Assert.Equal(3, refreshes);
    }

    [Fact]
    public async Task LiveQuery_ReportsAFailedRefresh_InsteadOfSwallowingIt()
    {
        var (transport, connection) = await OpenAsync();
        await using var _ = transport;
        await using var __ = connection;

        Exception? failure = null;
        await using var live = new LiveQuery<int>(
            connection,
            _ => throw new InvalidOperationException("boom"),
            ["product"]);
        live.RefreshFailed += (_, ex) => failure = ex;

        await ExecAsync(connection, "INSERT INTO product (name) VALUES ('x')");

        await WaitUntilAsync(() => failure is not null);
        Assert.Equal("boom", failure!.Message);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class NotifyingTransport : ISqliteTransport, ISqliteChangeNotifier
    {
        public event EventHandler<SqliteTablesChangedEventArgs>? RemoteTablesChanged;

        public void Raise(params string[] tables)
            => RemoteTablesChanged?.Invoke(this, new SqliteTablesChangedEventArgs(tables));

        public Task OpenAsync(string databaseName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SqliteCommandResult>> ExecuteAsync(
            IReadOnlyList<SqliteCommandRequest> batch,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SqliteCommandResult>>([new SqliteCommandResult()]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
