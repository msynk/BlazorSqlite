using BlazorSqlite.Storage;

namespace BlazorSqlite.Interop;

/// <summary>
/// How a <see cref="BlazorSqliteWorkerTransport"/> opens a database in the browser.
/// </summary>
/// <remarks>
/// The transport does not resolve storage itself: selection happens first, and this carries the
/// outcome - which engine build the chosen backend needs, and which VFS module to register. That
/// keeps the worker host ignorant of preference, sticky binding, and fallback.
/// </remarks>
public sealed class BlazorSqliteWorkerTransportOptions
{
    /// <summary>
    /// The ES module that exports <c>createHost</c>. Override only when an application hosts the
    /// worker from a path other than the RCL default.
    /// </summary>
    public string HostModuleUrl { get; init; } = BlazorSqliteWorkerTransport.VersionedHostModuleUrl;

    /// <summary>
    /// The worker script the host should spawn. <see langword="null"/> uses the host module's own
    /// default, which is what production wants.
    /// </summary>
    public string? WorkerUrl { get; init; }

    /// <summary>The engine build the selected backend's VFS requires.</summary>
    public required BlazorSqliteEngineBuild RequiredBuild { get; init; }

    /// <summary>
    /// The selected backend's VFS module, or <see langword="null"/> when the engine's built-in
    /// memory VFS is enough.
    /// </summary>
    public BlazorSqliteJsModule? Vfs { get; init; }

    /// <summary>
    /// Whether the selected backend can honour <c>ATTACH</c>. Forwarded to the worker so a
    /// JavaScript caller cannot bypass the .NET guard.
    /// </summary>
    public bool SupportsMultiDatabaseTransactions { get; init; } = true;

    /// <summary>Whether the selected backend can honour <c>PRAGMA page_size=…</c>.</summary>
    public bool CanChangePageSize { get; init; } = true;
}
