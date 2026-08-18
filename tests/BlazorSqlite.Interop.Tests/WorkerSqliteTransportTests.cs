using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// The transport is the only .NET type that talks to the worker, so these tests pin the calls it
/// makes and the way it turns an envelope into either a result or a <see cref="BlazorSqliteException"/>.
/// The worker itself is covered by the browser suite; this suite does not need one.
/// </summary>
public sealed class WorkerSqliteTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Open_ImportsTheHostModule_ThenCreatesAHost_ThenCallsOpen()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": false } }""");

        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        Assert.Equal(
            ["import", "module.createHost", "module.listen", "host.call"],
            js.Calls.Select(c => c.Identifier));
        // Stamped, not bare: the version in the query is what keeps a browser from answering the
        // import with a module it cached under an earlier one.
        Assert.Equal(WorkerSqliteTransport.VersionedHostModuleUrl, js.Calls[0].Args[0]);
        Assert.StartsWith(
            WorkerSqliteTransport.DefaultHostModuleUrl + "?v=",
            WorkerSqliteTransport.VersionedHostModuleUrl,
            StringComparison.Ordinal);

        var request = OpenRequest(js);
        Assert.Equal("open", Read(request, "kind"));
        Assert.Equal("app.db", Read(request, "databaseName"));
        Assert.Equal("synchronous", Read(request, "requiredBuild"));
        Assert.Null(Read(request, "vfs"));
    }

    /// <summary>
    /// The subscription is what makes a live query re-run for another tab's write, and it has to be
    /// in place before the open so a write during startup is not missed.
    /// </summary>
    [Fact]
    public async Task Open_SubscribesToOtherTabsWrites_ForThisDatabaseOnly()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": false } }""");

        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        var listen = js.Calls.Single(c => c.Identifier == "module.listen");
        Assert.Equal("app.db", listen.Args[2]);
        Assert.IsType<DotNetObjectReference<WorkerSqliteTransport>>(listen.Args[1]);
    }

    [Fact]
    public async Task OnTablesChanged_RaisesTheTransportEvent()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": false } }""");

        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        SqliteTablesChangedEventArgs? seen = null;
        transport.TablesChanged += (_, e) => seen = e;

        transport.OnTablesChanged(["product", "customer"]);

        Assert.NotNull(seen);
        Assert.Contains("product", seen.Tables);
        Assert.Contains("customer", seen.Tables);
    }

    /// <summary>
    /// Through <see cref="ISqliteTransport"/>, not the concrete type. The interface declares
    /// <c>TablesChanged</c> with a no-op default implementation so existing transports still
    /// compile, and if the worker transport's own event failed to implement it the subscription
    /// would bind to that default and cross-tab live queries would go quiet with nothing to see.
    /// </summary>
    [Fact]
    public async Task ARemoteWrite_ReachesAConnectionThroughTheInterface()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": false } }""");

        var transport = new WorkerSqliteTransport(js, DefaultOptions());
        ISqliteTransport asInterface = transport;
        await asInterface.OpenAsync("app.db", Ct);

        await using var connection = new BlazorSqliteConnection(asInterface, "app.db");
        SqliteTablesChangedEventArgs? seen = null;
        connection.TablesChanged += (_, e) => seen = e;

        transport.OnTablesChanged(["product"]);

        Assert.NotNull(seen);
        Assert.Contains("product", seen.Tables);
    }

    [Fact]
    public async Task Open_SubscribesOnlyOnce_WhenCalledAgain()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": false } }""");
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": true } }""");

        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);
        await transport.OpenAsync("app.db", Ct);

        Assert.Single(js.Calls, c => c.Identifier == "module.listen");
    }

    [Fact]
    public async Task Open_PassesTheVfsModuleTheProviderDeclared()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "jspi", "reused": false } }""");

        var options = new WorkerSqliteTransportOptions
        {
            RequiredBuild = BlazorSqliteEngineBuild.AsyncCapable,
            Vfs = new BlazorSqliteJsModule("./_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js"),
        };

        await using var transport = new WorkerSqliteTransport(js, options);
        await transport.OpenAsync("app.db", Ct);

        var vfs = Read(OpenRequest(js), "vfs")!;
        Assert.Equal("asyncCapable", Read(OpenRequest(js), "requiredBuild"));
        Assert.Equal("./_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js", Read(vfs, "moduleUrl"));
        Assert.Equal("register", Read(vfs, "registerExport"));
    }

    [Fact]
    public async Task Execute_SendsTheEncodedBatch_AndDecodesTaggedRows()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");
        js.EnqueueEnvelope(
            """
            { "ok": true, "result": [{
              "columnNames": ["v"],
              "columnTypes": ["INTEGER"],
              "recordsAffected": 0,
              "rows": [{ "t": [1], "v": ["9007199254740993"] }]
            }] }
            """);

        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        var results = await transport.ExecuteAsync(
        [
            new SqliteCommandRequest
            {
                CommandText = "SELECT v FROM t",
                ResultKind = SqliteResultKind.Reader,
            },
        ], Ct);

        var request = js.Calls.Last(c => c.Identifier == "host.call").Args[0]!;
        Assert.Equal("execute", Read(request, "kind"));

        var row = Assert.Single(Assert.Single(results).Rows);
        Assert.Equal(9007199254740993L, Assert.Single(row));
    }

    [Fact]
    public async Task Execute_ThrowsBlazorSqliteException_WhenTheEnvelopeIsAFailure()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");
        js.EnqueueEnvelope(
            """{ "ok": false, "error": { "message": "UNIQUE constraint failed: t.id", "sqliteCode": 19 } }""");

        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        var error = await Assert.ThrowsAsync<BlazorSqliteException>(
            () => transport.ExecuteAsync(
            [
                new SqliteCommandRequest { CommandText = "INSERT INTO t (id) VALUES (1)" },
            ], Ct));

        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Contains("UNIQUE", error.Message);
    }

    [Fact]
    public async Task Execute_BeforeOpen_Throws()
    {
        await using var transport = new WorkerSqliteTransport(new ScriptedJsRuntime(), DefaultOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.ExecuteAsync([], Ct));
    }

    [Fact]
    public async Task Close_IsANoOp_WhenNothingWasOpened()
    {
        var js = new ScriptedJsRuntime();
        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());

        await transport.CloseAsync(Ct);

        Assert.Empty(js.Calls);
    }

    [Fact]
    public async Task Dispose_TerminatesTheWorker()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");

        var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);
        await transport.DisposeAsync();

        Assert.True(js.HostDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.OpenAsync("app.db", Ct));
    }

    private static WorkerSqliteTransportOptions DefaultOptions() => new()
    {
        RequiredBuild = BlazorSqliteEngineBuild.Synchronous,
    };

    private static object OpenRequest(ScriptedJsRuntime js)
        => js.Calls.First(c => c.Identifier == "host.call").Args[0]!;

    private static object? Read(object target, string property)
        => target.GetType().GetProperty(property)?.GetValue(target);
}
