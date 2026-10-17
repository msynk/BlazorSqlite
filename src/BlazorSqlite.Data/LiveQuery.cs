using System.Runtime.CompilerServices;

namespace BlazorSqlite.Data;

/// <summary>Re-runs a query when watched tables are written.</summary>
/// <remarks>
/// <para>
/// Refreshes coalesce: a burst of writes while a refresh is running produces exactly one more
/// refresh after it, not one per write. Each subscriber therefore sees the latest state and the
/// database is never asked to re-run a query it is about to re-run again.
/// </para>
/// <para>
/// A refresh triggered by a write that fails is reported on <see cref="RefreshFailed"/> rather than
/// thrown - there is no caller to throw to - and never takes down the write that triggered it.
/// </para>
/// </remarks>
public sealed class LiveQuery<T> : ILiveQuery<T>
{
    private readonly BlazorSqliteConnection _connection;
    private readonly Func<CancellationToken, Task<T>> _execute;
    private readonly HashSet<string> _tables;
    private readonly List<TaskCompletionSource<T>> _waiters = [];
    private readonly object _gate = new();
    private Task? _inFlight;
    private bool _dirty;
    private int _disposed;

    public LiveQuery(
        BlazorSqliteConnection connection,
        Func<CancellationToken, Task<T>> execute,
        IEnumerable<string> tables)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(tables);

        _connection = connection;
        _execute = execute;
        _tables = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        _connection.TablesChanged += OnTablesChanged;
    }

    /// <inheritdoc />
    public event EventHandler<T>? Changed;

    /// <inheritdoc />
    public event EventHandler<Exception>? RefreshFailed;

    /// <inheritdoc />
    public T? Current { get; private set; }

    /// <summary>The tables whose writes trigger a refresh. Empty means every write does.</summary>
    public IReadOnlySet<string> Tables => _tables;

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

        lock (_gate)
        {
            if (_inFlight is not null)
            {
                // A refresh is running; one more after it is enough to observe this write too.
                _dirty = true;
                return;
            }

            _inFlight = RefreshQuietlyAsync();
        }
    }

    private async Task RefreshQuietlyAsync()
    {
        // Yield so the write that raised TablesChanged can finish (EF SaveChanges is still
        // consuming the insert result when the transport notifies).
        await Task.Yield();

        while (true)
        {
            try
            {
                if (_disposed != 0)
                {
                    return;
                }

                await RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failed refresh must not take down the write that triggered it.
                RefreshFailed?.Invoke(this, ex);
            }

            lock (_gate)
            {
                if (!_dirty)
                {
                    _inFlight = null;
                    return;
                }

                _dirty = false;
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
