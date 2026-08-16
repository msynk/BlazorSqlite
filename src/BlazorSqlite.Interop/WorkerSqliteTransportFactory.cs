using BlazorSqlite.Data;
using BlazorSqlite.Storage;
using Microsoft.JSInterop;

namespace BlazorSqlite.Interop;

/// <summary>
/// Builds a <see cref="WorkerSqliteTransport"/> from whatever backend selection chose.
/// </summary>
public sealed class WorkerSqliteTransportFactory : ISqliteTransportFactory
{
    private readonly IJSRuntime _js;
    private readonly string _hostModuleUrl;
    private readonly string? _workerUrl;

    public WorkerSqliteTransportFactory(IJSRuntime js, WorkerSqliteTransportOptions? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(js);

        _js = js;
        _hostModuleUrl = defaults?.HostModuleUrl ?? WorkerSqliteTransport.DefaultHostModuleUrl;
        _workerUrl = defaults?.WorkerUrl;
    }

    /// <inheritdoc />
    public ISqliteTransport Create(IBlazorSqliteStorageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return new WorkerSqliteTransport(_js, new WorkerSqliteTransportOptions
        {
            HostModuleUrl = _hostModuleUrl,
            WorkerUrl = _workerUrl,
            RequiredBuild = provider.Capabilities.RequiredBuild,
            Vfs = provider.VfsModule,
            SupportsMultiDatabaseTransactions = provider.Capabilities.SupportsMultiDatabaseTransactions,
            CanChangePageSize = provider.Capabilities.CanChangePageSize,
        });
    }
}
