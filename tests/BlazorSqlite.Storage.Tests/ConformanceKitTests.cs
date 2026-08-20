using BlazorSqlite.Storage.ConformanceTests;
using Xunit;
using Xunit.Sdk;

namespace BlazorSqlite.Storage.Tests;

/// <summary>
/// Tests the conformance kit itself. A suite that only ever runs against a correct provider proves
/// nothing, so each rule is aimed at a backend that breaks exactly that rule and is expected to fail.
/// </summary>
public sealed class ConformanceKitTests
{
    /// <summary>
    /// Runs the kit's assertions against a chosen provider. Private, so the kit's inherited tests are
    /// not collected a second time from here.
    /// </summary>
    private sealed class Harness(IBlazorSqliteStorageProvider provider) : BlazorSqliteStorageProviderConformanceTests
    {
        protected override IBlazorSqliteStorageProvider CreateProvider() => provider;
    }

    private static async Task RejectsAsync(
        IBlazorSqliteStorageProvider provider,
        Func<Harness, Task> rule)
    {
        var harness = new Harness(provider);

        await using (harness)
        {
            await Assert.ThrowsAnyAsync<XunitException>(async () => await rule(harness));
        }
    }

    private static async Task RejectsAsync(
        IBlazorSqliteStorageProvider provider,
        Action<Harness> rule)
        => await RejectsAsync(provider, harness =>
        {
            rule(harness);
            return Task.CompletedTask;
        });

    /// <summary>The correct provider is expected to pass; the rest of this class expects failures.</summary>
    [Fact]
    public async Task AcceptsAConformantBackend()
    {
        var harness = new Harness(new LyingStorageProvider());

        await using (harness)
        {
            harness.NonPersistentBackend_DoesNotOfferRelaxedDurability();
            harness.Capabilities_SupportTheDedicatedWorker();
            await harness.Export_ReturnsACopy_NotAViewOfLiveStorage();
            await harness.Import_ReplacesAnExistingDatabaseEntirely();
            await harness.Admin_RejectsABlankDatabaseName(" ");
        }
    }

    [Fact]
    public async Task RejectsDurabilityPromisedWithoutPersistence()
        => await RejectsAsync(
            new LyingStorageProvider { IsPersistent = false, SupportsRelaxedDurability = true },
            harness => harness.NonPersistentBackend_DoesNotOfferRelaxedDurability());

    [Fact]
    public async Task RejectsAnAsyncBackendWithNoVfsModule()
        => await RejectsAsync(
            new LyingStorageProvider { RequiredBuild = BlazorSqliteEngineBuild.AsyncCapable },
            harness => harness.BackendNeedingTheAsyncBuild_ShipsAVfsModule());

    [Fact]
    public async Task RejectsABackendThatRunsNowhere()
        => await RejectsAsync(
            new LyingStorageProvider { SupportedContexts = BlazorSqliteExecutionContexts.None },
            harness => harness.Capabilities_DeclareAtLeastOneExecutionContext());

    [Fact]
    public async Task RejectsABackendThatCannotRunInTheWorker()
        => await RejectsAsync(
            new LyingStorageProvider { SupportedContexts = BlazorSqliteExecutionContexts.Window },
            harness => harness.Capabilities_SupportTheDedicatedWorker());

    [Fact]
    public async Task RejectsABlankName()
        => await RejectsAsync(
            new LyingStorageProvider { Name = "  " },
            harness => harness.Name_IsNotBlank());

    [Fact]
    public async Task RejectsAnExportThatAliasesLiveStorage()
        => await RejectsAsync(
            new LyingStorageProvider { ExportsLiveArray = true },
            async harness => await harness.Export_ReturnsACopy_NotAViewOfLiveStorage());

    [Fact]
    public async Task RejectsAnImportThatLeavesTheOldTailBehind()
        => await RejectsAsync(
            new LyingStorageProvider { OverwritesInPlace = true },
            async harness => await harness.Import_ReplacesAnExistingDatabaseEntirely());

    [Fact]
    public async Task RejectsAnAdminThatAcceptsABlankName()
        => await RejectsAsync(
            new LyingStorageProvider { SkipsNameValidation = true },
            async harness => await harness.Admin_RejectsABlankDatabaseName(""));
}
