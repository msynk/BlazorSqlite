using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlazorSqlite.Data;

/// <summary>
/// ADO.NET connection whose work is carried out by an <see cref="ISqliteTransport"/>.
/// </summary>
/// <remarks>
/// Synchronous ADO.NET members throw <see cref="BlazorSqliteSynchronousApiNotSupportedException"/>:
/// the transport is asynchronous and the browser main thread cannot block on it. The opt-in strict
/// tier exists to lift that restriction.
/// </remarks>
public sealed class BlazorSqliteConnection : DbConnection
{
    private readonly ISqliteTransport _transport;
    private ConnectionState _state = ConnectionState.Closed;
    private string _database;

    public BlazorSqliteConnection(ISqliteTransport transport, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        _transport = transport;
        _database = databaseName;
        ConnectionString = $"Data Source={databaseName}";
    }

    internal ISqliteTransport Transport => _transport;

    internal BlazorSqliteTransaction? CurrentTransaction { get; set; }

    /// <summary>
    /// What the selected backend can honour. Defaults to unrestricted so an in-process transport
    /// used without a provider still behaves like a full SQLite.
    /// </summary>
    public SqliteRuntimeLimits RuntimeLimits { get; init; } = SqliteRuntimeLimits.Unrestricted;

    /// <summary>
    /// Raised after a write so live queries can re-run. Table-level: every listed name was touched
    /// by the statement that just completed.
    /// </summary>
    public event EventHandler<SqliteTablesChangedEventArgs>? TablesChanged;

    /// <summary>Used by the transport when another tab reports a write.</summary>
    public void NotifyTablesChanged(IEnumerable<string> tables)
        => TablesChanged?.Invoke(this, new SqliteTablesChangedEventArgs([.. tables]));

    [AllowNull]
    public override string ConnectionString { get; set; }

    public override string Database => _database;

    public override string DataSource => _database;

    public override string ServerVersion => "3";

    public override ConnectionState State => _state;

    public override void Open()
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(nameof(Open), nameof(OpenAsync));

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open)
        {
            return;
        }

        _state = ConnectionState.Connecting;
        try
        {
            await _transport.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
            _state = ConnectionState.Open;
        }
        catch
        {
            _state = ConnectionState.Closed;
            throw;
        }
    }

    /// <summary>
    /// Marks the connection closed without touching the transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EF opens and closes a connection around every operation, while opening a SQLite database in
    /// the worker means setting up a VFS and reading page headers. Forwarding each ADO.NET close to
    /// the transport would pay that cost per query, so the transport deliberately keeps the database
    /// open and this is bookkeeping only. The transport's lifetime belongs to whoever created it,
    /// not to an individual connection.
    /// </para>
    /// <para>
    /// That also makes <see cref="Close"/> safe to leave synchronous. It is reached from
    /// <c>DbContext.Dispose()</c>, which is far too common to forbid, and blocking on an async close
    /// would deadlock the browser's single main thread - the exact failure this type exists to avoid.
    /// </para>
    /// </remarks>
    public override void Close() => _state = ConnectionState.Closed;

    public override Task CloseAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        _database = databaseName;
    }

    protected override DbCommand CreateDbCommand() => new BlazorSqliteCommand(this);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(
            nameof(BeginTransaction), nameof(BeginTransactionAsync));

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        var transaction = new BlazorSqliteTransaction(this, isolationLevel);
        await transaction.BeginAsync(cancellationToken).ConfigureAwait(false);
        CurrentTransaction = transaction;
        return transaction;
    }

    /// <summary>Runs a statement that returns nothing, bypassing command plumbing.</summary>
    internal async Task ExecuteInternalAsync(string sql, CancellationToken cancellationToken)
    {
        SqliteFeatureGuards.EnsureSupported(sql, RuntimeLimits);
        var request = new SqliteCommandRequest { CommandText = sql };
        await _transport.ExecuteAsync([request], cancellationToken).ConfigureAwait(false);
    }
}
