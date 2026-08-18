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

    private readonly IJSRuntime _js;
    private readonly WorkerSqliteTransportOptions _options;
    private IJSObjectReference? _module;
    private IJSObjectReference? _host;
    private DotNetObjectReference<WorkerSqliteTransport>? _self;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<SqliteTablesChangedEventArgs>? TablesChanged;

    /// <summary>
    /// Called from <c>blazor-sqlite-host.js</c> when another tab reports a write to this database.
    /// </summary>
    /// <remarks>
    /// Public because <c>[JSInvokable]</c> requires it. The host module has already dropped this
    /// transport's own writes and other databases' traffic, so everything arriving here is a remote
    /// write to the database this transport opened.
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
            _self = DotNetObjectReference.Create(this);
            await _module
                .InvokeVoidAsync("listen", cancellationToken, _host, _self, databaseName)
                .ConfigureAwait(false);
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

    private async Task<JsonElement> CallAsync(object request, CancellationToken cancellationToken)
    {
        var envelope = await _host!
            .InvokeAsync<JsonElement>("call", cancellationToken, request)
            .ConfigureAwait(false);

        return SqliteWireFormat.DecodeCall(envelope);
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
