using BlazorSqlite.Storage;
using Microsoft.JSInterop;

namespace BlazorSqlite.Storage.Opfs;

/// <summary>
/// Persists SQLite databases in the origin's private file system via <c>OPFSCoopSyncVFS</c>.
/// </summary>
/// <remarks>
/// <para>
/// Chosen over AccessHandlePoolVFS because more than one connection may be open and the
/// <c>.db</c> is a real file - export is a read. Access handles are worker-only, so the engine
/// stays in a dedicated worker; probe and admin use the async file API and can run on the window.
/// </para>
/// <para>
/// Without an <see cref="IJSRuntime"/> the probe reports unavailable rather than throwing, so
/// desktop tests and selection can move on and still explain themselves.
/// </para>
/// </remarks>
public sealed class OpfsStorageProvider : IBlazorSqliteStorageProvider
{
    /// <summary>The name applications use to select this backend.</summary>
    public const string ProviderName = "opfs";

    /// <summary>The VFS name <c>open_v2</c> must ask for after registration.</summary>
    public const string VfsName = "opfs-coop-sync";

    // Root-relative: the worker's base URL is the core package, so a leading ./ would resolve
    // under _content/BlazorSqlite/ and miss this package's assets.
    public const string VfsModuleUrl = "/_content/BlazorSqlite.Storage.Opfs/opfs-vfs.js";

    public const string AdminModuleUrl = "/_content/BlazorSqlite.Storage.Opfs/opfs-admin.js";

    private readonly IJSRuntime? _js;

    public OpfsStorageProvider(IJSRuntime? js = null)
    {
        _js = js;
        Admin = new OpfsStorageAdmin(js);
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public BlazorSqliteStorageCapabilities Capabilities { get; } = new()
    {
        RequiredBuild = BlazorSqliteEngineBuild.Synchronous,
        IsPersistent = true,
        SupportsMultipleConnections = true,
        SupportsConcurrentReads = false,
        SupportsRelaxedDurability = false,
        SupportsMultiDatabaseTransactions = false,
        CanChangePageSize = true,
        SupportedContexts = BlazorSqliteExecutionContexts.DedicatedWorker,
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
                "OPFS can only be probed in a browser. This process has no JavaScript runtime.",
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
                .InvokeAsync<OpfsProbeReport>("probe", cancellationToken)
                .ConfigureAwait(false);

            var environment = report.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal);

            if (!report.Available)
            {
                return BlazorSqliteProbeResult.Unavailable(
                    report.Reason ?? "OPFS probe failed without a reason.",
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

    private sealed class OpfsProbeReport
    {
        public bool Available { get; set; }

        public string? Reason { get; set; }

        public long? QuotaBytes { get; set; }

        public long? UsageBytes { get; set; }

        public Dictionary<string, string>? Environment { get; set; }
    }
}
