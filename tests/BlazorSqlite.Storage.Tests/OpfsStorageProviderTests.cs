using BlazorSqlite.Storage;
using BlazorSqlite.Storage.Opfs;
using Xunit;

namespace BlazorSqlite.Storage.Tests;

public sealed class OpfsStorageProviderTests
{
    [Fact]
    public void Name_IsTheStickyBindingKey()
    {
        Assert.Equal("opfs", new OpfsStorageProvider().Name);
        Assert.Equal(OpfsStorageProvider.ProviderName, new OpfsStorageProvider().Name);
    }

    [Fact]
    public void Capabilities_MatchOpfsCoopSync()
    {
        var capabilities = new OpfsStorageProvider().Capabilities;

        Assert.Equal(BlazorSqliteEngineBuild.Synchronous, capabilities.RequiredBuild);
        Assert.True(capabilities.IsPersistent);
        Assert.True(capabilities.SupportsMultipleConnections);
        Assert.False(capabilities.SupportsConcurrentReads);
        Assert.False(capabilities.SupportsRelaxedDurability);
        Assert.False(capabilities.SupportsMultiDatabaseTransactions);
        Assert.True(capabilities.CanChangePageSize);
        Assert.Equal(BlazorSqliteExecutionContexts.DedicatedWorker, capabilities.SupportedContexts);
    }

    [Fact]
    public void VfsModule_IsRootRelative_SoTheWorkerCanImportIt()
    {
        var vfs = new OpfsStorageProvider().VfsModule;

        Assert.NotNull(vfs);
        Assert.Equal(OpfsStorageProvider.VfsModuleUrl, vfs.ModuleUrl);
        Assert.StartsWith("/_content/", vfs.ModuleUrl, StringComparison.Ordinal);
        Assert.Equal("register", vfs.RegisterExport);
    }

    [Fact]
    public async Task Probe_WithoutJavaScript_ReportsUnavailable()
    {
        var probe = await new OpfsStorageProvider().ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(probe.IsAvailable);
        Assert.Contains("browser", probe.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("false", probe.Environment["javascript"]);
    }
}
