using System.Reflection;
using System.Text.Json;
using BlazorSqlite.Data;
using BlazorSqlite.Storage;
using Microsoft.JSInterop;

namespace BlazorSqlite.Interop;

/// <summary>
/// An <see cref="ISqliteTransport"/> that drives the browser worker over Blazor JS interop.
/// </summary>
/// <remarks>
/// <para>
/// The worker is the process; this type is the socket. It imports the host module, asks it for a
/// host object, and then speaks only in <c>call</c> envelopes - never by letting a JavaScript
/// exception cross the boundary, because Blazor would strip the SQLite result code on the way.
/// </para>
/// <para>
/// Values cross as the tagged JSON described by <see cref="SqliteWireFormat"/>. The ADO.NET layer
/// never sees that encoding, so a cheaper marshaller can replace it later without touching
/// <see cref="BlazorSqliteConnection"/>.
/// </para>
/// </remarks>
public sealed class WorkerSqliteTransport : ISqliteTransport
{
    /// <summary>The RCL path the worker host is served from.</summary>
    public const string DefaultHostModuleUrl = "./_content/BlazorSqlite/blazor-sqlite-host.js";

    /// <summary>
    /// <see cref="DefaultHostModuleUrl"/> stamped with this assembly's version, and what a
    /// transport imports unless an application says otherwise.
    /// </summary>
    /// <remarks>
    /// The module and this assembly ship as one unit, so a browser that answers the import from a
    /// copy it cached under an earlier version pairs new .NET with old JavaScript - which surfaces
    /// as a missing export rather than as the version skew it is. No application can evict another
    /// machine's module cache, so the version goes in the query instead: an upgrade then asks for a
    /// URL no cache has an entry for.
    /// </remarks>
    public static string VersionedHostModuleUrl { get; } = BuildVersionedHostModuleUrl();

    private readonly IJSRuntime _js;
    private readonly WorkerSqliteTransportOptions _options;
    private IJSObjectReference? _module;
    private IJSObjectReference? _host;
    private DotNetObjectReference<WorkerSqliteTransport>? _self;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<SqliteTablesChangedEventArgs>? TablesChanged;

    /// <inheritdoc />
    /// <remarks>
    /// True: the worker reports what it wrote from SQLite's own update hook, cascades and triggers
    /// included, and only once the transaction that wrote it has committed - which is more than
    /// the command layer could learn from the SQL text.
    /// </remarks>
    public bool ReportsLocalWrites => true;

    /// <summary>
    /// Called from <c>blazor-sqlite-host.js</c> when a committed write - this tab's or another's -
    /// touched tables in this database.
    /// </summary>
    /// <remarks>
    /// Public because <c>[JSInvokable]</c> requires it. The host module has already dropped other
    /// databases' traffic, so everything arriving here concerns the database this transport opened.
    /// </remarks>
    [JSInvokable]
    public void OnTablesChanged(string[] tables)
        => TablesChanged?.Invoke(this, new SqliteTablesChangedEventArgs(tables ?? []));

    public WorkerSqliteTransport(IJSRuntime js, WorkerSqliteTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HostModuleUrl);

