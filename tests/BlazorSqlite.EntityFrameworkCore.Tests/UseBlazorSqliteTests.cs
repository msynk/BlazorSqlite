using BlazorSqlite.Data;
using BlazorSqlite.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

/// <summary>
/// <c>UseBlazorSqlite</c> is the stock SQLite provider plus two service replacements. These tests
/// pin that shape so a future change cannot quietly become a custom provider.
/// </summary>
public sealed class UseBlazorSqliteTests
{
    [Fact]
    public void ProviderName_IsStockSqlite()
    {
        using var stock = ContextFactory.CreateStock();
        using var ours = ContextFactory.Create(new BlazorSqliteInProcessTransport());

        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", stock.Database.ProviderName);
        Assert.Equal(stock.Database.ProviderName, ours.Database.ProviderName);
    }

    [Fact]
    public void ReplacesTheTwoSyncBoundServices()
    {
        using var ctx = ContextFactory.Create(new BlazorSqliteInProcessTransport());

        Assert.IsType<BlazorSqliteDatabaseCreator>(ctx.GetService<IRelationalDatabaseCreator>());
        Assert.IsType<BlazorSqliteHistoryRepository>(ctx.GetService<IHistoryRepository>());
    }

    [Fact]
    public async Task StockUseSqlite_EnsureCreatedAsync_StillThrows()
    {
        // The reason the veneer exists: UseSqlite(connection) alone reaches Open() through
        // RelationalDatabaseCreator.CreateAsync. If that ever becomes async in EF, this test
        // fails and the replacements can be re-evaluated.
        await using var transport = new BlazorSqliteInProcessTransport();
        await using var ctx = new ProductContext(new DbContextOptionsBuilder<ProductContext>()
            .UseSqlite(new BlazorSqliteConnection(transport, "stock.db"))
            .Options);

        var failure = await Assert.ThrowsAsync<BlazorSqliteSynchronousApiNotSupportedException>(
            () => ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Open", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StockSqliteConnection_ServerVersion_IsAtLeastJsonFunctions()
    {
        // EF Core 10's query translator does this before compiling any query. On WASM there is
        // no e_sqlite3; UseBlazorSqlite installs a PCL stub so the call does not throw.
        var version = new Version(new Microsoft.Data.Sqlite.SqliteConnection().ServerVersion);
        Assert.True(version >= new Version(3, 38));
    }
}
