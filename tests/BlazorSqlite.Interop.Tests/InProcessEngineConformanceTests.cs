using BlazorSqlite.Data;
using BlazorSqlite.Storage.ConformanceTests;
using BlazorSqlite.Testing;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// Runs the engine kit against the in-process transport, which is how the kit is shown to be
/// honest: a rule the reference engine cannot satisfy is a bug in the rule. Crash safety is
/// correctly skipped - there is no worker to kill and nothing persists.
/// </summary>
public sealed class InProcessEngineConformanceTests : BlazorSqliteStorageEngineConformanceTests
{
    /// <inheritdoc />
    protected override async ValueTask<IBlazorSqliteTransport> CreateOpenTransportAsync()
    {
        var transport = new BlazorSqliteInProcessTransport();
        await transport.OpenAsync("conformance.db");
        return transport;
    }
}
