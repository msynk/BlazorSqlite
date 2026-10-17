using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlazorSqlite.Data;

/// <summary>A command executed through the connection's transport.</summary>
public sealed class BlazorSqliteCommand : DbCommand
{
    private BlazorSqliteConnection? _connection;

    public BlazorSqliteCommand(BlazorSqliteConnection connection)
        => _connection = connection;

    public BlazorSqliteCommand()
    {
    }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    /// <summary>
    /// Accepted for ADO.NET compatibility but not enforced: the engine runs on a worker that
    /// cannot be interrupted from the main thread. A <see cref="CancellationToken"/> stops the
    /// caller waiting; the statement runs to completion in the worker.
    /// </summary>
    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new ArgumentException(
                    $"SQLite has no stored procedures or table-direct commands; only {nameof(CommandType.Text)} is supported.",
                    nameof(value));
            }
        }
    }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = (BlazorSqliteConnection?)value;
    }

    /// <summary>The parameters, typed.</summary>
    public new BlazorSqliteParameterCollection Parameters { get; } = [];

    protected override DbParameterCollection DbParameterCollection => Parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    /// <summary>
    /// The rowid of the most recent successful <c>INSERT</c> on the connection after this command
    /// last executed. Equivalent to selecting <c>last_insert_rowid()</c> without the round trip.
    /// </summary>
    public long LastInsertRowId { get; private set; }

    /// <summary>No-op: the worker cannot be interrupted. Cancel the token passed to the async call.</summary>
    public override void Cancel()
    {
    }

    /// <summary>No-op: statements are prepared and cached by the worker on first use.</summary>
    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new BlazorSqliteParameter();

    public override int ExecuteNonQuery()
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(
            nameof(ExecuteNonQuery), nameof(ExecuteNonQueryAsync));

    public override object? ExecuteScalar()
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(
            nameof(ExecuteScalar), nameof(ExecuteScalarAsync));

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(
            nameof(ExecuteReader), nameof(ExecuteReaderAsync));

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(BlazorSqliteResultKind.NonQuery, rowLimit: null, cancellationToken)
            .ConfigureAwait(false);
        return result.RecordsAffected;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(BlazorSqliteResultKind.Scalar, rowLimit: 1, cancellationToken)
            .ConfigureAwait(false);
        return result.Rows.Count > 0 && result.Rows[0].Length > 0 ? result.Rows[0][0] : null;
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        int? rowLimit = behavior.HasFlag(CommandBehavior.SchemaOnly)
            ? 0
            : behavior.HasFlag(CommandBehavior.SingleRow) ? 1 : null;

        var result = await ExecuteCoreAsync(BlazorSqliteResultKind.Reader, rowLimit, cancellationToken)
            .ConfigureAwait(false);
        return new BlazorSqliteDataReader([result], behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null);
    }

    /// <summary>Builds the transport request this command would send, without sending it.</summary>
    internal BlazorSqliteCommandRequest ToRequest(BlazorSqliteResultKind resultKind, int? rowLimit)
    {
        var parameters = Parameters.Count == 0
            ? []
            : new BlazorSqliteParameterValue[Parameters.Count];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = Parameters[i];
            parameters[i] = new BlazorSqliteParameterValue(
                parameter.ParameterName,
                BlazorSqliteParameterBinding.ToTransportValue(parameter.Value));
        }

        return new BlazorSqliteCommandRequest
        {
            CommandText = CommandText,
            Parameters = parameters,
            ResultKind = resultKind,
            RowLimit = rowLimit,
        };
    }

    private async Task<BlazorSqliteCommandResult> ExecuteCoreAsync(
        BlazorSqliteResultKind resultKind,
        int? rowLimit,
        CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("The command requires an open connection.");
        }

        var results = await _connection
            .ExecuteBatchAsync([ToRequest(resultKind, rowLimit)], cancellationToken)
            .ConfigureAwait(false);

        if (results.Count == 0)
        {
            throw new InvalidOperationException("The transport returned no result for the command.");
        }

        LastInsertRowId = _connection.LastInsertRowId;
        return results[0];
    }
}
