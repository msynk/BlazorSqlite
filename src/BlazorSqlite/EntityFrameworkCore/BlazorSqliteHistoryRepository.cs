using Microsoft.EntityFrameworkCore.Migrations;

namespace BlazorSqlite.EntityFrameworkCore;

/// <summary>
/// A history repository whose migration lock costs no synchronous I/O.
/// </summary>
/// <remarks>
/// <para>
/// EF's SQLite history repository guards migrations with a database-level lock whose
/// <c>Dispose()</c> runs <c>ExecuteScalar</c> - synchronously, even on the <c>MigrateAsync</c> path.
/// That is the second and last blocker the S1 audit found.
/// </para>
/// <para>
/// The lock is a no-op rather than an async rewrite, and that is a design decision: BlazorSqlite
/// serialises at the storage layer. Exactly one worker owns a database, and cross-tab coordination
/// happens through the Web Locks API, so a SQL-level advisory lock adds a round trip and a failure
/// mode while guarding against contention the architecture already prevents.
/// </para>
/// <para>
/// The history-table SQL matches EF's SQLite provider: model-driven create script with
/// <c>IF NOT EXISTS</c> spliced in, and the same rejection of conditional script helpers SQLite
/// cannot express.
/// </para>
/// </remarks>
public sealed class BlazorSqliteHistoryRepository(HistoryRepositoryDependencies dependencies)
    : HistoryRepository(dependencies)
{
    protected override string ExistsSql => $"""
        SELECT COUNT(*) FROM "sqlite_master" WHERE "name" = '{TableName}' AND "type" = 'table'
        """;

    protected override bool InterpretExistsResult(object? value)
        => value is not null && Convert.ToInt64(value) != 0L;

    /// <summary>Nothing to release, since the lock is a no-op.</summary>
    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Connection;

    /// <summary>
    /// Reuses the model-driven create script so the table and column names stay in step with EF,
    /// and only splices in <c>IF NOT EXISTS</c> - which is what SQLite offers in place of the
    /// conditional blocks other providers use.
    /// </summary>
    public override string GetCreateIfNotExistsScript()
    {
        const string createTable = "CREATE TABLE";

        var script = GetCreateScript();
        var index = script.IndexOf(createTable, StringComparison.Ordinal);

        return index < 0
            ? script
            : script.Insert(index + createTable.Length, " IF NOT EXISTS");
    }

    public override IMigrationsDatabaseLock AcquireDatabaseLock() => new NoOpLock(this);

    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IMigrationsDatabaseLock>(new NoOpLock(this));

    /// <summary>
    /// SQLite has no procedural <c>IF</c>, so EF's own provider rejects these too. They are only
    /// reached when generating idempotent SQL scripts, which is a server-side workflow.
    /// </summary>
    public override string GetBeginIfNotExistsScript(string migrationId)
        => throw new NotSupportedException(
            "SQLite does not support conditional migration scripts; generate a plain script instead.");

    public override string GetBeginIfExistsScript(string migrationId)
        => throw new NotSupportedException(
            "SQLite does not support conditional migration scripts; generate a plain script instead.");

    public override string GetEndIfScript()
        => throw new NotSupportedException(
            "SQLite does not support conditional migration scripts; generate a plain script instead.");

    private sealed class NoOpLock(IHistoryRepository historyRepository) : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository { get; } = historyRepository;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
