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

    [Fact]
    public async Task SynchronousSchemaApis_StillThrow()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = ContextFactory.Create(transport);

        Assert.Throws<BlazorSqliteSynchronousApiNotSupportedException>(
            () => ctx.Database.EnsureCreated());
    }
}
