using BlazorSqlite.Data;

namespace BlazorSqlite.Interop;

/// <summary>
/// Resolves a storage backend, opens a transport against it, and only then records the binding.
/// </summary>
/// <remarks>
/// <para>
/// This is the composition <c>UseBlazorSqlite</c> will call. It lives here rather than in the EF
/// package so raw ADO.NET can open a database without taking a dependency on EF, and so the
/// "commit the binding only after the open succeeds" rule has one implementation.
/// </para>
/// <para>
/// <see cref="StorageMigrationMode.AutomaticOnOpen"/> copies to the preferred backend, checks the
/// image, then flips the binding. A failed copy leaves the source and the binding untouched.
/// </para>
/// </remarks>
public sealed class BlazorSqliteSessionFactory
{
    private readonly StorageProviderResolver _resolver;
    private readonly ISqliteTransportFactory _transports;
    private readonly BlazorSqliteStorageSelection _selection;
    private readonly StorageMigrator _migrator;
    private readonly IStorageBindingStore _bindings;

    public BlazorSqliteSessionFactory(
        StorageProviderResolver resolver,
        ISqliteTransportFactory transports,
        BlazorSqliteStorageSelection selection,
        IStorageBindingStore bindings,
        StorageMigrator? migrator = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(bindings);

        _resolver = resolver;
        _transports = transports;
        _selection = selection;
        _bindings = bindings;
        _migrator = migrator ?? new StorageMigrator();
    }

    /// <summary>
    /// Opens <paramref name="databaseName"/> on the backend selection chooses.
    /// </summary>
    public async Task<BlazorSqliteSession> OpenAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        var resolution = await _resolver
            .ResolveAsync(databaseName, _selection, cancellationToken)
            .ConfigureAwait(false);

        if (_selection.MigrationMode is StorageMigrationMode.AutomaticOnOpen
            && resolution.BetterProviderAvailable is { } better)
        {
            await _migrator
                .MigrateAsync(databaseName, resolution.Provider, better, _bindings, cancellationToken)
                .ConfigureAwait(false);

            resolution = await _resolver
                .ResolveAsync(databaseName, _selection, cancellationToken)
                .ConfigureAwait(false);
        }

        var transport = _transports.Create(resolution.Provider);

        try
        {
            await transport.OpenAsync(databaseName, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Only after the open: a failed open must not leave a binding claiming the database exists
        // somewhere it does not.
        await _resolver.CommitBindingAsync(resolution, cancellationToken).ConfigureAwait(false);

        var capabilities = resolution.Provider.Capabilities;
        var connection = new BlazorSqliteConnection(transport, databaseName)
        {
            RuntimeLimits = new SqliteRuntimeLimits
            {
                SupportsMultiDatabaseTransactions = capabilities.SupportsMultiDatabaseTransactions,
                CanChangePageSize = capabilities.CanChangePageSize,
            },
        };
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return new BlazorSqliteSession(connection, transport, resolution);
    }
}
