using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage;
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

        Assert.Equal("import", js.Calls[0].Identifier);
        Assert.Equal(WorkerSqliteTransport.DefaultHostModuleUrl, js.Calls[0].Args[0]);
        Assert.Equal("module.createHost", js.Calls[1].Identifier);
        Assert.Equal("host.call", js.Calls[2].Identifier);

        var request = js.Calls[2].Args[0]!;
        Assert.Equal("open", Read(request, "kind"));
        Assert.Equal("app.db", Read(request, "databaseName"));
        Assert.Equal("synchronous", Read(request, "requiredBuild"));
        Assert.Null(Read(request, "vfs"));
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

        var vfs = Read(js.Calls[2].Args[0]!, "vfs")!;
        Assert.Equal("asyncCapable", Read(js.Calls[2].Args[0]!, "requiredBuild"));
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

    private static object? Read(object target, string property)
        => target.GetType().GetProperty(property)?.GetValue(target);
}
