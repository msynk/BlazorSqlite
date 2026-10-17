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

        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        Assert.Equal("import", js.Calls[0].Identifier);
        Assert.Equal(WorkerSqliteTransport.DefaultHostModuleUrl, js.Calls[0].Args[0]);
        Assert.Equal("module.createHost", js.Calls[1].Identifier);
        Assert.Equal("host.call", js.Calls[2].Identifier);
        Assert.Equal("host.subscribeDotNet", js.Calls[3].Identifier);

        var request = js.Calls[2].Args[0]!;
        Assert.Equal("open", Read(request, "kind"));
        Assert.Equal("app.db", Read(request, "databaseName"));
        Assert.Equal("synchronous", Read(request, "requiredBuild"));
        Assert.Null(Read(request, "vfs"));
        Assert.Equal(nameof(WorkerSqliteTransport.OnRemoteTablesChanged), js.Subscription!.Value.Method);
    }

    [Fact]
    public async Task Open_EnablesForeignKeys_ByDefault_AndForwardsExtraPragmas()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");

        var options = new WorkerSqliteTransportOptions
        {
            RequiredBuild = BlazorSqliteEngineBuild.Synchronous,
            OnOpenSql = ["PRAGMA cache_size=-16000"],
        };

        await using var transport = new WorkerSqliteTransport(js, options);
        await transport.OpenAsync("app.db", Ct);

        var onOpenSql = Assert.IsType<List<string>>(Read(js.Calls[2].Args[0]!, "onOpenSql"));
        Assert.Equal(["PRAGMA foreign_keys = ON", "PRAGMA cache_size=-16000"], onOpenSql);
    }

    [Fact]
    public async Task Open_CanLeaveForeignKeysAlone()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");

        await using var transport = new WorkerSqliteTransport(js, new WorkerSqliteTransportOptions
        {
            RequiredBuild = BlazorSqliteEngineBuild.Synchronous,
            EnableForeignKeys = false,
        });
        await transport.OpenAsync("app.db", Ct);

        Assert.Empty(Assert.IsType<List<string>>(Read(js.Calls[2].Args[0]!, "onOpenSql")));
    }

    [Fact]
    public async Task Open_PassesTheVfsModuleAndExtensionsTheProviderDeclared()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "jspi", "reused": false } }""");

        var options = new WorkerSqliteTransportOptions
        {
            RequiredBuild = BlazorSqliteEngineBuild.AsyncCapable,
            Vfs = new BlazorSqliteJsModule("./_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js"),
            ExtensionModules = [new BlazorSqliteJsModule("./functions.js", "install")],
        };

        await using var transport = new WorkerSqliteTransport(js, options);
        await transport.OpenAsync("app.db", Ct);

        var open = js.Calls[2].Args[0]!;
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
            .Result(["v"], [SqliteWireFormat.TypeCode.Integer])
            .Row(9007199254740993L)
            .End()
            .Build());

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

        var call = js.Calls.Last(c => c.Identifier == "host.executeBinary");
        var sent = Assert.IsType<byte[]>(call.Args[0]);
        Assert.Equal(SqliteWireFormat.Magic, sent[0]);

        var row = Assert.Single(Assert.Single(results).Rows);
        Assert.Equal(9007199254740993L, Assert.Single(row));
    }

    [Fact]
    public async Task Execute_ThrowsTypedException_WhenTheReplyIsAFailure()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");
        js.EnqueueReply(ReplyBuilder.Error("UNIQUE constraint failed: t.id", 19, "SQLiteError"));

        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        var error = await Assert.ThrowsAsync<BlazorSqliteConstraintException>(
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
    public async Task RemoteNotification_RaisesRemoteTablesChanged()
    {
        var js = new ScriptedJsRuntime();
        js.EnqueueEnvelope("""{ "ok": true, "result": { "build": "synchronous" } }""");

        await using var transport = new WorkerSqliteTransport(js, DefaultOptions());
        await transport.OpenAsync("app.db", Ct);

        SqliteTablesChangedEventArgs? seen = null;
        transport.RemoteTablesChanged += (_, e) => seen = e;

        var target = Assert.IsType<DotNetObjectReference<WorkerSqliteTransport>>(js.Subscription!.Value.Target);
        target.Value.OnRemoteTablesChanged(["Products"]);

        Assert.NotNull(seen);
        Assert.Contains("products", seen.Tables);
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

    private static object? Read(object target, string property)
        => target.GetType().GetProperty(property)?.GetValue(target);
}
