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

    /// <summary>
    /// Raised when committed writes touched tables in the open database: another tab's writes
    /// always, and this transport's own writes when <see cref="ReportsLocalWrites"/> is true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="BlazorSqliteConnection"/> subscribes to this and re-raises it as
    /// <see cref="BlazorSqliteConnection.TablesChanged"/>, which is what makes a live query re-run.
    /// A transport that reports its own writes must do so only once they are committed - a live
    /// query in another tab that re-ran on an uncommitted write would read the old data and then
    /// never hear about the commit.
    /// </para>
    /// <para>
    /// Implemented as a no-op by default: a transport with no way to hear about other writers - a
    /// test double, say - is still a complete transport, and the connection covers its local
    /// writes from the SQL it sends.
    /// </para>
    /// </remarks>
    event EventHandler<SqliteTablesChangedEventArgs>? TablesChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Whether this transport raises <see cref="TablesChanged"/> for the writes it performs itself,
    /// so the connection must not.
    /// </summary>
    /// <remarks>
    /// A transport that sits next to the engine can be exact about what changed - SQLite's update
    /// hook names every table a row landed in, including through triggers and cascades - and about
    /// when, since it sees the commit. One that cannot leaves this <see langword="false"/>, and
    /// <see cref="BlazorSqliteConnection"/> derives the tables from the SQL it sends instead.
    /// Reporting on both sides would re-run every live query twice per write.
    /// </remarks>
    bool ReportsLocalWrites => false;
}

/// <summary>A single statement plus its parameters.</summary>
public sealed record SqliteCommandRequest
{
    /// <summary>The SQL to execute. May contain multiple statements.</summary>
    public required string CommandText { get; init; }

    public IReadOnlyList<SqliteParameterValue> Parameters { get; init; } = [];

    public SqliteResultKind ResultKind { get; init; } = SqliteResultKind.NonQuery;
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

    /// <summary>Rows affected, for non-query requests.</summary>
    public int RecordsAffected { get; init; }
}
