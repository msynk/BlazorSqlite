using BlazorSqlite.Interop;
using Xunit;

namespace BlazorSqlite.Storage.Tests;

public sealed class StorageProviderResolverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Database = "app.db";

    private static BlazorSqliteStorageSelection Select(
        Action<BlazorSqliteStorageSelectionBuilder> configure)
        => BlazorSqliteStorageSelectionBuilder.Create(configure);

    private static StorageProviderResolver Resolver(
        IStorageBindingStore bindingStore,
        params IBlazorSqliteStorageProvider[] providers)
        => new(providers, bindingStore);

    [Fact]
    public async Task SingleAvailableProvider_IsSelected()
    {
        var opfs = new FakeStorageProvider("opfs");
        var resolver = Resolver(new InMemoryStorageBindingStore(), opfs);

        var resolution = await resolver.ResolveAsync(Database, Select(s => s.Prefer("opfs")), Ct);

        Assert.Same(opfs, resolution.Provider);
        Assert.True(resolution.IsFirstChoice);
        Assert.False(resolution.WasDecidedByExistingData);
        Assert.Null(resolution.BetterProviderAvailable);
    }

    /// <summary>
    /// A lone candidate is a strict instruction. Substituting something else would mean the
    /// application silently got storage it never asked for.
    /// </summary>
    [Fact]
    public async Task SingleUnavailableProvider_Throws_WithoutSubstituting()
    {
        var resolver = Resolver(
            new InMemoryStorageBindingStore(),
            new FakeStorageProvider("opfs", isAvailable: false),
            new FakeStorageProvider("indexeddb"));

        var failure = await Assert.ThrowsAsync<BlazorSqliteStorageUnavailableException>(
            () => resolver.ResolveAsync(Database, Select(s => s.Prefer("opfs")), Ct).AsTask());

        Assert.Equal(Database, failure.DatabaseName);
        var attempt = Assert.Single(failure.Attempts);
        Assert.Equal(StorageCandidateStatus.Unavailable, attempt.Status);
        Assert.Contains("switched off", attempt.Probe!.UnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallbackIsUsed_WhenPreferredIsUnavailable()
    {
        var indexedDb = new FakeStorageProvider("indexeddb");
        var resolver = Resolver(
            new InMemoryStorageBindingStore(),
            new FakeStorageProvider("opfs", isAvailable: false),
            indexedDb);

        var resolution = await resolver.ResolveAsync(
            Database,
            Select(s => s.Prefer("opfs").Fallback("indexeddb")),
            Ct);

        Assert.Same(indexedDb, resolution.Provider);
        Assert.False(resolution.IsFirstChoice);
        Assert.Collection(
            resolution.Attempts,
            a => Assert.Equal(StorageCandidateStatus.Unavailable, a.Status),
            a => Assert.Equal(StorageCandidateStatus.Selected, a.Status));
    }

    [Fact]
    public async Task UnregisteredCandidate_IsReported_AndSkipped()
    {
        var indexedDb = new FakeStorageProvider("indexeddb");
        var resolver = Resolver(new InMemoryStorageBindingStore(), indexedDb);

        var resolution = await resolver.ResolveAsync(
            Database,
            Select(s => s.Prefer("opfs").Fallback("indexeddb")),
            Ct);

        Assert.Same(indexedDb, resolution.Provider);
        Assert.Equal(StorageCandidateStatus.NotRegistered, resolution.Attempts[0].Status);
    }

    [Fact]
    public async Task AllCandidatesFail_ReportsEveryReason()
    {
        var resolver = Resolver(
            new InMemoryStorageBindingStore(),
            new FakeStorageProvider("opfs", isAvailable: false),
            new FakeStorageProvider("indexeddb", isAvailable: false));

        var failure = await Assert.ThrowsAsync<BlazorSqliteStorageUnavailableException>(
            () => resolver
                .ResolveAsync(Database, Select(s => s.Prefer("opfs").Fallback("indexeddb")), Ct)
                .AsTask());

        Assert.Equal(2, failure.Attempts.Count);
        Assert.All(failure.Attempts, a => Assert.Equal(StorageCandidateStatus.Unavailable, a.Status));
        Assert.Contains("opfs", failure.Message, StringComparison.Ordinal);
        Assert.Contains("indexeddb", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderThatThrowsWhileProbing_IsTreatedAsUnavailable()
    {
        var indexedDb = new FakeStorageProvider("indexeddb");
        var resolver = Resolver(
            new InMemoryStorageBindingStore(),
            new FakeStorageProvider("opfs", probeThrows: new InvalidOperationException("boom")),
            indexedDb);

        var resolution = await resolver.ResolveAsync(
            Database,
            Select(s => s.Prefer("opfs").Fallback("indexeddb")),
            Ct);

        Assert.Same(indexedDb, resolution.Provider);
        Assert.Contains("boom", resolution.Attempts[0].Probe!.UnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateProviderNames_AreRejected()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => Resolver(
                new InMemoryStorageBindingStore(),
                new FakeStorageProvider("opfs"),
                new FakeStorageProvider("OPFS")));

        Assert.Contains("must be unique", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeResultIsCached_AcrossResolves()
    {
        var opfs = new FakeStorageProvider("opfs");
        var resolver = Resolver(new InMemoryStorageBindingStore(), opfs);
        var selection = Select(s => s.Prefer("opfs"));

        await resolver.ResolveAsync("one.db", selection, Ct);
        await resolver.ResolveAsync("two.db", selection, Ct);

        Assert.Equal(1, opfs.ProbeCount);
    }

    public sealed class NonPersistentFallback
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task IsRejected_WithoutOptIn()
        {
            var resolver = Resolver(
                new InMemoryStorageBindingStore(),
                new FakeStorageProvider("opfs", isAvailable: false),
                new FakeStorageProvider("in-memory", isPersistent: false));

            var failure = await Assert.ThrowsAsync<BlazorSqliteStorageUnavailableException>(
                () => resolver
                    .ResolveAsync(Database, Select(s => s.Prefer("opfs").Fallback("in-memory")), Ct)
                    .AsTask());

            Assert.Equal(
                StorageCandidateStatus.RejectedAsNonPersistent,
                failure.Attempts[1].Status);
            Assert.Contains(
                "AllowNonPersistentFallback",
                failure.Attempts[1].Explanation!,
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task IsAccepted_WithOptIn()
        {
            var inMemory = new FakeStorageProvider("in-memory", isPersistent: false);
            var resolver = Resolver(
                new InMemoryStorageBindingStore(),
                new FakeStorageProvider("opfs", isAvailable: false),
                inMemory);

            var resolution = await resolver.ResolveAsync(
                Database,
                Select(s => s.Prefer("opfs").Fallback("in-memory").AllowNonPersistentFallback()),
                Ct);

            Assert.Same(inMemory, resolution.Provider);
        }

        /// <summary>
        /// Choosing volatile storage outright is a decision the application already made, so it needs
        /// no second opt-in — the guard exists for silent downgrades, not deliberate ones.
        /// </summary>
        [Fact]
        public async Task IsAllowed_AsAnExplicitFirstChoice()
        {
            var inMemory = new FakeStorageProvider("in-memory", isPersistent: false);
            var resolver = Resolver(new InMemoryStorageBindingStore(), inMemory);

            var resolution = await resolver.ResolveAsync(
                Database,
                Select(s => s.Prefer("in-memory")),
                Ct);

            Assert.Same(inMemory, resolution.Provider);
        }
    }

    public sealed class StickyBinding
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ExistingData_OutranksPreference()
        {
            var store = new InMemoryStorageBindingStore();
            await store.SetProviderNameAsync(Database, "indexeddb", Ct);

            var opfs = new FakeStorageProvider("opfs");
            var indexedDb = new FakeStorageProvider("indexeddb");
            var resolver = Resolver(store, opfs, indexedDb);

            var resolution = await resolver.ResolveAsync(
                Database,
                Select(s => s.Prefer("opfs").Fallback("indexeddb")),
                Ct);

            Assert.Same(indexedDb, resolution.Provider);
            Assert.True(resolution.WasDecidedByExistingData);
            Assert.False(resolution.IsFirstChoice);
            Assert.Same(opfs, resolution.BetterProviderAvailable);
        }

        /// <summary>
        /// The failure this whole mechanism exists to prevent: OPFS becomes available, preference says
        /// OPFS, and the user's IndexedDB database would be replaced by an empty one with no error.
        /// </summary>
        [Fact]
        public async Task UnreachableExistingData_Throws_RatherThanOpeningAnEmptyDatabase()
        {
            var store = new InMemoryStorageBindingStore();
            await store.SetProviderNameAsync(Database, "indexeddb", Ct);

            var resolver = Resolver(
                store,
                new FakeStorageProvider("opfs"),
                new FakeStorageProvider("indexeddb", isAvailable: false));

            var failure = await Assert.ThrowsAsync<BlazorSqliteStorageUnavailableException>(
                () => resolver
                    .ResolveAsync(Database, Select(s => s.Prefer("opfs").Fallback("indexeddb")), Ct)
                    .AsTask());

            var attempt = Assert.Single(failure.Attempts);
            Assert.Equal("indexeddb", attempt.ProviderName);
            Assert.Contains("will not open an empty database", attempt.Explanation!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ExistingDataOnUnregisteredProvider_ThrowsWithActionableMessage()
        {
            var store = new InMemoryStorageBindingStore();
            await store.SetProviderNameAsync(Database, "cache-storage", Ct);

            var resolver = Resolver(store, new FakeStorageProvider("opfs"));

            var failure = await Assert.ThrowsAsync<BlazorSqliteStorageUnavailableException>(
                () => resolver.ResolveAsync(Database, Select(s => s.Prefer("opfs")), Ct).AsTask());

            var attempt = Assert.Single(failure.Attempts);
            Assert.Equal(StorageCandidateStatus.NotRegistered, attempt.Status);
            Assert.Contains("Register it", attempt.Explanation!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task NoBetterProviderIsReported_WhenBoundProviderIsTheFirstChoice()
        {
            var store = new InMemoryStorageBindingStore();
            await store.SetProviderNameAsync(Database, "opfs", Ct);

            var resolver = Resolver(
                store,
                new FakeStorageProvider("opfs"),
                new FakeStorageProvider("indexeddb"));

            var resolution = await resolver.ResolveAsync(
                Database,
                Select(s => s.Prefer("opfs").Fallback("indexeddb")),
                Ct);

            Assert.True(resolution.IsFirstChoice);
            Assert.Null(resolution.BetterProviderAvailable);
        }

        /// <summary>
        /// Moving a database onto volatile storage is never an upgrade, however the preference list is
        /// ordered.
        /// </summary>
        [Fact]
        public async Task NonPersistentProviderIsNeverReportedAsBetter()
        {
            var store = new InMemoryStorageBindingStore();
            await store.SetProviderNameAsync(Database, "indexeddb", Ct);

            var resolver = Resolver(
                store,
                new FakeStorageProvider("in-memory", isPersistent: false),
                new FakeStorageProvider("indexeddb"));

            var resolution = await resolver.ResolveAsync(
                Database,
                Select(s => s.Prefer("in-memory").Fallback("indexeddb")),
                Ct);

            Assert.Null(resolution.BetterProviderAvailable);
        }

        [Fact]
        public async Task CommitBinding_MakesTheChoiceStick()
        {
            var store = new InMemoryStorageBindingStore();
            var indexedDb = new FakeStorageProvider("indexeddb");
            var resolver = Resolver(store, new FakeStorageProvider("opfs", isAvailable: false), indexedDb);
            var selection = Select(s => s.Prefer("opfs").Fallback("indexeddb"));

            var first = await resolver.ResolveAsync(Database, selection, Ct);
            await resolver.CommitBindingAsync(first, Ct);

            Assert.Equal("indexeddb", await store.GetProviderNameAsync(Database, Ct));

            var second = await resolver.ResolveAsync(Database, selection, Ct);
            Assert.Same(indexedDb, second.Provider);
            Assert.True(second.WasDecidedByExistingData);
        }

        /// <summary>
        /// Resolving is a read-only decision; a database that never opened must not leave a binding
        /// behind claiming it did.
        /// </summary>
        [Fact]
        public async Task Resolve_DoesNotRecordAnything_UntilCommitted()
        {
            var store = new InMemoryStorageBindingStore();
            var resolver = Resolver(store, new FakeStorageProvider("opfs"));

            await resolver.ResolveAsync(Database, Select(s => s.Prefer("opfs")), Ct);

            Assert.Null(await store.GetProviderNameAsync(Database, Ct));
        }
    }
}
