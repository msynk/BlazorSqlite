using BlazorSqlite.Storage;
using BlazorSqlite.Storage.ConformanceTests;
using BlazorSqlite.Storage.Opfs;

namespace BlazorSqlite.Storage.Tests;

/// <summary>
/// Runs the contract-layer kit against the OPFS provider. Admin tests skip on desktop because
/// there is no JavaScript runtime — the probe reports unavailable, which is the honest answer.
/// The engine-layer kit and admin operations run in the browser suite.
/// </summary>
public sealed class OpfsStorageConformanceTests : StorageProviderConformanceTests
{
    /// <inheritdoc />
    protected override IBlazorSqliteStorageProvider CreateProvider() => new OpfsStorageProvider();
}
