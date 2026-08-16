using BlazorSqlite.Storage;

namespace BlazorSqlite.Storage.InMemory;

/// <summary>
/// Keeps databases in the engine's own memory. Available everywhere, survives nothing.
/// </summary>
/// <remarks>
/// <para>
/// Useful for tests, for demos, and as a declared last resort when persistence genuinely cannot be
/// had. Because it loses data on reload, selection refuses to fall back to it unless the application
/// opts in explicitly.
/// </para>
/// <para>
/// It is also the reference implementation of the contract: the smallest thing that passes the
/// conformance kit, and the one to read before writing a backend.
/// </para>
/// </remarks>
public sealed class InMemoryStorageProvider : IBlazorSqliteStorageProvider
{
    /// <summary>The name applications use to select this backend.</summary>
    public const string ProviderName = "in-memory";

    private readonly InMemoryStorageAdmin _admin = new();

    /// <summary>Creates the default in-memory backend, or a named instance for migration tests.</summary>
    public InMemoryStorageProvider(string? name = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? ProviderName : name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Needs no asynchronous VFS, since memory reads never suspend, which is also why it is the only
    /// backend that can run on the plain synchronous engine build unconditionally.
    /// </remarks>
    public BlazorSqliteStorageCapabilities Capabilities { get; } = new()
    {
        RequiredBuild = BlazorSqliteEngineBuild.Synchronous,
        IsPersistent = false,
        SupportsMultipleConnections = true,
        SupportsConcurrentReads = true,
        SupportsMultiDatabaseTransactions = true,
        CanChangePageSize = true,

        // Durability is meaningless without persistence: there is nothing to flush to.
        SupportsRelaxedDurability = false,

        SupportedContexts = BlazorSqliteExecutionContexts.Window
            | BlazorSqliteExecutionContexts.DedicatedWorker
            | BlazorSqliteExecutionContexts.SharedWorker
            | BlazorSqliteExecutionContexts.ServiceWorker,
    };

    /// <inheritdoc />
    /// <remarks>None: SQLite's built-in memory VFS does the work, so there is no JavaScript to load.</remarks>
    public BlazorSqliteJsModule? VfsModule => null;

    /// <inheritdoc />
    public IBlazorSqliteStorageAdmin Admin => _admin;

    /// <inheritdoc />
    /// <remarks>Always available - there is no browser capability to depend on.</remarks>
    public ValueTask<BlazorSqliteProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(BlazorSqliteProbeResult.Available(
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["persistent"] = "false",
                ["requiredEngineBuild"] = nameof(BlazorSqliteEngineBuild.Synchronous),
            }));
}
