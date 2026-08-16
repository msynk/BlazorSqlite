using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage.InMemory;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

public sealed class StorageMigratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Image =
    [
        .. "SQLite format 3\0"u8,
        .. Enumerable.Repeat((byte)7, 48),
    ];

    [Fact]
    public async Task CopiesTheImage_FlipsTheBinding_AndDeletesTheSource()
    {
        var source = new InMemoryStorageProvider("source-memory");
        var target = new InMemoryStorageProvider("target-memory");
        var store = new InMemoryStorageBindingStore();
        await source.Admin.ImportAsync("app.db", Image, Ct);

        await new StorageMigrator().MigrateAsync("app.db", source, target, store, Ct);

        Assert.False(await source.Admin.ExistsAsync("app.db", Ct));
        Assert.Equal(Image, await target.Admin.ExportAsync("app.db", Ct));
        Assert.Equal(target.Name, await store.GetProviderNameAsync("app.db", Ct));
    }

    [Fact]
    public async Task ACorruptImage_LeavesTheSourceAndBindingUntouched()
    {
        var source = new InMemoryStorageProvider("source-memory");
        var target = new InMemoryStorageProvider("target-memory");
        var store = new InMemoryStorageBindingStore();
        await source.Admin.ImportAsync("app.db", new byte[] { 1, 2, 3, 4 }, Ct);

        await Assert.ThrowsAsync<BlazorSqliteCorruptDatabaseException>(
            () => new StorageMigrator().MigrateAsync("app.db", source, target, store, Ct));

        Assert.True(await source.Admin.ExistsAsync("app.db", Ct));
        Assert.False(await target.Admin.ExistsAsync("app.db", Ct));
        Assert.Null(await store.GetProviderNameAsync("app.db", Ct));
    }
}
