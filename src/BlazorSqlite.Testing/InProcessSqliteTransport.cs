using System.Data;
using System.Runtime.InteropServices;
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
/// <see cref="SqliteFunctions"/>), reporting changed tables from the engine's update hook, and
/// snapshotting through <c>sqlite3_serialize</c> - so a test that passes here is a meaningful signal.
/// </remarks>
public sealed class InProcessSqliteTransport : ISqliteTransport, ISqliteSnapshotSource
{
    private readonly string _connectionString;
    private readonly bool _registerEfFunctions;
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private SqliteConnection? _connection;

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

    /// <summary>The underlying connection, for tests that need to inspect engine state directly.</summary>
    public SqliteConnection? Connection => _connection;

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

        // The same source of truth the worker uses: the engine names every table a row changed
        // in, including through triggers and cascades.
        raw.sqlite3_update_hook(
            _connection.Handle!,
            (_, _, _, table, _) => _changed.Add(table.utf8_to_string()),
            null);

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
        foreach (var request in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutedCommands.Add(request.CommandText);
            _changed.Clear();

            var result = Execute(_connection, request);

            if (SqliteTableNames.LooksLikeWrite(request.CommandText)
                && request.CommandText.Contains("create", StringComparison.OrdinalIgnoreCase)
                | request.CommandText.Contains("drop", StringComparison.OrdinalIgnoreCase)
                | request.CommandText.Contains("alter", StringComparison.OrdinalIgnoreCase))
            {
                // DDL does not reach the update hook; the worker uses the same fallback.
                _changed.UnionWith(SqliteTableNames.Extract(request.CommandText));
            }

            results.Add(result with
            {
                LastInsertRowId = raw.sqlite3_last_insert_rowid(_connection.Handle!),
                ChangedTables = new HashSet<string>(_changed, StringComparer.OrdinalIgnoreCase),
            });
        }

        return Task.FromResult<IReadOnlyList<SqliteCommandResult>>(results);
    }

    /// <inheritdoc />
    public Task<byte[]> ExportAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("The transport is not open.");
        }

        var pointer = raw.sqlite3_serialize(_connection.Handle!, "main", out var size, 0);
        if (pointer == IntPtr.Zero)
        {
            if (size == 0)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            throw new BlazorSqliteException("sqlite3_serialize could not snapshot the database.");
        }

        try
        {
            var image = new byte[size];
            Marshal.Copy(pointer, image, 0, image.Length);
            return Task.FromResult(image);
        }
        finally
        {
            raw.sqlite3_free(pointer);
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
                using (var reader = command.ExecuteReader(
                    request.RowLimit == 0 ? CommandBehavior.SchemaOnly : CommandBehavior.Default))
                {
                    return Materialize(reader, request.RowLimit ?? int.MaxValue);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.ResultKind, "Unknown result kind.");
        }
    }

    private static SqliteCommandResult Materialize(SqliteDataReader reader, int rowLimit)
    {
        var columnNames = new string[reader.FieldCount];
        var columnTypes = new string?[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columnNames[i] = reader.GetName(i);
            columnTypes[i] = SafeDataTypeName(reader, i);
        }

        var rows = new List<object?[]>();
        while (rows.Count < rowLimit && reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        // Remaining statements in the text still have to run, as they do in the worker.
        while (reader.NextResult())
        {
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
