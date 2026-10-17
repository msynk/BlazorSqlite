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
/// host object, and then speaks in <c>call</c> envelopes for control requests and in the binary
/// wire format for SQL - never by letting a JavaScript exception cross the boundary, because Blazor
/// would strip the SQLite result code on the way.
/// </para>
/// <para>
/// SQL batches and their replies cross as byte arrays described by <see cref="SqliteWireFormat"/>.
/// Blazor marshals byte arrays without base64 or JSON, and the host transfers the buffers to and
/// from the worker rather than copying them, so a result set is encoded once in the worker and
/// decoded once here.
/// </para>
/// </remarks>
public sealed class WorkerSqliteTransport : ISqliteTransport, ISqliteChangeNotifier, ISqliteSnapshotSource
{
    /// <summary>The RCL path the worker host is served from.</summary>
    public const string DefaultHostModuleUrl = "./_content/BlazorSqlite.Js/blazor-sqlite-host.js";

    private readonly IJSRuntime _js;
    private readonly WorkerSqliteTransportOptions _options;
    private IJSObjectReference? _module;
    private IJSObjectReference? _host;
    private DotNetObjectReference<WorkerSqliteTransport>? _self;
    private bool _disposed;

    public WorkerSqliteTransport(IJSRuntime js, WorkerSqliteTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HostModuleUrl);

        _js = js;
        _options = options;
    }

    /// <inheritdoc />
    public event EventHandler<SqliteTablesChangedEventArgs>? RemoteTablesChanged;

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

        var onOpenSql = new List<string>(_options.OnOpenSql.Count + 1);
        if (_options.EnableForeignKeys)
        {
            onOpenSql.Add("PRAGMA foreign_keys = ON");
        }

        onOpenSql.AddRange(_options.OnOpenSql);

        await CallAsync(
            new
            {
                kind = "open",
                databaseName,
                requiredBuild = EncodeBuild(_options.RequiredBuild),
                vfs = EncodeModule(_options.Vfs),
                limits = new
                {
                    supportsMultiDatabaseTransactions = _options.SupportsMultiDatabaseTransactions,
                    canChangePageSize = _options.CanChangePageSize,
                },
                onOpenSql,
                extensions = _options.ExtensionModules.Select(EncodeModule).ToArray(),
            },
            cancellationToken).ConfigureAwait(false);

        if (_self is null)
        {
            _self = DotNetObjectReference.Create(this);
            await _host
                .InvokeVoidAsync("subscribeDotNet", cancellationToken, _self, nameof(OnRemoteTablesChanged))
                .ConfigureAwait(false);
        }
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

        var reply = await _host
            .InvokeAsync<byte[]>("executeBinary", cancellationToken, SqliteWireFormat.EncodeBatch(batch))
            .ConfigureAwait(false);

        return SqliteWireFormat.DecodeReply(reply);
    }

    /// <inheritdoc />
    public async Task<byte[]> ExportAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_host is null)
        {
            throw new InvalidOperationException("The transport is not open.");
        }

        try
        {
            return await _host
                .InvokeAsync<byte[]>("export", cancellationToken, "main")
                .ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            throw new BlazorSqliteException($"The database could not be exported: {ex.Message}");
        }
    }

    /// <summary>Called by the host when another tab reports a write to this database.</summary>
    [JSInvokable]
    public void OnRemoteTablesChanged(string[] tables)
    {
        if (_disposed || tables is null || tables.Length == 0)
        {
            return;
        }

        RemoteTablesChanged?.Invoke(this, new SqliteTablesChangedEventArgs(tables));
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

        _self?.Dispose();
        _self = null;
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

    private static object? EncodeModule(BlazorSqliteJsModule? module)
        => module is null ? null : new { moduleUrl = module.ModuleUrl, registerExport = module.RegisterExport };
}