        _js = js;
        _options = options;
    }

    /// <inheritdoc />
    public async Task OpenAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _module ??= await _js
            .InvokeAsync<IJSObjectReference>("import", cancellationToken, _options.HostModuleUrl)
            .ConfigureAwait(false);

        _host ??= await _module
            .InvokeAsync<IJSObjectReference>("createHost", cancellationToken, _options.WorkerUrl)
            .ConfigureAwait(false);

        if (_self is null)
        {
            // Before the open, so a write another tab performs while this one is still opening is
            // not missed.
            var self = DotNetObjectReference.Create(this);
            try
            {
                await _module
                    .InvokeVoidAsync("listen", cancellationToken, _host, self, databaseName)
                    .ConfigureAwait(false);
            }
            catch (JSException ex) when (ex.Message.Contains("is not a function", StringComparison.Ordinal))
            {
                // A host module without `listen` is not one this package ever shipped, so either the
                // browser is serving a copy older than this assembly or the reference is not the
                // module at all. Interop reports only that the export is missing, which sends the
                // reader hunting through source that plainly exports it - so name what the object
                // actually is. A module namespace lists its exports; anything else does not.
                self.Dispose();
                var exports = await DescribeAsync(_module, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"'{_options.HostModuleUrl}' has no 'listen' export. The imported object " +
                    $"exposes: {exports}. If 'listen' is missing from that list the browser served a " +
                    "cached copy of the host module older than this assembly - clear the site's data " +
                    "and reload with Ctrl+Shift+R, and check that no service worker is serving it.",
                    ex);
            }
            catch
            {
                self.Dispose();
                throw;
            }

            _self = self;
        }

        await CallAsync(
            new
            {
                kind = "open",
                databaseName,
                requiredBuild = EncodeBuild(_options.RequiredBuild),
                vfs = EncodeVfs(_options.Vfs),
                limits = new
                {
                    supportsMultiDatabaseTransactions = _options.SupportsMultiDatabaseTransactions,
                    canChangePageSize = _options.CanChangePageSize,
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_host is null)
        {
            return;
        }

        await CallAsync(new { kind = "close" }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SqliteCommandResult>> ExecuteAsync(
        IReadOnlyList<SqliteCommandRequest> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_host is null)
        {
            throw new InvalidOperationException("The transport is not open.");
        }

        var result = await CallAsync(
            new { kind = "execute", batch = SqliteWireFormat.EncodeBatch(batch) },
            cancellationToken).ConfigureAwait(false);

        return SqliteWireFormat.DecodeResults(result);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_host is not null)
        {
            // Terminate rather than close: dispose means the worker is finished, not that EF opened
            // and closed a connection around a query. A close would leave a live worker with no
            // owner, which is a leak in a browser that charges for every one.
            try
            {
                await _host.InvokeVoidAsync("dispose").ConfigureAwait(false);
            }
            catch (JSException)
            {
                // The worker may already be gone; the JS references still need releasing.
            }

            await _host.DisposeAsync().ConfigureAwait(false);
            _host = null;
        }

        if (_module is not null)
        {
            await _module.DisposeAsync().ConfigureAwait(false);
            _module = null;
        }

        // After the host, so no notification can arrive for a reference that is already gone.
        _self?.Dispose();
        _self = null;
        TablesChanged = null;
    }

    /// <summary>
    /// Names what a JS reference actually is, for the one error where that is the whole question.
    /// </summary>
    private async Task<string> DescribeAsync(
        IJSObjectReference reference,
        CancellationToken cancellationToken)
    {
        try
        {
            var keys = await _js
                .InvokeAsync<string[]>("Object.keys", cancellationToken, reference)
                .ConfigureAwait(false);

            return keys.Length == 0 ? "no enumerable members" : string.Join(", ", keys);
        }
        catch (JSException)
        {
            // The diagnostic must never replace the failure it is describing.
            return "unknown";
        }
    }

    private async Task<JsonElement> CallAsync(object request, CancellationToken cancellationToken)
    {
        var envelope = await _host!
            .InvokeAsync<JsonElement>("call", cancellationToken, request)
            .ConfigureAwait(false);

        return SqliteWireFormat.DecodeCall(envelope);
    }

    private static string BuildVersionedHostModuleUrl()
    {
        var assembly = typeof(WorkerSqliteTransport).Assembly;

        // The informational version carries the commit for a CI build, which is a finer cache key
        // than the three-part version a series of prereleases would share.
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString();

        return string.IsNullOrWhiteSpace(version)
            ? DefaultHostModuleUrl
            : $"{DefaultHostModuleUrl}?v={Uri.EscapeDataString(version)}";
    }

    private static string EncodeBuild(BlazorSqliteEngineBuild build) => build switch
    {
        BlazorSqliteEngineBuild.Synchronous => "synchronous",
        BlazorSqliteEngineBuild.AsyncCapable => "asyncCapable",
        _ => throw new ArgumentOutOfRangeException(nameof(build), build, "Unknown engine build."),
    };

    private static object? EncodeVfs(BlazorSqliteJsModule? vfs)
        => vfs is null ? null : new { moduleUrl = vfs.ModuleUrl, registerExport = vfs.RegisterExport };
}
