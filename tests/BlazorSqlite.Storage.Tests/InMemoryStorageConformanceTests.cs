using BlazorSqlite.Storage.ConformanceTests;
using BlazorSqlite.Storage.InMemory;

namespace BlazorSqlite.Storage.Tests;

/// <summary>
/// Runs the conformance kit against the in-memory backend, which is the contract's reference
/// implementation - so a kit rule the in-memory provider cannot satisfy is a bug in the rule.
/// </summary>
public sealed class InMemoryStorageConformanceTests : BlazorSqliteStorageProviderConformanceTests
{
    /// <inheritdoc />
    protected override IBlazorSqliteStorageProvider CreateProvider() => new BlazorSqliteInMemoryStorageProvider();
}
