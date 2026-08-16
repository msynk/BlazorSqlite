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

    private async Task CompleteAsync(string statement, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        await _connection.ExecuteInternalAsync(statement, cancellationToken).ConfigureAwait(false);
        _completed = true;
        _connection.CurrentTransaction = null;
    }
}
