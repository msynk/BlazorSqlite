using BlazorSqlite.Storage;
using Microsoft.JSInterop;

namespace BlazorSqlite.Storage.CacheStorage;

/// <summary>
/// Persists SQLite databases in the Cache Storage API as 4096-byte pages.
/// </summary>
/// <remarks>
/// Built against the frozen storage contract with no core changes. Batch-atomic writes are not
/// offered - the Cache API cannot commit several entries as one. A database left behind by besql
/// is imported losslessly on first open.
/// </remarks>
public sealed class CacheStorageProvider : IBlazorSqliteStorageProvider
{
    public const string ProviderName = "cache-storage";

    public const string VfsName = "cache-storage";

    /// <summary>
    /// The VFS module, named the way every Blazor static asset is named - relative to the
    /// document - so an application served under a sub-path still finds it. The worker host
    /// resolves it against the document base before the worker imports it.
    /// </summary>
    public const string VfsModuleUrl = "./_content/BlazorSqlite.Storage.CacheStorage/cache-register.js";

    /// <summary>The admin module, imported on the main thread relative to the document.</summary>
    public const string AdminModuleUrl = "./_content/BlazorSqlite.Storage.CacheStorage/cache-admin.js";

    private readonly IJSRuntime? _js;

    public CacheStorageProvider(IJSRuntime? js = null)
    {
        _js = js;
        Admin = new CacheStorageAdmin(js);
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public BlazorSqliteStorageCapabilities Capabilities { get; } = new()
    {
        RequiredBuild = BlazorSqliteEngineBuild.AsyncCapable,
        IsPersistent = true,
        SupportsMultipleConnections = true,
        SupportsConcurrentReads = true,
        SupportsRelaxedDurability = false,
        SupportsMultiDatabaseTransactions = true,
        CanChangePageSize = false,
        SupportedContexts =
            BlazorSqliteExecutionContexts.DedicatedWorker
            | BlazorSqliteExecutionContexts.Window
            | BlazorSqliteExecutionContexts.SharedWorker
            | BlazorSqliteExecutionContexts.ServiceWorker,
    };

    /// <inheritdoc />
    public BlazorSqliteJsModule VfsModule { get; } = new(VfsModuleUrl);

    /// <inheritdoc />
    public IBlazorSqliteStorageAdmin Admin { get; }

    /// <inheritdoc />
    public async ValueTask<BlazorSqliteProbeResult> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        if (_js is null)
        {
            return BlazorSqliteProbeResult.Unavailable(
                "Cache Storage can only be probed in a browser. This process has no JavaScript runtime.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["javascript"] = "false",
                });
        }

        var module = await _js
            .InvokeAsync<IJSObjectReference>("import", cancellationToken, AdminModuleUrl)
            .ConfigureAwait(false);

        try
        {
            var report = await module
                .InvokeAsync<CacheProbeReport>("probe", cancellationToken)
                .ConfigureAwait(false);

            var environment = report.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal);

            if (!report.Available)
            {
                return BlazorSqliteProbeResult.Unavailable(
                    report.Reason ?? "Cache Storage probe failed without a reason.",
                    environment);
            }

            return BlazorSqliteProbeResult.Available(
                report.QuotaBytes,
                report.UsageBytes,
                environment);
        }
        finally
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class CacheProbeReport
    {
        public bool Available { get; set; }

        public string? Reason { get; set; }

        public long? QuotaBytes { get; set; }

        public long? UsageBytes { get; set; }

        public Dictionary<string, string>? Environment { get; set; }
    }
}
