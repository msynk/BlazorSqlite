using System.Runtime.CompilerServices;

namespace BlazorSqlite.Data;

/// <summary>Re-runs a query when watched tables are written.</summary>
public sealed class LiveQuery<T> : ILiveQuery<T>
{
    private readonly BlazorSqliteConnection _connection;
    private readonly Func<CancellationToken, Task<T>> _execute;
    private readonly HashSet<string> _tables;
    private readonly List<TaskCompletionSource<T>> _waiters = [];
    private int _disposed;

    public LiveQuery(
        BlazorSqliteConnection connection,
        Func<CancellationToken, Task<T>> execute,
        IEnumerable<string> tables)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(execute);

        _connection = connection;
        _execute = execute;
        _tables = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
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

        return ValueTask.CompletedTask;
    }

    private void OnTablesChanged(object? sender, SqliteTablesChangedEventArgs e)
    {
        if (_tables.Count > 0 && !_tables.Overlaps(e.Tables))
        {
            return;
        }

        _ = RefreshQuietlyAsync();
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            // Yield so the write that raised TablesChanged can finish (EF SaveChanges is
            // still consuming the insert result when the in-process transport notifies).
            await Task.Yield();
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed refresh must not take down the write that triggered it.
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
