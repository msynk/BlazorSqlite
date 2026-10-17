using BlazorSqlite.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

/// <summary>
/// SQL generation never inspects the connection type, so <c>UseBlazorSqlite</c> must produce
/// SQL byte-identical to the stock provider. That is the §2 "same model both sides" guardrail.
/// </summary>
public sealed class SqlEquivalenceTests
{
    public static TheoryData<string, Func<ProductContext, IQueryable<object>>> Queries => new()
    {
        { "simple filter", ctx => ctx.Products.Where(p => p.Name == "widget") },
        { "decimal comparison", ctx => ctx.Products.Where(p => p.Price > 10m) },
        { "decimal arithmetic", ctx => ctx.Products.Where(p => p.Price * 2m < 100m) },
        { "order by decimal", ctx => ctx.Products.OrderBy(p => p.Price) },
        { "include navigation", ctx => ctx.Products.Include(p => p.Category) },
        {
            "group by with aggregate",
            ctx => ctx.Products
                .GroupBy(p => p.CategoryId)
                .Select(g => new { g.Key, Total = g.Sum(p => p.Price), Count = g.Count() })
        },
        {
            "projection with join",
            ctx => ctx.Products.Select(p => new { p.Name, Category = p.Category!.Name })
        },
        { "skip take", ctx => ctx.Products.OrderBy(p => p.Id).Skip(10).Take(5) },
        { "string contains", ctx => ctx.Products.Where(p => p.Name.Contains("wid")) },
        { "date filter", ctx => ctx.Products.Where(p => p.CreatedUtc > new DateTime(2026, 1, 1)) },
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task GeneratedSql_IsIdenticalToStockProvider(
        string name,
        Func<ProductContext, IQueryable<object>> query)
    {
        await using var transport = new BlazorSqliteInProcessTransport();
        using var stock = ContextFactory.CreateStock();
        using var ours = ContextFactory.Create(transport);

        var expected = query(stock).ToQueryString();
        var actual = query(ours).ToQueryString();

        Assert.Equal(expected, actual);
        Assert.False(string.IsNullOrWhiteSpace(actual), $"Query '{name}' produced no SQL.");
    }
}
