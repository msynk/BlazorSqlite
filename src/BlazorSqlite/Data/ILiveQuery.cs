namespace BlazorSqlite.Data;

/// <summary>
/// A query that re-executes when any table it reads is written, locally or in another tab.
/// </summary>
public interface ILiveQuery<T> : IAsyncDisposable
{
    event EventHandler<T>? Changed;

    T? Current { get; }

    Task<T> RefreshAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<T> WithCancellation(CancellationToken cancellationToken);
}
