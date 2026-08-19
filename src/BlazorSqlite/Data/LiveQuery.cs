using System.Runtime.CompilerServices;

namespace BlazorSqlite.Data;

/// <summary>Re-runs a query when watched tables are written.</summary>
public sealed class LiveQuery<T> : ILiveQuery<T>
{
    private readonly BlazorSqliteConnection _connection;
    private readonly Func<CancellationToken, Task<T>> _execute;
    private readonly HashSet<string> _tables;
    private readonly List<TaskCompletionSource<T>> _waiters = [];
    private readonly Action? _onDispose;
    private readonly Lock _refreshGate = new();
    private bool _refreshing;
    private bool _refreshRequested;
    private int _disposed;

    /// <summary>How many times one notification's refresh is attempted before it is given up on.</summary>
    private const int RefreshAttempts = 2;

    public LiveQuery(
        BlazorSqliteConnection connection,
        Func<CancellationToken, Task<T>> execute,
        IEnumerable<string> tables)
        : this(connection, execute, tables, onDispose: null)
    {
    }

    /// <summary>
    /// As the public constructor, plus <paramref name="onDispose"/>: runs once when the live query
    /// is disposed, after it has stopped listening to the connection. Lets a caller that wired the
    /// query to something else - the EF entry point subscribes to the context's SaveChanges events -
    /// release that too.
    /// </summary>
    internal LiveQuery(
        BlazorSqliteConnection connection,
        Func<CancellationToken, Task<T>> execute,
        IEnumerable<string> tables,
        Action? onDispose)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(execute);

        _connection = connection;
        _execute = execute;
        _tables = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        _onDispose = onDispose;
        _connection.TablesChanged += OnTablesChanged;
    }

    /// <inheritdoc />
    public event EventHandler<T>? Changed;

    /// <inheritdoc />
    public T? Current { get; private set; }

    /// <inheritdoc />
    public async Task<T> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var snapshot = await _execute(cancellationToken).ConfigureAwait(false);
        Current = snapshot;
        Changed?.Invoke(this, snapshot);
        Publish(snapshot);
        return snapshot;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> WithCancellation([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await RefreshAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            var waiter = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiters)
            {
                _waiters.Add(waiter);
            }

            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            T snapshot;
            try
            {
                snapshot = await waiter.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            yield return snapshot;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _connection.TablesChanged -= OnTablesChanged;
        lock (_waiters)
        {
            foreach (var waiter in _waiters)
            {
                waiter.TrySetCanceled();
            }

            _waiters.Clear();
        }

        _onDispose?.Invoke();
        return ValueTask.CompletedTask;
    }

    private void OnTablesChanged(object? sender, SqliteTablesChangedEventArgs e)
    {
        if (_tables.Count > 0 && !_tables.Overlaps(e.Tables))
        {
            return;
        }

        RequestRefresh();
    }

    /// <summary>
    /// Asks for a refresh, coalescing with one already in flight.
    /// </summary>
    /// <remarks>
    /// Refreshes are single-file because they usually run against an EF <c>DbContext</c>, which
    /// rejects a second concurrent operation. Overlapping notifications - a burst of writes, or a
    /// local write and another tab's arriving together - would otherwise throw and be swallowed,
    /// leaving the query showing data that is one write out of date. A request that arrives while a
    /// refresh is running therefore sets a flag the running refresh honours before it finishes,
    /// so the last state of the database is always the state that is read.
    /// </remarks>
    private void RequestRefresh()
    {
        lock (_refreshGate)
        {
            _refreshRequested = true;
            if (_refreshing)
            {
                return;
            }

            _refreshing = true;
        }

        _ = RefreshQuietlyAsync();
    }

    private async Task RefreshQuietlyAsync()
    {
        // Yield so the write that raised TablesChanged can finish (EF SaveChanges is still
        // consuming the insert result when the in-process transport notifies).
        await Task.Yield();

        while (true)
        {
            lock (_refreshGate)
            {
                if (!_refreshRequested || _disposed != 0)
                {
                    _refreshing = false;
                    return;
                }

                _refreshRequested = false;
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await RefreshAsync().ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (attempt < RefreshAttempts - 1 && _disposed == 0)
                {
                    // Almost always the write that triggered this is still finishing and the
                    // context is busy. Give it the rest of the turn and read again.
                    await Task.Yield();
                }
                catch (Exception)
                {
                    // A failed refresh must not take down the write that triggered it.
                    break;
                }
            }
        }
    }

    private void Publish(T snapshot)
    {
        List<TaskCompletionSource<T>> waiters;
        lock (_waiters)
        {
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(snapshot);
        }
    }
}
