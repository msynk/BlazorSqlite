using BlazorSqlite.Storage;
using BlazorSqlite.Storage.CacheStorage;
using BlazorSqlite.Storage.ConformanceTests;

namespace BlazorSqlite.Storage.Tests;

public sealed class CacheStorageConformanceTests : BlazorSqliteStorageProviderConformanceTests
{
    /// <inheritdoc />
    protected override IBlazorSqliteStorageProvider CreateProvider() => new BlazorSqliteCacheStorageProvider();
}
