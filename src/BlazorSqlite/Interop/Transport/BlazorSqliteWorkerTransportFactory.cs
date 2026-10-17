using BlazorSqlite.Data;
using BlazorSqlite.Storage;
using Microsoft.JSInterop;

namespace BlazorSqlite.Interop;

/// <summary>
/// Builds a <see cref="BlazorSqliteWorkerTransport"/> from whatever backend selection chose.
/// </summary>
/// <remarks>
/// The <paramref name="defaults"/> given at construction supply everything that is not decided by
/// the provider: host and worker URLs, open-time pragmas, and extension modules. The provider
/// supplies the engine build, the VFS, and the capability limits.
/// </remarks>
public sealed class BlazorSqliteWorkerTransportFactory(IJSRuntime js, BlazorSqliteWorkerTransportDefaults? defaults = null)
    : IBlazorSqliteTransportFactory
{
    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));
    private readonly BlazorSqliteWorkerTransportDefaults _defaults = defaults ?? new BlazorSqliteWorkerTransportDefaults();

    /// <inheritdoc />
    public IBlazorSqliteTransport Create(IBlazorSqliteStorageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return new BlazorSqliteWorkerTransport(_js, new BlazorSqliteWorkerTransportOptions
        {
            HostModuleUrl = _defaults.HostModuleUrl,
            WorkerUrl = _defaults.WorkerUrl,
            RequiredBuild = provider.Capabilities.RequiredBuild,
            Vfs = provider.VfsModule,
            SupportsMultiDatabaseTransactions = provider.Capabilities.SupportsMultiDatabaseTransactions,
            CanChangePageSize = provider.Capabilities.CanChangePageSize,
            EnableForeignKeys = _defaults.EnableForeignKeys,
            OnOpenSql = _defaults.OnOpenSql,
            ExtensionModules = _defaults.ExtensionModules,
        });
    }
}

/// <summary>
/// The parts of <see cref="BlazorSqliteWorkerTransportOptions"/> that do not depend on which storage
/// backend was selected, applied to every transport the factory creates.
/// </summary>
public sealed class BlazorSqliteWorkerTransportDefaults
{
    /// <inheritdoc cref="BlazorSqliteWorkerTransportOptions.HostModuleUrl"/>
    public string HostModuleUrl { get; init; } = BlazorSqliteWorkerTransport.VersionedHostModuleUrl;

    /// <inheritdoc cref="BlazorSqliteWorkerTransportOptions.WorkerUrl"/>
    public string? WorkerUrl { get; init; }

    /// <inheritdoc cref="BlazorSqliteWorkerTransportOptions.EnableForeignKeys"/>
    public bool EnableForeignKeys { get; init; } = true;

    /// <inheritdoc cref="BlazorSqliteWorkerTransportOptions.OnOpenSql"/>
    public IReadOnlyList<string> OnOpenSql { get; init; } = [];

    /// <inheritdoc cref="BlazorSqliteWorkerTransportOptions.ExtensionModules"/>
    public IReadOnlyList<BlazorSqliteJsModule> ExtensionModules { get; init; } = [];
}
