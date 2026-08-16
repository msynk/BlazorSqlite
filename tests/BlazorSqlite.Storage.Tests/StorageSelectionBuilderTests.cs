using Xunit;

namespace BlazorSqlite.Storage.Tests;

/// <summary>
/// Configuration mistakes here are silent in production - the wrong backend or the wrong order - so
/// the builder rejects them at the point they are written instead.
/// </summary>
public sealed class StorageSelectionBuilderTests
{
    [Fact]
    public void CandidateOrder_IsThePreferenceOrder()
    {
        var selection = BlazorSqliteStorageSelectionBuilder.Create(
            s => s.Prefer("opfs").Fallback("indexeddb").Fallback("cache-storage"));

        Assert.Equal(["opfs", "indexeddb", "cache-storage"], selection.Candidates);
        Assert.False(selection.IsStrict);
    }

    [Fact]
    public void SingleCandidate_IsStrict()
    {
        var selection = BlazorSqliteStorageSelectionBuilder.Create(s => s.Prefer("opfs"));

        Assert.True(selection.IsStrict);
    }

    [Fact]
    public void Defaults_AreTheConservativeChoices()
    {
        var selection = BlazorSqliteStorageSelectionBuilder.Create(s => s.Prefer("opfs"));

        Assert.False(selection.AllowNonPersistentFallback);
        Assert.Equal(StorageMigrationMode.KeepExisting, selection.MigrationMode);
    }

    [Fact]
    public void PreferringTwice_PointsAtFallback()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => BlazorSqliteStorageSelectionBuilder.Create(s => s.Prefer("opfs").Prefer("indexeddb")));

        Assert.Contains("Fallback(\"indexeddb\")", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackWithoutPrefer_PointsAtPrefer()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => BlazorSqliteStorageSelectionBuilder.Create(s => s.Fallback("opfs")));

        Assert.Contains("Prefer(\"opfs\")", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatingACandidate_IsRejected()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => BlazorSqliteStorageSelectionBuilder.Create(
                s => s.Prefer("opfs").Fallback("OPFS")));

        Assert.Contains("already a candidate", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingNothing_IsRejected()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => BlazorSqliteStorageSelectionBuilder.Create(_ => { }));

        Assert.Contains("No storage provider was selected", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationMode_IsCarriedThrough()
    {
        var selection = BlazorSqliteStorageSelectionBuilder.Create(
            s => s.Prefer("opfs").WithMigrationMode(StorageMigrationMode.AutomaticOnOpen));

        Assert.Equal(StorageMigrationMode.AutomaticOnOpen, selection.MigrationMode);
    }
}
