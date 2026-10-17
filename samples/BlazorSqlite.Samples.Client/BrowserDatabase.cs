using BlazorSqlite.Samples.Data;
using BlazorSqlite;
using BlazorSqlite.Data;
using BlazorSqlite.EntityFrameworkCore;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage;

namespace BlazorSqlite.Samples.Client;

public enum StorageSwitchMode
{
    /// <summary>Copy the SQLite image, flip the sticky binding, delete the source.</summary>
    Migrate,

    /// <summary>Leave the old file (if any) and open an empty database on the target backend.</summary>
    Fresh,
}

/// <summary>
/// The sample's view of the library's shared <see cref="BlazorSqliteDatabase"/>: hands out contexts
/// and drives the storage-switching UI. Everything that is not UI plumbing lives in the library.
/// </summary>
public sealed class BrowserDatabase(
    BlazorSqliteDatabase database,
    IBlazorSqliteDbContextFactory<AppDbContext> contexts,
    IEnumerable<IBlazorSqliteStorageProvider> providers,
    IStorageBindingStore bindings,
    StorageMigrator migrator)
{
    public const string DatabaseName = "app.db";

    public BlazorSqliteDatabase Database { get; } = database;

    public IReadOnlyList<IBlazorSqliteStorageProvider> Providers { get; } = [.. providers];

    public IStorageBindingStore Bindings { get; } = bindings;

    public BlazorSqliteSession? Session => Database.Session;

    public StorageResolution? Resolution => Database.Resolution;

    public BlazorSqliteConnection? Connection => Database.Connection;

    /// <summary>Raised before the worker is torn down so pages can drop live queries and contexts.</summary>
    public event Func<Task>? BeforeSessionReset
    {
        add => Database.Closing += value;
        remove => Database.Closing -= value;
    }

    /// <summary>Raised after a session opens so pages can (re)load.</summary>
    public event Func<Task>? AfterSessionReset
    {
        add => Database.Opened += value;
        remove => Database.Opened -= value;
    }

    public async Task<BlazorSqliteSession> EnsureSessionAsync(CancellationToken cancellationToken = default)
        => await Database.OpenAsync(cancellationToken);

    public Task<AppDbContext> OpenContextAsync(CancellationToken cancellationToken = default)
        => contexts.CreateDbContextAsync(cancellationToken);

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

        var source = Resolution?.Provider ?? await ProviderFromBindingAsync(cancellationToken);

        await Database.WhileClosedAsync(async ct =>
        {
            if (mode is StorageSwitchMode.Migrate
                && source is not null
                && !string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                && await source.Admin.ExistsAsync(DatabaseName, ct))
            {
                await migrator.MigrateAsync(DatabaseName, source, target, Bindings, ct);
            }
            else
            {
                await target.Admin.DeleteAsync(DatabaseName, ct);
                await Bindings.SetProviderNameAsync(DatabaseName, target.Name, ct);
            }
        }, cancellationToken);

        await Database.OpenAsync(cancellationToken);
    }

    /// <summary>
    /// Tears the worker down so admin import/delete can replace the file without racing an open
    /// connection. The next <see cref="OpenContextAsync"/> opens a fresh session.
    /// </summary>
    public Task CloseSessionAsync() => Database.CloseAsync();

    public async Task<object?> ExecuteScalarAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        var session = await EnsureSessionAsync(cancellationToken);
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task ExecuteNonQueryAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        var session = await EnsureSessionAsync(cancellationToken);
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IBlazorSqliteStorageProvider?> ProviderFromBindingAsync(
        CancellationToken cancellationToken)
    {
        var name = await Bindings.GetProviderNameAsync(DatabaseName, cancellationToken);
        return name is null
            ? null
            : Providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
