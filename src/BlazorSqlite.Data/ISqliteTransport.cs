namespace BlazorSqlite.Data;

/// <summary>
/// Executes SQLite work on behalf of the ADO.NET layer. Implementations decide where the engine
/// actually lives - a web worker, an in-process engine, or a test double.
/// </summary>
/// <remarks>
/// The contract is deliberately coarse: one round trip per command batch, because in the browser
/// the dominant cost is crossing the interop boundary, not executing SQL.
/// </remarks>
public interface ISqliteTransport : IAsyncDisposable
{
    /// <summary>Opens <paramref name="databaseName"/>, creating it when absent.</summary>
    Task OpenAsync(string databaseName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the database handle. Called by whoever owns the transport, not by
    /// <see cref="BlazorSqliteConnection"/> - see its <c>Close</c> documentation for why.
    /// </summary>
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes a batch and returns one result per request, in order.</summary>
    Task<IReadOnlyList<SqliteCommandResult>> ExecuteAsync(
        IReadOnlyList<SqliteCommandRequest> batch,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A transport that can hand out a consistent snapshot of the open database as a SQLite file image
/// without closing it.
/// </summary>
/// <remarks>
/// Optional because not every transport can: the worker takes the snapshot through
/// <c>sqlite3_serialize</c>, which copies every committed page under the engine's own lock, so it
/// is safe while the database is in use.
/// </remarks>
public interface ISqliteSnapshotSource
{
    /// <summary>Reads the whole database out as a SQLite file image.</summary>
    Task<byte[]> ExportAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A transport that learns about writes made elsewhere - by another tab, another worker, or another
/// connection on the same storage - and reports which tables they touched.
/// </summary>
/// <remarks>
/// Writes made through the transport itself are reported on each
/// <see cref="SqliteCommandResult.ChangedTables"/>, synchronously and in order; this event carries
/// only the writes the transport did not perform. <see cref="BlazorSqliteConnection"/> subscribes to
/// it when the transport offers it.
/// </remarks>
public interface ISqliteChangeNotifier
{
    /// <summary>Raised when another writer reports the tables it changed.</summary>
    event EventHandler<SqliteTablesChangedEventArgs>? RemoteTablesChanged;
}

/// <summary>A single statement plus its parameters.</summary>
public sealed record SqliteCommandRequest
{
    /// <summary>The SQL to execute. May contain multiple statements.</summary>
    public required string CommandText { get; init; }

    public IReadOnlyList<SqliteParameterValue> Parameters { get; init; } = [];

    public SqliteResultKind ResultKind { get; init; } = SqliteResultKind.NonQuery;

    /// <summary>
    /// The most rows to return, or <see langword="null"/> for all of them. Zero returns the column
    /// list without stepping the statement, which is what <c>CommandBehavior.SchemaOnly</c> asks for.
    /// </summary>
    public int? RowLimit { get; init; }
}

/// <summary>How much of the result the caller intends to consume.</summary>
public enum SqliteResultKind
{
    /// <summary>Row count only.</summary>
    NonQuery,

    /// <summary>First column of the first row.</summary>
    Scalar,

    /// <summary>Full result set.</summary>
    Reader,
}

/// <summary>A parameter value in transport form.</summary>
public sealed record SqliteParameterValue(string Name, object? Value);

/// <summary>The outcome of one <see cref="SqliteCommandRequest"/>.</summary>
public sealed record SqliteCommandResult
{
    /// <summary>Column names, present when the request asked for a reader.</summary>
    public IReadOnlyList<string> ColumnNames { get; init; } = [];

    /// <summary>
    /// SQLite type per column, as reported by <see cref="BlazorSqliteDataReader.GetDataTypeName"/>.
    /// </summary>
    /// <remarks>
    /// Transports differ in what they can report, and the difference is not hidden because it is not
    /// fixable: the vendored engine is built with <c>SQLITE_OMIT_DECLTYPE</c>, so declared types do not
    /// exist in it at all and the worker reports the storage class of the first row instead. The
    /// in-process transport reports true declared types, so it is the more generous of the two here.
    /// EF Core is unaffected - it materializes from the model, not from this - but a caller reading
    /// <c>GetDataTypeName</c> directly should expect <c>TEXT</c> where a server would say
    /// <c>decimal(18,2)</c>.
    /// </remarks>
    public IReadOnlyList<string?> ColumnTypes { get; init; } = [];

    /// <summary>Rows, each of <see cref="ColumnNames"/> length.</summary>
    public IReadOnlyList<object?[]> Rows { get; init; } = [];

    /// <summary>
    /// Rows changed by the statements that write, summed. Read-only statements do not contribute,
    /// so an <c>INSERT</c> followed by a <c>SELECT</c> reports the insert alone.
    /// </summary>
    public int RecordsAffected { get; init; }

    /// <summary>The rowid of the most recent successful <c>INSERT</c> on the connection.</summary>
    public long LastInsertRowId { get; init; }

    /// <summary>
    /// Tables this request wrote to, as the engine observed them - through triggers and cascades
    /// too - or <see langword="null"/> when the transport cannot tell and the caller must infer it
    /// from the SQL.
    /// </summary>
    public IReadOnlySet<string>? ChangedTables { get; init; }
}
