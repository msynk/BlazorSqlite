using System.Data;
using BlazorSqlite.Data;
using BlazorSqlite.Storage;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// The session factory is the only place that both chooses a backend and opens it, so the rule
/// "the binding is written only after the open succeeds" is asserted here rather than left as a
/// comment on two collaborating types.
/// </summary>
public sealed class BlazorSqliteSessionFactoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Open_UsesTheResolvedProvider_AndCommitsTheBinding()
    {
        var opfs = Available("opfs", BlazorSqliteEngineBuild.Synchronous);
        var store = new InMemoryStorageBindingStore();
        var transports = new RecordingTransportFactory();
        var factory = Factory(store, transports, s => s.Prefer("opfs"), opfs);

        await using var session = await factory.OpenAsync("app.db", Ct);

        Assert.Equal("opfs", session.Resolution.Provider.Name);
        Assert.Equal("opfs", await store.GetProviderNameAsync("app.db", Ct));
        Assert.Equal("app.db", transports.Last.OpenedAs);
        Assert.Equal(opfs, transports.Created[0].Provider);
        Assert.Equal(ConnectionState.Open, session.Connection.State);
    }

    [Fact]
    public async Task Open_PassesTheProvidersBuildAndVfs_ThroughTheTransportFactory()
    {
        var indexed = Available(
            "indexeddb",
            BlazorSqliteEngineBuild.AsyncCapable,
            vfs: new BlazorSqliteJsModule("./_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js"));

        var capturing = new RecordingTransportFactory();
        var factory = Factory(
            new InMemoryStorageBindingStore(),
            capturing,
            s => s.Prefer("indexeddb"),
            indexed);

        await using var session = await factory.OpenAsync("app.db", Ct);

        var seen = capturing.Created[0].Provider;
        Assert.Same(indexed, seen);
        Assert.Equal(BlazorSqliteEngineBuild.AsyncCapable, seen.Capabilities.RequiredBuild);
        Assert.Equal("./_content/BlazorSqlite.Storage.IndexedDb/idb-vfs.js", seen.VfsModule!.ModuleUrl);
    }

    [Fact]
    public async Task FailedOpen_DoesNotCommitABinding_AndDisposesTheTransport()
    {
        var store = new InMemoryStorageBindingStore();
        var transports = new RecordingTransportFactory(
            () => new ScriptedTransport
            {
                OpenThrows = new BlazorSqliteException("disk I/O error", sqliteErrorCode: 10),
            });
        var factory = Factory(store, transports, s => s.Prefer("opfs"), Available("opfs"));

        var error = await Assert.ThrowsAsync<BlazorSqliteException>(
            () => factory.OpenAsync("app.db", Ct));

        Assert.Equal(10, error.SqliteErrorCode);
        Assert.Null(await store.GetProviderNameAsync("app.db", Ct));
        Assert.True(transports.Last.Disposed);
    }

    [Fact]
    public async Task ExistingData_OpensOnTheBoundBackend_NotThePreferredOne()
    {
        var indexed = Available("indexeddb", BlazorSqliteEngineBuild.AsyncCapable);
        var opfs = Available("opfs", BlazorSqliteEngineBuild.Synchronous);
        var store = new InMemoryStorageBindingStore();
        await store.SetProviderNameAsync("app.db", "indexeddb", Ct);

        var transports = new RecordingTransportFactory();
        var factory = Factory(
            store,
            transports,
            s => s.Prefer("opfs").Fallback("indexeddb"),
            opfs,
            indexed);

        await using var session = await factory.OpenAsync("app.db", Ct);

        Assert.Equal("indexeddb", session.Resolution.Provider.Name);
        Assert.True(session.Resolution.WasDecidedByExistingData);
        Assert.Same(indexed, transports.Created[0].Provider);
    }

    [Fact]
    public async Task AutomaticOnOpen_WithoutABetterProvider_JustOpens()
    {
        var store = new InMemoryStorageBindingStore();
        var factory = Factory(
            store,
            new RecordingTransportFactory(),
            s => s.Prefer("opfs").WithMigrationMode(StorageMigrationMode.AutomaticOnOpen),
            Available("opfs"));

        await using var session = await factory.OpenAsync("app.db", Ct);

        Assert.Equal("opfs", session.Resolution.Provider.Name);
        Assert.Equal("opfs", await store.GetProviderNameAsync("app.db", Ct));
    }

    [Fact]
    public async Task Dispose_TearsDownTheTransport()
    {
        var transports = new RecordingTransportFactory();
        var factory = Factory(
            new InMemoryStorageBindingStore(),
            transports,
            s => s.Prefer("opfs"),
            Available("opfs"));

        var session = await factory.OpenAsync("app.db", Ct);
        await session.DisposeAsync();

        Assert.True(transports.Last.Disposed);
    }

    private static ConfigurableProvider Available(
        string name,
        BlazorSqliteEngineBuild build = BlazorSqliteEngineBuild.Synchronous,
        BlazorSqliteJsModule? vfs = null)
        => new()
        {
            Name = name,
            RequiredBuild = build,
            Vfs = vfs,
        };

    private static BlazorSqliteSessionFactory Factory(
        InMemoryStorageBindingStore store,
        ISqliteTransportFactory transports,
        Action<BlazorSqliteStorageSelectionBuilder> configure,
        params IBlazorSqliteStorageProvider[] providers)
        => new(
            new StorageProviderResolver(providers, store),
            transports,
            BlazorSqliteStorageSelectionBuilder.Create(configure),
            store);
}
