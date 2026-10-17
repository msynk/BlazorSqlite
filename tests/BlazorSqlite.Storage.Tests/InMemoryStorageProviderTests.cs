using BlazorSqlite.Storage.InMemory;
using Xunit;

namespace BlazorSqlite.Storage.Tests;

/// <summary>
/// What the in-memory backend promises beyond the contract. The generic rules - round-tripping,
/// replacement, detached exports, idempotent deletes - are checked by
/// <see cref="InMemoryStorageConformanceTests"/>, so this file covers only the choices specific to
/// this backend.
/// </summary>
public sealed class InMemoryStorageProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Image = [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x00, 0x01];

    [Fact]
    public void DeclaresItselfVolatileAndSynchronous()
    {
        var provider = new BlazorSqliteInMemoryStorageProvider();

        Assert.Equal("in-memory", provider.Name);
        Assert.False(provider.Capabilities.IsPersistent);
        Assert.Equal(BlazorSqliteEngineBuild.Synchronous, provider.Capabilities.RequiredBuild);
        Assert.Null(provider.VfsModule);

        // A second worker is a second heap: nothing is shared, so nothing is claimed.
        Assert.False(provider.Capabilities.SupportsMultipleConnections);
        Assert.False(provider.Capabilities.SupportsConcurrentReads);
    }

    [Fact]
    public async Task ProbeIsAlwaysAvailable_AndExplainsItself()
    {
        var probe = await new BlazorSqliteInMemoryStorageProvider().ProbeAsync(Ct);

        Assert.True(probe.IsAvailable);
        Assert.Null(probe.UnavailableReason);
        Assert.Equal("false", probe.Environment["persistent"]);
    }

    /// <summary>
    /// Ordering is this backend's own guarantee rather than a contract requirement, and it is what
    /// makes the diagnostics listing reproducible.
    /// </summary>
    [Fact]
    public async Task ListReturnsEveryDatabase_InAStableOrder()
    {
        var admin = new BlazorSqliteInMemoryStorageProvider().Admin;
        await admin.ImportAsync("zebra.db", Image, Ct);
        await admin.ImportAsync("apple.db", Image, Ct);

        Assert.Equal(["apple.db", "zebra.db"], await admin.ListAsync(Ct));
    }

    /// <summary>
    /// The contract only requires that exporting an absent database fails; this backend commits to the
    /// exception the migration protocol distinguishes a missing source by.
    /// </summary>
    [Fact]
    public async Task ExportingAnAbsentDatabase_ThrowsFileNotFound()
    {
        var admin = new BlazorSqliteInMemoryStorageProvider().Admin;

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => admin.ExportAsync("never-existed.db", Ct).AsTask());
    }

    /// <summary>
    /// Each provider instance owns its own databases; sharing them through static state would make
    /// two independently configured stores silently interfere.
    /// </summary>
    [Fact]
    public async Task ProviderInstancesDoNotShareStorage()
    {
        var first = new BlazorSqliteInMemoryStorageProvider();
        var second = new BlazorSqliteInMemoryStorageProvider();

        await first.Admin.ImportAsync("app.db", Image, Ct);

        Assert.False(await second.Admin.ExistsAsync("app.db", Ct));
    }
}
