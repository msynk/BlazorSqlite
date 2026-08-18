using BlazorSqlite.Storage;
using BlazorSqlite.Storage.IndexedDb;
using Xunit;

namespace BlazorSqlite.Storage.Tests;

public sealed class IndexedDbStorageProviderTests
{
    [Fact]
    public void Name_IsTheStickyBindingKey()
    {
        Assert.Equal("indexeddb", new IndexedDbStorageProvider().Name);
        Assert.Equal(IndexedDbStorageProvider.ProviderName, new IndexedDbStorageProvider().Name);
    }

    [Fact]
    public void Capabilities_MatchIdbBatchAtomic()
    {
        var capabilities = new IndexedDbStorageProvider().Capabilities;

        Assert.Equal(BlazorSqliteEngineBuild.AsyncCapable, capabilities.RequiredBuild);
        Assert.True(capabilities.IsPersistent);
        Assert.True(capabilities.SupportsMultipleConnections);

        // idb-vfs.js registers the VFS without a lock policy, so WebLocksMixin's 'exclusive'
        // default applies and reads serialize behind each other like writes do.
        Assert.False(capabilities.SupportsConcurrentReads);

        Assert.True(capabilities.SupportsRelaxedDurability);
        Assert.True(capabilities.SupportsMultiDatabaseTransactions);
        Assert.False(capabilities.CanChangePageSize);
        Assert.True(capabilities.SupportedContexts.HasFlag(BlazorSqliteExecutionContexts.DedicatedWorker));
        Assert.True(capabilities.SupportedContexts.HasFlag(BlazorSqliteExecutionContexts.Window));
    }

    [Fact]
    public void VfsModule_IsRootRelative_SoTheWorkerCanImportIt()
    {
        var vfs = new IndexedDbStorageProvider().VfsModule;

        Assert.NotNull(vfs);
        Assert.Equal(IndexedDbStorageProvider.VfsModuleUrl, vfs.ModuleUrl);
        Assert.StartsWith("/_content/", vfs.ModuleUrl, StringComparison.Ordinal);
        Assert.Equal("register", vfs.RegisterExport);
    }

    [Fact]
    public async Task Probe_WithoutJavaScript_ReportsUnavailable()
    {
        var probe = await new IndexedDbStorageProvider().ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(probe.IsAvailable);
        Assert.Contains("browser", probe.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("false", probe.Environment["javascript"]);
    }
}
