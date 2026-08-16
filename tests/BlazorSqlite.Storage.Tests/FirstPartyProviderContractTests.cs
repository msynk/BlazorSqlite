using BlazorSqlite;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage.CacheStorage;
using BlazorSqlite.Storage.IndexedDb;
using BlazorSqlite.Storage.InMemory;
using BlazorSqlite.Storage.Opfs;
using Xunit;

namespace BlazorSqlite.Storage.Tests;

/// <summary>
/// S8's freeze check: the three first-party providers sit on the same contract without the core
/// knowing their names, and sticky binding still refuses to open an empty database elsewhere.
/// </summary>
public sealed class FirstPartyProviderContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FirstPartyNames_AreTheOnesSelectionAlreadyUses()
    {
        Assert.Equal("in-memory", new InMemoryStorageProvider().Name);
        Assert.Equal("opfs", new OpfsStorageProvider().Name);
        Assert.Equal("indexeddb", new IndexedDbStorageProvider().Name);
        Assert.Equal("cache-storage", new CacheStorageProvider().Name);
    }

    [Fact]
    public void PersistentProviders_DoNotShareAName()
    {
        Assert.NotEqual(new OpfsStorageProvider().Name, new IndexedDbStorageProvider().Name);
    }

    [Fact]
    public async Task BoundToIndexedDb_DoesNotOpenOpfs_WhenIndexedDbCannotBeProbed()
    {
        var store = new InMemoryStorageBindingStore();
        await store.SetProviderNameAsync("app.db", "indexeddb", Ct);

        var resolver = new StorageProviderResolver(
            [new OpfsStorageProvider(), new IndexedDbStorageProvider()],
            store);

        var failure = await Assert.ThrowsAsync<BlazorSqliteStorageUnavailableException>(
            () => resolver
                .ResolveAsync(
                    "app.db",
                    BlazorSqliteStorageSelectionBuilder.Create(s => s.Prefer("opfs").Fallback("indexeddb")),
                    Ct)
                .AsTask());

        var attempt = Assert.Single(failure.Attempts);
        Assert.Equal("indexeddb", attempt.ProviderName);
        Assert.Contains("will not open an empty database", attempt.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundToOpfs_DoesNotOpenIndexedDb_WhenOpfsCannotBeProbed()
    {
        var store = new InMemoryStorageBindingStore();
        await store.SetProviderNameAsync("app.db", "opfs", Ct);

        var resolver = new StorageProviderResolver(
            [new OpfsStorageProvider(), new IndexedDbStorageProvider()],
            store);

        var failure = await Assert.ThrowsAsync<BlazorSqliteStorageUnavailableException>(
            () => resolver
                .ResolveAsync(
                    "app.db",
                    BlazorSqliteStorageSelectionBuilder.Create(s => s.Prefer("indexeddb").Fallback("opfs")),
                    Ct)
                .AsTask());

        var attempt = Assert.Single(failure.Attempts);
        Assert.Equal("opfs", attempt.ProviderName);
        Assert.Contains("will not open an empty database", attempt.Explanation!, StringComparison.Ordinal);
    }
}
