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

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = (BlazorSqliteConnection?)value;
    }

    protected override DbParameterCollection DbParameterCollection { get; } = new BlazorSqliteParameterCollection();

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

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
        var result = await ExecuteCoreAsync(BlazorSqliteResultKind.NonQuery, cancellationToken).ConfigureAwait(false);
        return result.RecordsAffected;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(BlazorSqliteResultKind.Scalar, cancellationToken).ConfigureAwait(false);
        return result.Rows.Count > 0 && result.Rows[0].Length > 0 ? result.Rows[0][0] : null;
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(BlazorSqliteResultKind.Reader, cancellationToken).ConfigureAwait(false);
        return new BlazorSqliteDataReader(result);
    }

    private async Task<BlazorSqliteCommandResult> ExecuteCoreAsync(
        BlazorSqliteResultKind resultKind,
        CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("The command requires an open connection.");
        }

        var parameters = new List<BlazorSqliteParameterValue>(DbParameterCollection.Count);
        foreach (BlazorSqliteParameter parameter in DbParameterCollection)
        {
            parameters.Add(new BlazorSqliteParameterValue(
                parameter.ParameterName,
                BlazorSqliteParameterBinding.ToTransportValue(parameter.Value)));
        }

        BlazorSqliteFeatureGuards.EnsureSupported(CommandText, _connection.RuntimeLimits);

        var request = new BlazorSqliteCommandRequest
        {
            CommandText = CommandText,
            Parameters = parameters,
            ResultKind = resultKind,
        };
        var results = await _connection.Transport
            .ExecuteAsync([request], cancellationToken)
            .ConfigureAwait(false);

        if (BlazorSqliteTableNames.LooksLikeWrite(CommandText))
        {
            _connection.OnCommandWrote(BlazorSqliteTableNames.Extract(CommandText));
        }

        return results[0];
    }
}
