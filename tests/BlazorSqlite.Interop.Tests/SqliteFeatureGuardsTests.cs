using BlazorSqlite.Data;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

public sealed class SqliteFeatureGuardsTests
{
    [Theory]
    [InlineData("PRAGMA journal_mode=WAL")]
    [InlineData("pragma journal_mode = wal")]
    [InlineData("PRAGMA journal_mode='wal'")]
    [InlineData("PRAGMA journal_mode=\"WAL\"")]
    public void RejectsWalInAnyReasonableSpelling(string sql)
    {
        var error = Assert.Throws<BlazorSqliteException>(() => SqliteFeatureGuards.EnsureSupported(sql));

        Assert.Contains("WAL", error.Message);
    }

    [Theory]
    [InlineData("PRAGMA journal_mode=DELETE")]
    [InlineData("PRAGMA journal_mode=TRUNCATE")]
    [InlineData("SELECT journal_mode FROM pragma_journal_mode")]
    [InlineData("INSERT INTO t (name) VALUES ('walrus')")]
    [InlineData("ATTACH 'other.db' AS other")]
    [InlineData("PRAGMA page_size=4096")]
    public void DoesNotRejectUnrelatedSql(string sql)
        => SqliteFeatureGuards.EnsureSupported(sql);

    [Theory]
    [InlineData("ATTACH 'other.db' AS other")]
    [InlineData("attach database 'other.db' as other")]
    public void RejectsAttach_WhenTheBackendCannotSpanDatabases(string sql)
    {
        var limits = SqliteRuntimeLimits.Unrestricted with { SupportsMultiDatabaseTransactions = false };
        var error = Assert.Throws<BlazorSqliteException>(() => SqliteFeatureGuards.EnsureSupported(sql, limits));

        Assert.Contains("ATTACH", error.Message);
    }

    [Fact]
    public void RejectsPageSizeAssignment_WhenTheBackendPinsThePageSize()
    {
        var limits = SqliteRuntimeLimits.Unrestricted with { CanChangePageSize = false };
        var error = Assert.Throws<BlazorSqliteException>(
            () => SqliteFeatureGuards.EnsureSupported("PRAGMA page_size=8192", limits));

        Assert.Contains("page_size", error.Message);
    }

    [Theory]
    [InlineData("INSERT INTO t (name) VALUES ('attach')")]
    [InlineData("SELECT page_size FROM pragma_page_size")]
    [InlineData("PRAGMA page_size")]
    public void DoesNotFalsePositive_OnAttachOrPageSizeWords(string sql)
    {
        var limits = new SqliteRuntimeLimits
        {
            SupportsMultiDatabaseTransactions = false,
            CanChangePageSize = false,
        };

        SqliteFeatureGuards.EnsureSupported(sql, limits);
    }
}
