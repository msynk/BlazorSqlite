using BlazorSqlite.Samples.Data;
using BlazorSqlite.Samples.Domain;
using BlazorSqlite.Data;
using BlazorSqlite.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

/// <summary>
/// The sample's load-bearing claim: one migration set applies through <c>UseBlazorSqlite</c>.
/// </summary>
public sealed class SharedSampleModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SampleMigrations_ApplyAndRoundTrip()
    {
        await using var transport = new InProcessSqliteTransport();
        await using var connection = new BlazorSqliteConnection(transport, "sample.db");
        await connection.OpenAsync(Ct);
        await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseBlazorSqlite(connection)
            .Options);

        await ctx.Database.MigrateAsync(Ct);
        await DemoData.SeedIfEmptyAsync(ctx, Ct);

        var product = await ctx.Products.Include(p => p.Category).SingleAsync(p => p.Sku == "TL-100", Ct);
        Assert.Equal("Digital caliper", product.Name);
        Assert.Equal(42.50m, product.Price);
        Assert.Equal(TimeSpan.FromDays(2), product.LeadTime);
        Assert.Equal("Tools", product.Category!.Name);

        var customer = await ctx.Customers.SingleAsync(c => c.Email == "stores@northwind.example", Ct);
        Assert.Equal(new DateOnly(1978, 4, 12), customer.DateOfBirth);
        Assert.True(customer.IsVip);
        Assert.Equal(25_000m, customer.CreditLimit);

        var order = await ctx.Orders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Product)
            .SingleAsync(o => o.Number == "SO-1042", Ct);
        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.Equal(2, order.Lines.Count);
        Assert.Contains(order.Lines, l => l.Product!.Sku == "TL-100");
    }
}
