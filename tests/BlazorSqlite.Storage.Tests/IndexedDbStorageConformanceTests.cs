using BlazorSqlite.Storage;
using BlazorSqlite.Storage.ConformanceTests;
using BlazorSqlite.Storage.IndexedDb;

namespace BlazorSqlite.Storage.Tests;

/// <summary>
/// Runs the contract-layer kit against the IndexedDB provider. Admin tests skip on desktop because
/// there is no JavaScript runtime - the probe reports unavailable, which is the honest answer.
/// </summary>
public sealed class IndexedDbStorageConformanceTests : StorageProviderConformanceTests
{
    /// <inheritdoc />
    protected override IBlazorSqliteStorageProvider CreateProvider() => new IndexedDbStorageProvider();
}
