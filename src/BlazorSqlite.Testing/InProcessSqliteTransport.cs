using System.Data;
using BlazorSqlite.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BlazorSqlite.Testing;

/// <summary>
/// An <see cref="ISqliteTransport"/> that runs SQLite in-process via Microsoft.Data.Sqlite.
/// </summary>
/// <remarks>
/// Stands in for the web-worker transport so provider behaviour can be tested on desktop .NET.
/// It mirrors the worker's responsibilities exactly - installing the EF function set (see
/// <see cref="SqliteFunctions"/>), and reporting committed writes through the same update-hook and
/// commit-hook mechanism - so a test that passes here is a meaningful signal.
/// </remarks>
public sealed class InProcessSqliteTransport : ISqliteTransport
{
    private readonly string _connectionString;
    private readonly bool _registerEfFunctions;
    private readonly HashSet<string> _writtenTables = new(StringComparer.OrdinalIgnoreCase);
    private SqliteConnection? _connection;
    private bool _committed;

    // Held so the native side never calls a delegate the collector has reclaimed.
    private delegate_update? _updateHook;
    private delegate_commit? _commitHook;

    /// <param name="connectionString">
    /// Defaults to a private in-memory database that lives as long as the connection.
    /// </param>
    /// <param name="registerEfFunctions">
    /// When <see langword="false"/>, the EF function set is deliberately omitted so tests can
    /// demonstrate what breaks without it.
    /// </param>
    public InProcessSqliteTransport(
        string? connectionString = null,
        bool registerEfFunctions = true)
    {
        _connectionString = connectionString ?? "Data Source=:memory:";
        _registerEfFunctions = registerEfFunctions;
    }

    /// <summary>Statements executed so far, in order. Useful for asserting generated SQL.</summary>
    public List<string> ExecutedCommands { get; } = [];

    /// <inheritdoc />
    public event EventHandler<SqliteTablesChangedEventArgs>? TablesChanged;

    /// <inheritdoc />
    /// <remarks>
    /// True, the way the worker transport is: writes are reported from SQLite's update hook once
    /// the transaction that made them has committed, so live queries behave on desktop exactly as
    /// they do in the browser.
    /// </remarks>
    public bool ReportsLocalWrites => true;

    public Task OpenAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            return Task.CompletedTask;
        }

        _connection = new SqliteConnection(_connectionString);
        _connection.Open();

        if (_registerEfFunctions)
        {
            SqliteFunctions.Register(_connection);
        }

        // The same two hooks the worker installs: exact table names per row change, and the
        // moment they became visible. See NotifyIfCommitted for how they combine.
        var handle = _connection.Handle!;
        _updateHook = (_, _, _, table, _) => _writtenTables.Add(table.utf8_to_string());
        _commitHook = _ =>
        {
            _committed = true;
            return raw.SQLITE_OK;
        };
        raw.sqlite3_update_hook(handle, _updateHook, null);
        raw.sqlite3_commit_hook(handle, _commitHook, null);

        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        // Closing an in-memory database would discard it, and EF opens and closes connections
        // freely around operations. Keep it alive until disposal.
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SqliteCommandResult>> ExecuteAsync(
        IReadOnlyList<SqliteCommandRequest> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (_connection is null)
        {
            throw new InvalidOperationException("The transport is not open.");
        }

        var results = new List<SqliteCommandResult>(batch.Count);
        try
        {
            foreach (var request in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExecutedCommands.Add(request.CommandText);
                results.Add(Execute(_connection, request));
            }
        }
        finally
        {
            // Also on failure: the statements before the one that failed have run, and in
            // autocommit mode they have committed.
            NotifyIfCommitted(_connection, batch);
        }

        return Task.FromResult<IReadOnlyList<SqliteCommandResult>>(results);
    }

    /// <summary>
    /// Raises <see cref="TablesChanged"/> for what the batch wrote, once it is committed - the same
    /// rule as the worker's <c>notifyIfCommitted</c>.
    /// </summary>
    /// <remarks>
    /// The update hook is exact for row changes but blind to DDL and to the truncate-optimised
    /// <c>DELETE FROM t</c>, so the statement text fills those in. Nothing is raised while a
    /// transaction is open; once the connection is back in autocommit the accumulated names go out
    /// if a commit happened, and are dropped if the transaction rolled back.
    /// </remarks>
    private void NotifyIfCommitted(SqliteConnection connection, IReadOnlyList<SqliteCommandRequest> batch)
    {
        foreach (var request in batch)
        {
            if (SqliteTableNames.LooksLikeWrite(request.CommandText))
            {
                _writtenTables.UnionWith(SqliteTableNames.Extract(request.CommandText));
            }
        }

        if (raw.sqlite3_get_autocommit(connection.Handle!) == 0)
        {
            return;
        }

        var committed = _committed;
        var tables = new HashSet<string>(_writtenTables, StringComparer.OrdinalIgnoreCase);
        _committed = false;
        _writtenTables.Clear();

        if (committed && tables.Count > 0)
        {
            TablesChanged?.Invoke(this, new SqliteTablesChangedEventArgs(tables));
        }
    }

    private static SqliteCommandResult Execute(SqliteConnection connection, SqliteCommandRequest request)
    {
        using var command = connection.CreateCommand();
        command.CommandText = request.CommandText;

        foreach (var parameter in request.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        switch (request.ResultKind)
        {
            case SqliteResultKind.NonQuery:
                return new SqliteCommandResult { RecordsAffected = command.ExecuteNonQuery() };

            case SqliteResultKind.Scalar:
            {
                var value = command.ExecuteScalar();
                return new SqliteCommandResult
                {
                    ColumnNames = ["value"],
                    ColumnTypes = [null],
                    Rows = [[value is DBNull ? null : value]],
                };
            }

            case SqliteResultKind.Reader:
                using (var reader = command.ExecuteReader())
                {
                    return Materialize(reader);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.ResultKind, "Unknown result kind.");
        }
    }

    private static SqliteCommandResult Materialize(SqliteDataReader reader)
    {
        var columnNames = new string[reader.FieldCount];
        var columnTypes = new string?[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columnNames[i] = reader.GetName(i);
            columnTypes[i] = SafeDataTypeName(reader, i);
        }

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return new SqliteCommandResult
        {
            ColumnNames = columnNames,
            ColumnTypes = columnTypes,
            Rows = rows,
            RecordsAffected = reader.RecordsAffected,
        };
    }

    private static string? SafeDataTypeName(SqliteDataReader reader, int ordinal)
    {
        // Computed columns have no declared type and Microsoft.Data.Sqlite throws rather than
        // returning null.
        try
        {
            return reader.GetDataTypeName(ordinal);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        _connection = null;
        return ValueTask.CompletedTask;
    }
}
