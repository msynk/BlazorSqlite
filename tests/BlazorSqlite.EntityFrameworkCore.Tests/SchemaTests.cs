using BlazorSqlite.Data;
using BlazorSqlite.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

/// <summary>
/// The schema paths the S1 audit found blocked - now the exit criterion for <c>UseBlazorSqlite</c>.
/// </summary>
public sealed class SchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnsureCreatedAsync_CreatesAUsableSchema()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = ContextFactory.Create(transport);

        Assert.True(await ctx.Database.EnsureCreatedAsync(Ct));

        ctx.Categories.Add(new Category { Name = "Tools" });
        ctx.Products.Add(new Product { Name = "Widget", Price = 9.99m, CategoryId = 1 });
        await ctx.SaveChangesAsync(Ct);
        ctx.ChangeTracker.Clear();

        var loaded = await ctx.Products.Include(p => p.Category).SingleAsync(Ct);
        Assert.Equal("Tools", loaded.Category!.Name);
    }

    [Fact]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = ContextFactory.Create(transport);

        Assert.True(await ctx.Database.EnsureCreatedAsync(Ct));
        Assert.False(await ctx.Database.EnsureCreatedAsync(Ct));
    }

    [Fact]
    public async Task MigrateAsync_AppliesMigrations_AndRecordsHistory()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = ContextFactory.Create(transport);

        Assert.Equal(["20260101000000_InitialCreate"], await ctx.Database.GetPendingMigrationsAsync(Ct));

        await ctx.Database.MigrateAsync(Ct);

        Assert.Equal(["20260101000000_InitialCreate"], await ctx.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync(Ct));

        ctx.Categories.Add(new Category { Name = "Tools" });
        ctx.Products.Add(new Product { Name = "Widget", Price = 12.34m, CategoryId = 1 });
        await ctx.SaveChangesAsync(Ct);
        ctx.ChangeTracker.Clear();

        var loaded = await ctx.Products.SingleAsync(Ct);
        Assert.Equal(12.34m, loaded.Price);
    }

    [Fact]
    public async Task MigrateAsync_IsIdempotent()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = ContextFactory.Create(transport);

        await ctx.Database.MigrateAsync(Ct);
        await ctx.Database.MigrateAsync(Ct);

        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task EnsureDeletedAsync_DropsUserTables_NotSqliteBookkeeping()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = ContextFactory.Create(transport);
        await ctx.Database.EnsureCreatedAsync(Ct);

        var deleted = await ctx.Database.EnsureDeletedAsync(Ct);

        Assert.True(deleted);
        Assert.True(await ctx.Database.EnsureCreatedAsync(Ct));
    }

    /// <summary>
    /// With foreign keys on, DROP TABLE runs an implicit DELETE that a referencing table can veto,
    /// and sqlite_master lists parents first. A view left behind would break the next
    /// EnsureCreated. Neither may stop the delete, and enforcement has to be back on afterwards
    /// because the connection lives on.
    /// </summary>
    [Fact]
    public async Task EnsureDeletedAsync_CopesWithReferencedRowsAndViews_AndLeavesForeignKeysOn()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = ContextFactory.Create(transport);
        await ctx.Database.EnsureCreatedAsync(Ct);

        await ctx.Database.ExecuteSqlRawAsync("CREATE TABLE parent (id INTEGER PRIMARY KEY)", Ct);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id))", Ct);
        await ctx.Database.ExecuteSqlRawAsync("INSERT INTO parent (id) VALUES (1)", Ct);
        await ctx.Database.ExecuteSqlRawAsync("INSERT INTO child (id, parent_id) VALUES (1, 1)", Ct);
        await ctx.Database.ExecuteSqlRawAsync("CREATE VIEW parents AS SELECT id FROM parent", Ct);

        Assert.True(await ctx.Database.EnsureDeletedAsync(Ct));

        Assert.True(await ctx.Database.EnsureCreatedAsync(Ct));
        Assert.Equal(0L, await ScalarAsync(ctx, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view'"));
        Assert.Equal(1L, await ScalarAsync(ctx, "PRAGMA foreign_keys"));
    }

    private static async Task<long> ScalarAsync(DbContext ctx, string sql)
    {
        var connection = ctx.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task SynchronousSchemaApis_StillThrow()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = ContextFactory.Create(transport);

        Assert.Throws<BlazorSqliteSynchronousApiNotSupportedException>(
            () => ctx.Database.EnsureCreated());
    }
}
