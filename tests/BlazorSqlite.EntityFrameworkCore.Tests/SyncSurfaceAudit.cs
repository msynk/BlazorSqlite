using BlazorSqlite.Data;
using BlazorSqlite.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

/// <summary>
/// The S1 audit, now a regression suite: every entry point an application actually calls is marked
/// usable or blocked, so a new sync-only EF path shows up as a failing expectation naming the frame.
/// </summary>
public sealed class SyncSurfaceAudit(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Expectation
    {
        Works,
        SyncBound,
    }

    private static async Task<ProductContext> CreateSeededAsync(InProcessSqliteTransport transport)
    {
        var ctx = ContextFactory.Create(transport, "audit.db");
        await ctx.Database.EnsureCreatedAsync(Ct);
        ctx.Categories.Add(new Category { Name = "Tools" });
        ctx.Products.Add(new Product { Name = "Widget", Price = 9.99m, CategoryId = 1 });
        await ctx.SaveChangesAsync(Ct);
        ctx.ChangeTracker.Clear();
        return ctx;
    }

    public static TheoryData<string, Expectation, Func<ProductContext, Task>> DataOperations => new()
    {
        { "ToListAsync", Expectation.Works, ctx => ctx.Products.ToListAsync(Ct) },
        { "FirstOrDefaultAsync", Expectation.Works, ctx => ctx.Products.FirstOrDefaultAsync(Ct) },
        { "CountAsync", Expectation.Works, ctx => ctx.Products.CountAsync(Ct) },
        { "AnyAsync", Expectation.Works, ctx => ctx.Products.AnyAsync(Ct) },
        { "Include + ToListAsync", Expectation.Works, ctx => ctx.Products.Include(p => p.Category).ToListAsync(Ct) },
        {
            "AsAsyncEnumerable",
            Expectation.Works,
            async ctx =>
            {
                await foreach (var _ in ctx.Products.AsAsyncEnumerable().WithCancellation(Ct))
                {
                }
            }
        },
        {
            "SaveChangesAsync (insert)",
            Expectation.Works,
            ctx =>
            {
                ctx.Products.Add(new Product { Name = "New", Price = 1m, CategoryId = 1 });
                return ctx.SaveChangesAsync(Ct);
            }
        },
        {
            "SaveChangesAsync (update)",
            Expectation.Works,
            async ctx =>
            {
                var product = await ctx.Products.FirstAsync(Ct);
                product.Name = "Renamed";
                await ctx.SaveChangesAsync(Ct);
            }
        },
        {
            "SaveChangesAsync (delete)",
            Expectation.Works,
            async ctx =>
            {
                ctx.Products.Remove(await ctx.Products.FirstAsync(Ct));
                await ctx.SaveChangesAsync(Ct);
            }
        },
        {
            "ExecuteUpdateAsync",
            Expectation.Works,
            ctx => ctx.Products.ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Bulk"), Ct)
        },
        { "ExecuteDeleteAsync", Expectation.Works, ctx => ctx.Products.ExecuteDeleteAsync(Ct) },
        {
            "FromSqlRaw + ToListAsync",
            Expectation.Works,
            ctx => ctx.Products.FromSqlRaw("SELECT * FROM Products").ToListAsync(Ct)
        },
        {
            "ExecuteSqlRawAsync",
            Expectation.Works,
            ctx => ctx.Database.ExecuteSqlRawAsync("UPDATE Products SET Name = 'raw'", Ct)
        },
        { "CanConnectAsync", Expectation.Works, ctx => ctx.Database.CanConnectAsync(Ct) },
        {
            "OpenConnectionAsync + CloseConnectionAsync",
            Expectation.Works,
            async ctx =>
            {
                await ctx.Database.OpenConnectionAsync(Ct);
                await ctx.Database.CloseConnectionAsync();
            }
        },
        {
            "BeginTransactionAsync + CommitAsync",
            Expectation.Works,
            async ctx =>
            {
                await using var tx = await ctx.Database.BeginTransactionAsync(Ct);
                ctx.Products.Add(new Product { Name = "Tx", Price = 1m, CategoryId = 1 });
                await ctx.SaveChangesAsync(Ct);
                await tx.CommitAsync(Ct);
            }
        },
        {
            "BeginTransactionAsync + RollbackAsync",
            Expectation.Works,
            async ctx =>
            {
                await using var tx = await ctx.Database.BeginTransactionAsync(Ct);
                ctx.Products.Add(new Product { Name = "Tx", Price = 1m, CategoryId = 1 });
                await ctx.SaveChangesAsync(Ct);
                await tx.RollbackAsync(Ct);
            }
        },
        {
            "Entry.ReloadAsync",
            Expectation.Works,
            async ctx =>
            {
                var product = await ctx.Products.FirstAsync(Ct);
                await ctx.Entry(product).ReloadAsync(Ct);
            }
        },
        {
            "Collection.LoadAsync",
            Expectation.Works,
            async ctx =>
            {
                var category = await ctx.Categories.FirstAsync(Ct);
                await ctx.Entry(category).Collection(c => c.Products).LoadAsync(Ct);
            }
        },
        {
            "GetAppliedMigrationsAsync",
            Expectation.Works,
            ctx => ctx.Database.GetAppliedMigrationsAsync(Ct)
        },
        {
            "GetPendingMigrationsAsync",
            Expectation.Works,
            ctx => ctx.Database.GetPendingMigrationsAsync(Ct)
        },
        { "ToList (sync)", Expectation.SyncBound, ctx => Task.FromResult(ctx.Products.ToList()) },
        { "Count (sync)", Expectation.SyncBound, ctx => Task.FromResult(ctx.Products.Count()) },
        {
            "SaveChanges (sync)",
            Expectation.SyncBound,
            ctx =>
            {
                ctx.Products.Add(new Product { Name = "Sync", Price = 1m, CategoryId = 1 });
                return Task.FromResult(ctx.SaveChanges());
            }
        },
    };

    public static TheoryData<string, Expectation, Func<ProductContext, Task>> SchemaOperations => new()
    {
        { "EnsureCreatedAsync", Expectation.Works, ctx => ctx.Database.EnsureCreatedAsync(Ct) },
        { "EnsureDeletedAsync", Expectation.Works, ctx => ctx.Database.EnsureDeletedAsync(Ct) },
        { "MigrateAsync", Expectation.Works, ctx => ctx.Database.MigrateAsync(Ct) },
        { "CanConnectAsync (virgin database)", Expectation.Works, ctx => ctx.Database.CanConnectAsync(Ct) },
        { "EnsureCreated (sync)", Expectation.SyncBound, ctx => Task.FromResult(ctx.Database.EnsureCreated()) },
    };

    [Theory]
    [MemberData(nameof(DataOperations))]
    public async Task DataOperation_MatchesExpectation(
        string name,
        Expectation expected,
        Func<ProductContext, Task> operation)
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = await CreateSeededAsync(transport);
        await AssertExpectationAsync(name, expected, ctx, operation);
    }

    [Theory]
    [MemberData(nameof(SchemaOperations))]
    public async Task SchemaOperation_MatchesExpectation(
        string name,
        Expectation expected,
        Func<ProductContext, Task> operation)
    {
        await using var transport = new InProcessSqliteTransport();
        await using var ctx = ContextFactory.Create(transport, "audit-schema.db");
        await AssertExpectationAsync(name, expected, ctx, operation);
    }

    private async Task AssertExpectationAsync(
        string name,
        Expectation expected,
        ProductContext ctx,
        Func<ProductContext, Task> operation)
    {
        var failure = await Record.ExceptionAsync(() => operation(ctx));

        if (expected == Expectation.SyncBound)
        {
            Assert.IsType<BlazorSqliteSynchronousApiNotSupportedException>(
                failure,
                exactMatch: false);

            output.WriteLine($"{name} is sync-bound via:");
            foreach (var frame in EfFrames(failure))
            {
                output.WriteLine($"    {frame}");
            }

            return;
        }

        if (failure is not null)
        {
            Assert.Fail($"'{name}' was expected to work on the async tier but threw:{Environment.NewLine}{failure}");
        }
    }

    private static IEnumerable<string> EfFrames(Exception failure)
        => (failure.StackTrace ?? string.Empty)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Contains("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            .Take(4);
}
