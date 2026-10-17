using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlazorSqlite.Data;

/// <summary>
/// Several commands sent to the worker in one round trip, each with its own parameters.
/// </summary>
/// <remarks>
/// In the browser the cost that dominates is crossing the interop boundary, not executing SQL, so
/// a batch of twenty inserts costs about what one insert does. <c>ExecuteReaderAsync</c>
/// returns a reader over every command's result set, advanced with <c>NextResultAsync</c>.
/// </remarks>
public sealed class BlazorSqliteBatch : DbBatch
{
    private BlazorSqliteConnection? _connection;

    public BlazorSqliteBatch(BlazorSqliteConnection connection)
        => _connection = connection;

    public BlazorSqliteBatch()
    {
    }

    /// <summary>The commands, typed.</summary>
    public new BlazorSqliteBatchCommandCollection BatchCommands { get; } = [];

    protected override DbBatchCommandCollection DbBatchCommands => BatchCommands;

    /// <inheritdoc cref="BlazorSqliteCommand.CommandTimeout"/>
    public override int Timeout { get; set; } = 30;

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = (BlazorSqliteConnection?)value;
    }

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override void Prepare()
    {
    }

    public override Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    protected override DbBatchCommand CreateDbBatchCommand() => new BlazorSqliteBatchCommand();

    public override int ExecuteNonQuery()
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(
            nameof(ExecuteNonQuery), nameof(ExecuteNonQueryAsync));

    public override object? ExecuteScalar()
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(
            nameof(ExecuteScalar), nameof(ExecuteScalarAsync));

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(
            nameof(ExecuteReader), nameof(ExecuteReaderAsync));

    /// <summary>Runs every command and returns the sum of their affected rows.</summary>
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        var results = await ExecuteCoreAsync(BlazorSqliteResultKind.NonQuery, rowLimit: null, cancellationToken)
            .ConfigureAwait(false);

        var total = 0;
        for (var i = 0; i < results.Count; i++)
        {
            total += results[i].RecordsAffected;
            BatchCommands[i].SetRecordsAffected(results[i].RecordsAffected);
        }

        return total;
    }

    /// <summary>Runs every command and returns the first column of the first row of the first command that produced one.</summary>
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
    {
        var results = await ExecuteCoreAsync(BlazorSqliteResultKind.Reader, rowLimit: 1, cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < results.Count; i++)
        {
            BatchCommands[i].SetRecordsAffected(results[i].RecordsAffected);
            if (results[i].Rows.Count > 0 && results[i].Rows[0].Length > 0)
            {
                return results[i].Rows[0][0];
            }
        }

        return null;
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        int? rowLimit = behavior.HasFlag(CommandBehavior.SchemaOnly)
            ? 0
            : behavior.HasFlag(CommandBehavior.SingleRow) ? 1 : null;

        var results = await ExecuteCoreAsync(BlazorSqliteResultKind.Reader, rowLimit, cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < results.Count; i++)
        {
            BatchCommands[i].SetRecordsAffected(results[i].RecordsAffected);
        }

        return new BlazorSqliteDataReader(results, behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null);
    }

    private async Task<IReadOnlyList<BlazorSqliteCommandResult>> ExecuteCoreAsync(
        BlazorSqliteResultKind resultKind,
        int? rowLimit,
        CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("The batch requires an open connection.");
        }

        var requests = new BlazorSqliteCommandRequest[BatchCommands.Count];
        for (var i = 0; i < requests.Length; i++)
        {
            requests[i] = BatchCommands[i].ToRequest(resultKind, rowLimit);
        }

        var results = await _connection.ExecuteBatchAsync(requests, cancellationToken).ConfigureAwait(false);
        if (results.Count != requests.Length)
        {
            throw new InvalidOperationException(
                $"The transport returned {results.Count} result(s) for {requests.Length} command(s).");
        }

        return results;
    }
}

/// <summary>One command inside a <see cref="BlazorSqliteBatch"/>.</summary>
public sealed class BlazorSqliteBatchCommand : DbBatchCommand
{
    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new ArgumentException(
                    $"SQLite has no stored procedures or table-direct commands; only {nameof(System.Data.CommandType.Text)} is supported.",
                    nameof(value));
            }
        }
    }

    private int _recordsAffected;

    public override int RecordsAffected => _recordsAffected;

    internal void SetRecordsAffected(int value) => _recordsAffected = value;

    /// <summary>The parameters, typed.</summary>
    public new BlazorSqliteParameterCollection Parameters { get; } = [];

    protected override DbParameterCollection DbParameterCollection => Parameters;

    public override bool CanCreateParameter => true;

    public override DbParameter CreateParameter() => new BlazorSqliteParameter();

    internal BlazorSqliteCommandRequest ToRequest(BlazorSqliteResultKind resultKind, int? rowLimit)
    {
        var parameters = Parameters.Count == 0 ? [] : new BlazorSqliteParameterValue[Parameters.Count];
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
}

/// <summary>The commands of a <see cref="BlazorSqliteBatch"/>.</summary>
public sealed class BlazorSqliteBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<BlazorSqliteBatchCommand> _commands = [];

    public override int Count => _commands.Count;

    public override bool IsReadOnly => false;

    public new BlazorSqliteBatchCommand this[int index]
    {
        get => _commands[index];
        set => _commands[index] = value;
    }

    public override void Add(DbBatchCommand item) => _commands.Add(Cast(item));

    public override void Clear() => _commands.Clear();

    public override bool Contains(DbBatchCommand item) => _commands.Contains(Cast(item));

    public override void CopyTo(DbBatchCommand[] array, int arrayIndex) => ((ICollection<DbBatchCommand>)_commands.ToList<DbBatchCommand>()).CopyTo(array, arrayIndex);

    public override IEnumerator<DbBatchCommand> GetEnumerator() => _commands.GetEnumerator();

    public override int IndexOf(DbBatchCommand item) => _commands.IndexOf(Cast(item));

    public override void Insert(int index, DbBatchCommand item) => _commands.Insert(index, Cast(item));

    public override bool Remove(DbBatchCommand item) => _commands.Remove(Cast(item));

    public override void RemoveAt(int index) => _commands.RemoveAt(index);

    protected override DbBatchCommand GetBatchCommand(int index) => _commands[index];

    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand)
        => _commands[index] = Cast(batchCommand);

    private static BlazorSqliteBatchCommand Cast(DbBatchCommand command)
        => command as BlazorSqliteBatchCommand
            ?? throw new InvalidCastException(
                $"Expected {nameof(BlazorSqliteBatchCommand)} but received {command?.GetType().Name ?? "null"}.");
}
