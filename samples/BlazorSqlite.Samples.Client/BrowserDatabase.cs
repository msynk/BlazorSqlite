using BlazorSqlite.Samples.Data;
using BlazorSqlite;
using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage;
using Microsoft.EntityFrameworkCore;

namespace BlazorSqlite.Samples.Client;

public enum StorageSwitchMode
{
    /// <summary>Copy the SQLite image, flip the sticky binding, delete the source.</summary>
    Migrate,

    /// <summary>Leave the old file (if any) and open an empty database on the target backend.</summary>
    Fresh,
}

/// <summary>
/// Opens one browser session and hands out contexts on that connection.
/// </summary>
public sealed class BrowserDatabase(
    BlazorSqliteSessionFactory factory,
    IReadOnlyList<IBlazorSqliteStorageProvider> providers,
    IBlazorSqliteStorageBindingStore bindings) : IAsyncDisposable
{
    public const string DatabaseName = "app.db";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BlazorSqliteStorageMigrator _migrator = new();
    private BlazorSqliteSession? _session;
    private bool _migrated;

    public IReadOnlyList<IBlazorSqliteStorageProvider> Providers { get; } = providers;

    public IBlazorSqliteStorageBindingStore Bindings { get; } = bindings;

    public BlazorSqliteSession? Session => _session;

    public BlazorSqliteStorageResolution? Resolution => _session?.Resolution;

    public BlazorSqliteConnection? Connection => _session?.Connection;

    /// <summary>Raised before the worker is torn down so pages can drop live queries and contexts.</summary>
    public event Func<Task>? BeforeSessionReset;

    /// <summary>Raised after a new backend is bound so pages can reopen.</summary>
    public event Func<Task>? AfterSessionReset;

    public async Task<BlazorSqliteSession> EnsureSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session ??= await factory.OpenAsync(DatabaseName, cancellationToken).ConfigureAwait(false);
            return _session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppDbContext> OpenContextAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session ??= await factory.OpenAsync(DatabaseName, cancellationToken).ConfigureAwait(false);
            var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseBlazorSqlite(_session.Connection)
                .Options);

            if (!_migrated)
            {
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                await DemoData.SeedIfEmptyAsync(context, cancellationToken).ConfigureAwait(false);
                _migrated = true;
            }

            return context;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves or recreates <see cref="DatabaseName"/> on <paramref name="targetProviderName"/>.
    /// </summary>
    public async Task SwitchStorageAsync(
        string targetProviderName,
        StorageSwitchMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProviderName);

        var target = Providers.FirstOrDefault(p =>
            string.Equals(p.Name, targetProviderName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No registered provider named '{targetProviderName}'.");

        await RaiseAsync(BeforeSessionReset).ConfigureAwait(false);

        IBlazorSqliteStorageProvider? source;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            source = _session?.Resolution.Provider;
            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }

            _migrated = false;
        }
        finally
        {
            _gate.Release();
        }

        source ??= await ProviderFromBindingAsync(cancellationToken).ConfigureAwait(false);

        if (mode is StorageSwitchMode.Migrate
            && source is not null
            && !string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase)
            && await source.Admin.ExistsAsync(DatabaseName, cancellationToken).ConfigureAwait(false))
        {
            await _migrator
                .MigrateAsync(DatabaseName, source, target, Bindings, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await target.Admin.DeleteAsync(DatabaseName, cancellationToken).ConfigureAwait(false);
            await Bindings.SetProviderNameAsync(DatabaseName, target.Name, cancellationToken)
                .ConfigureAwait(false);
        }

        await RaiseAsync(AfterSessionReset).ConfigureAwait(false);
    }

    /// <summary>
    /// Tears the worker down so admin import/delete can replace the file without racing an open
    /// connection. The next <see cref="OpenContextAsync"/> opens a fresh session.
    /// </summary>
    public async Task CloseSessionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }

            _migrated = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tells pages the session they hold is gone and they should reopen.</summary>
    public Task NotifySessionChangedAsync() => RaiseAsync(AfterSessionReset);

    public async Task<object?> ExecuteScalarAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteNonQueryAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task<IBlazorSqliteStorageProvider?> ProviderFromBindingAsync(
        CancellationToken cancellationToken)
    {
        var name = await Bindings.GetProviderNameAsync(DatabaseName, cancellationToken).ConfigureAwait(false);
        return name is null
            ? null
            : Providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task RaiseAsync(Func<Task>? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
        {
            await handler().ConfigureAwait(false);
        }
    }
}
