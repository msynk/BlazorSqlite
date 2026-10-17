using BlazorSqlite.Storage;
using BlazorSqlite.Storage.CacheStorage;
using Xunit;

namespace BlazorSqlite.Storage.Tests;

public sealed class CacheStorageProviderTests
{
    [Fact]
    public void Name_IsTheStickyBindingKey()
        => Assert.Equal("cache-storage", new BlazorSqliteCacheStorageProvider().Name);

    [Fact]
    public void Capabilities_MatchJournalBasedCacheStorage()
    {
        var capabilities = new BlazorSqliteCacheStorageProvider().Capabilities;

        Assert.Equal(BlazorSqliteEngineBuild.AsyncCapable, capabilities.RequiredBuild);
        Assert.True(capabilities.IsPersistent);
        Assert.True(capabilities.SupportsMultipleConnections);
        Assert.True(capabilities.SupportsConcurrentReads);
        Assert.False(capabilities.SupportsRelaxedDurability);
        Assert.True(capabilities.SupportsMultiDatabaseTransactions);
        Assert.False(capabilities.CanChangePageSize);
    }

    [Fact]
    public async Task Probe_WithoutJavaScript_ReportsUnavailable()
    {
        var probe = await new BlazorSqliteCacheStorageProvider().ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(probe.IsAvailable);
        Assert.Contains("browser", probe.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }
}
