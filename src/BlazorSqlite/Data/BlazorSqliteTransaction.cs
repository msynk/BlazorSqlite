using System.Data;
using System.Data.Common;

namespace BlazorSqlite.Data;

/// <summary>A transaction driven by explicit BEGIN/COMMIT/ROLLBACK statements on the transport.</summary>
public sealed class BlazorSqliteTransaction : DbTransaction
{
    private readonly BlazorSqliteConnection _connection;
    private bool _completed;

    internal BlazorSqliteTransaction(BlazorSqliteConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel == IsolationLevel.Unspecified
            ? IsolationLevel.Serializable
            : isolationLevel;
    }

    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection DbConnection => _connection;

    internal Task BeginAsync(CancellationToken cancellationToken)
        => _connection.ExecuteInternalAsync("BEGIN", cancellationToken);

    public override void Commit()
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(nameof(Commit), nameof(CommitAsync));

    public override void Rollback()
        => throw BlazorSqliteSynchronousApiNotSupportedException.ForMember(nameof(Rollback), nameof(RollbackAsync));

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await CompleteAsync("COMMIT", cancellationToken).ConfigureAwait(false);
    }

    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await CompleteAsync("ROLLBACK", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back a transaction nobody committed, which is what ADO.NET promises disposal does.
    /// </summary>
    /// <remarks>
    /// Without it, an <c>await using</c> block left early would return with the connection still
    /// inside its <c>BEGIN</c> - and since the transport keeps one database open for the life of
    /// the session, that open transaction would outlive the scope that started it and block every
    /// later write. A rollback that itself fails is swallowed: disposal is already the unwinding
    /// path, and the original reason for leaving the block is the more useful error.
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try
            {
                await CompleteAsync("ROLLBACK", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _completed = true;
                _connection.CurrentTransaction = null;
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CompleteAsync(string statement, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        await _connection.ExecuteInternalAsync(statement, cancellationToken).ConfigureAwait(false);
        _completed = true;
        _connection.CurrentTransaction = null;
    }
}
