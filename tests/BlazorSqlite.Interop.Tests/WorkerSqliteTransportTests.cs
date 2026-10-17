using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// The transport is the only .NET type that talks to the worker, so these tests pin the calls it
/// makes and the way it turns a reply into either a result or a <see cref="BlazorSqliteException"/>.
/// The worker itself is covered by the browser suite; this suite does not need one.
/// </summary>
public sealed class WorkerSqliteTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Open_ImportsTheHostModule_ThenCreatesAHost_ThenCallsOpen_ThenSubscribes()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": false } }""");

        await using var transport = new BlazorSqliteWorkerTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        Assert.Equal(
            ["import", "module.createHost", "module.listen", "host.call"],
            js.Calls.Select(c => c.Identifier));
        // Stamped, not bare: the version in the query is what keeps a browser from answering the
        // import with a module it cached under an earlier one.
        Assert.Equal(BlazorSqliteWorkerTransport.VersionedHostModuleUrl, js.Calls[0].Args[0]);
        Assert.StartsWith(
            BlazorSqliteWorkerTransport.DefaultHostModuleUrl + "?v=",
            BlazorSqliteWorkerTransport.VersionedHostModuleUrl,
            StringComparison.Ordinal);

        var request = OpenRequest(js);
        Assert.Equal("open", Read(request, "kind"));
        Assert.Equal("app.db", Read(request, "databaseName"));
        Assert.Equal("synchronous", Read(request, "requiredBuild"));
        Assert.Null(Read(request, "vfs"));
        Assert.Equal(nameof(BlazorSqliteWorkerTransport.OnTablesChanged), js.Subscription!.Value.Method);
    }

    /// <summary>
    /// The subscription is what makes a live query re-run for a committed write - this tab's or
    /// another's - and it has to be in place before the open so a write during startup is not missed.
    /// </summary>
    [Fact]
    public async Task Open_SubscribesToCommittedWrites_ForThisDatabaseOnly()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": false } }""");

        await using var transport = new BlazorSqliteWorkerTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        var listen = js.Calls.Single(c => c.Identifier == "module.listen");
        Assert.Equal("app.db", listen.Args[2]);
        Assert.IsType<DotNetObjectReference<BlazorSqliteWorkerTransport>>(listen.Args[1]);

        // The worker reports this tab's own writes too, from the engine's hooks and only once
        // committed, so the command layer must not raise them again from the SQL text.
        Assert.True(transport.ReportsLocalWrites);
    }

    [Fact]
    public async Task OnTablesChanged_RaisesTheTransportEvent()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": false } }""");

        await using var transport = new BlazorSqliteWorkerTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        BlazorSqliteTablesChangedEventArgs? seen = null;
        transport.TablesChanged += (_, e) => seen = e;

        transport.OnTablesChanged(["product", "customer"]);

        Assert.NotNull(seen);
        Assert.Contains("product", seen.Tables);
        Assert.Contains("customer", seen.Tables);
    }

    /// <summary>
    /// Through <see cref="IBlazorSqliteTransport"/>, not the concrete type. The interface declares
    /// <c>TablesChanged</c> with a no-op default implementation so existing transports still
    /// compile, and if the worker transport's own event failed to implement it the subscription
    /// would bind to that default and cross-tab live queries would go quiet with nothing to see.
    /// </summary>
    [Fact]
    public async Task ARemoteWrite_ReachesAConnectionThroughTheInterface()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous", "reused": false } }""");

        var transport = new BlazorSqliteWorkerTransport(js, DefaultOptions());
        IBlazorSqliteTransport asInterface = transport;
        await asInterface.OpenAsync("app.db", Ct);

        await using var connection = new BlazorSqliteConnection(asInterface, "app.db");
        BlazorSqliteTablesChangedEventArgs? seen = null;
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

        await using var transport = new BlazorSqliteWorkerTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);
        await transport.OpenAsync("app.db", Ct);

        Assert.Single(js.Calls, c => c.Identifier == "module.listen");
    }

    [Fact]
    public async Task Open_EnablesForeignKeys_ByDefault_AndForwardsExtraPragmas()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");

        var options = new BlazorSqliteWorkerTransportOptions
        {
            RequiredBuild = BlazorSqliteEngineBuild.Synchronous,
            OnOpenSql = ["PRAGMA cache_size=-16000"],
        };

        await using var transport = new BlazorSqliteWorkerTransport(js, options);
        await transport.OpenAsync("app.db", Ct);

        var open = OpenRequest(js);
        Assert.Equal(true, Read(open, "foreignKeys"));
        Assert.Equal(["PRAGMA cache_size=-16000"], Assert.IsAssignableFrom<IEnumerable<string>>(Read(open, "onOpenSql")));
    }

    [Fact]
    public async Task Open_CanLeaveForeignKeysAlone()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");

        await using var transport = new BlazorSqliteWorkerTransport(js, new BlazorSqliteWorkerTransportOptions
        {
            RequiredBuild = BlazorSqliteEngineBuild.Synchronous,
            EnableForeignKeys = false,
        });
        await transport.OpenAsync("app.db", Ct);

        var open = OpenRequest(js);
        Assert.Equal(false, Read(open, "foreignKeys"));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<string>>(Read(open, "onOpenSql")));
    }

    [Fact]
    public async Task Open_PassesTheVfsModuleAndExtensionsTheProviderDeclared()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "jspi", "reused": false } }""");

        var options = new BlazorSqliteWorkerTransportOptions
        {
            RequiredBuild = BlazorSqliteEngineBuild.AsyncCapable,
            Vfs = new BlazorSqliteJsModule("./_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js"),
            ExtensionModules = [new BlazorSqliteJsModule("./functions.js", "install")],
        };

        await using var transport = new BlazorSqliteWorkerTransport(js, options);
        await transport.OpenAsync("app.db", Ct);

        var open = OpenRequest(js);
        var vfs = Read(open, "vfs")!;
        Assert.Equal("asyncCapable", Read(open, "requiredBuild"));
        Assert.Equal("./_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js", Read(vfs, "moduleUrl"));
        Assert.Equal("register", Read(vfs, "registerExport"));

        var extension = Assert.Single((object?[])Read(open, "extensions")!)!;
        Assert.Equal("./functions.js", Read(extension, "moduleUrl"));
        Assert.Equal("install", Read(extension, "registerExport"));
    }

    [Fact]
    public async Task Execute_SendsTheBinaryBatch_AndDecodesTheBinaryReply()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");
        js.EnqueueReply(new ReplyBuilder()
            .Result(["v"], [BlazorSqliteWireFormat.TypeCode.Integer])
            .Row(9007199254740993L)
            .End()
            .Build());

        await using var transport = new BlazorSqliteWorkerTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        var results = await transport.ExecuteAsync(
        [
            new BlazorSqliteCommandRequest
            {
                CommandText = "SELECT v FROM t",
                ResultKind = BlazorSqliteResultKind.Reader,
            },
        ], Ct);

        var call = js.Calls.Last(c => c.Identifier == "host.executeBinary");
        var sent = Assert.IsType<byte[]>(call.Args[0]);
        Assert.Equal(BlazorSqliteWireFormat.Magic, sent[0]);

        var row = Assert.Single(Assert.Single(results).Rows);
        Assert.Equal(9007199254740993L, Assert.Single(row));
    }

    [Fact]
    public async Task Execute_ThrowsTypedException_WhenTheReplyIsAFailure()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");
        js.EnqueueReply(ReplyBuilder.Error("UNIQUE constraint failed: t.id", 19, "SQLiteError"));

        await using var transport = new BlazorSqliteWorkerTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        var error = await Assert.ThrowsAsync<BlazorSqliteConstraintException>(
            () => transport.ExecuteAsync(
            [
                new BlazorSqliteCommandRequest { CommandText = "INSERT INTO t (id) VALUES (1)" },
            ], Ct));

        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Contains("UNIQUE", error.Message);
    }

    [Fact]
    public async Task Execute_BeforeOpen_Throws()
    {
        await using var transport = new BlazorSqliteWorkerTransport(new ScriptedJsRuntime(), DefaultOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.ExecuteAsync([], Ct));
    }

    [Fact]
    public async Task Close_IsANoOp_WhenNothingWasOpened()
    {
        var js = new ScriptedJsRuntime();
        await using var transport = new BlazorSqliteWorkerTransport(js, DefaultOptions());

        await transport.CloseAsync(Ct);

        Assert.Empty(js.Calls);
    }

    [Fact]
    public async Task Dispose_TerminatesTheWorker()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");

        var transport = new BlazorSqliteWorkerTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);
        await transport.DisposeAsync();

        Assert.True(js.HostDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.OpenAsync("app.db", Ct));
    }

    private static BlazorSqliteWorkerTransportOptions DefaultOptions() => new()
    {
        RequiredBuild = BlazorSqliteEngineBuild.Synchronous,
    };

    private static object OpenRequest(ScriptedJsRuntime js)
        => js.Calls.First(c => c.Identifier == "host.call").Args[0]!;

    private static object? Read(object target, string property)
        => target.GetType().GetProperty(property)?.GetValue(target);
}
