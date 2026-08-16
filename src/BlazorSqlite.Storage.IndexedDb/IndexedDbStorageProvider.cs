using BlazorSqlite.Storage;
using Microsoft.JSInterop;

namespace BlazorSqlite.Storage.IndexedDb;

/// <summary>
/// Persists SQLite databases in IndexedDB via <c>IDBBatchAtomicVFS</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the backend that reaches browsers OPFS does not: it needs an async-capable engine
/// (JSPI, or Asyncify where JSPI is missing) and cannot change the page size. Those limits are
/// declared here and enforced generically by the core.
/// </para>
/// <para>
/// Without an <see cref="IJSRuntime"/> the probe reports unavailable rather than throwing, so
/// desktop tests and selection can move on and still explain themselves.
/// </para>
/// </remarks>
public sealed class IndexedDbStorageProvider : IBlazorSqliteStorageProvider
{
    /// <summary>The name applications use to select this backend.</summary>
    public const string ProviderName = "indexeddb";

    /// <summary>The VFS name <c>open_v2</c> must ask for after registration.</summary>
    public const string VfsName = "idb-batch-atomic";

    // Root-relative: the worker's base URL is the core package, so a leading ./ would resolve
    // under _content/BlazorSqlite/ and miss this package's assets.
    public const string VfsModuleUrl = "/_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js";

    public const string AdminModuleUrl = "/_content/BlazorSqlite.Storage.IndexedDb/idb-admin.js";

    private readonly IJSRuntime? _js;

    public IndexedDbStorageProvider(IJSRuntime? js = null)
    {
        _js = js;
        Admin = new IndexedDbStorageAdmin(js);
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
        SupportsRelaxedDurability = true,
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
                "IndexedDB can only be probed in a browser. This process has no JavaScript runtime.",
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
                .InvokeAsync<IndexedDbProbeReport>("probe", cancellationToken)
                .ConfigureAwait(false);

            var environment = report.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal);

            if (!report.Available)
            {
                return BlazorSqliteProbeResult.Unavailable(
                    report.Reason ?? "IndexedDB probe failed without a reason.",
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

    private sealed class IndexedDbProbeReport
    {
        public bool Available { get; set; }

        public string? Reason { get; set; }

        public long? QuotaBytes { get; set; }

        public long? UsageBytes { get; set; }

        public Dictionary<string, string>? Environment { get; set; }
    }
}
