namespace BlazorSqlite.Data;

/// <summary>
/// A query that re-executes when any table it reads is written, locally or in another tab.
/// </summary>
/// <remarks>
/// Nothing runs until <see cref="RefreshAsync"/> is called once: that first call establishes
/// <see cref="Current"/>, and every later write to a watched table re-runs the query and raises
/// <see cref="Changed"/>.
/// </remarks>
public interface ILiveQuery<T> : IAsyncDisposable
{
    /// <summary>Raised with the fresh result after every re-execution, including explicit refreshes.</summary>
    event EventHandler<T>? Changed;

    /// <summary>
    /// Raised when a write-triggered re-execution fails. Explicit <see cref="RefreshAsync"/> calls
    /// throw instead.
    /// </summary>
    event EventHandler<Exception>? RefreshFailed;

    /// <summary>The most recent result, or <see langword="default"/> before the first refresh.</summary>
    T? Current { get; }

    /// <summary>Runs the query now and publishes the result.</summary>
    Task<T> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The current result followed by every later one, as an asynchronous stream that ends when
    /// <paramref name="cancellationToken"/> is cancelled or the query is disposed.
    /// </summary>
    IAsyncEnumerable<T> WithCancellation(CancellationToken cancellationToken);
}
