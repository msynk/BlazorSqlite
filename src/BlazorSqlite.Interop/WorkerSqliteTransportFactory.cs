using BlazorSqlite.Data;
using BlazorSqlite.Storage;
using Microsoft.JSInterop;

namespace BlazorSqlite.Interop;

/// <summary>
/// Builds a <see cref="WorkerSqliteTransport"/> from whatever backend selection chose.
/// </summary>
/// <remarks>
/// The <paramref name="defaults"/> given at construction supply everything that is not decided by
/// the provider: host and worker URLs, open-time pragmas, and extension modules. The provider
/// supplies the engine build, the VFS, and the capability limits.
/// </remarks>
public sealed class WorkerSqliteTransportFactory(IJSRuntime js, WorkerSqliteTransportDefaults? defaults = null)
    : ISqliteTransportFactory
{
    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));
    private readonly WorkerSqliteTransportDefaults _defaults = defaults ?? new WorkerSqliteTransportDefaults();

    /// <inheritdoc />
    public ISqliteTransport Create(IBlazorSqliteStorageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return new WorkerSqliteTransport(_js, new WorkerSqliteTransportOptions
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
/// The parts of <see cref="WorkerSqliteTransportOptions"/> that do not depend on which storage
/// backend was selected, applied to every transport the factory creates.
/// </summary>
public sealed class WorkerSqliteTransportDefaults
{
    /// <inheritdoc cref="WorkerSqliteTransportOptions.HostModuleUrl"/>
    public string HostModuleUrl { get; init; } = WorkerSqliteTransport.DefaultHostModuleUrl;

    /// <inheritdoc cref="WorkerSqliteTransportOptions.WorkerUrl"/>
    public string? WorkerUrl { get; init; }

    /// <inheritdoc cref="WorkerSqliteTransportOptions.EnableForeignKeys"/>
    public bool EnableForeignKeys { get; init; } = true;

    /// <inheritdoc cref="WorkerSqliteTransportOptions.OnOpenSql"/>
    public IReadOnlyList<string> OnOpenSql { get; init; } = [];

    /// <inheritdoc cref="WorkerSqliteTransportOptions.ExtensionModules"/>
    public IReadOnlyList<BlazorSqliteJsModule> ExtensionModules { get; init; } = [];
}
