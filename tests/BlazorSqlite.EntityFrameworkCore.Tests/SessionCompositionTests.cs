using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage;
using BlazorSqlite.Storage.InMemory;
using BlazorSqlite.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

/// <summary>
/// The composition <c>UseBlazorSqlite</c> is documented as: resolve and open through
/// <see cref="BlazorSqliteSessionFactory"/>, then hand the session's connection to EF.
/// </summary>
public sealed class SessionCompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SessionFactory_ThenUseBlazorSqlite_MigratesAndQueries()
    {
        var provider = new BlazorSqliteInMemoryStorageProvider();
        var store = new BlazorSqliteInMemoryStorageBindingStore();
        var factory = new BlazorSqliteSessionFactory(
            new BlazorSqliteStorageProviderResolver([provider], store),
            new InProcessTransportFactory(),
            BlazorSqliteStorageSelectionBuilder.Create(s => s.Prefer(BlazorSqliteInMemoryStorageProvider.ProviderName)),
            store);

        await using var session = await factory.OpenAsync("app.db", Ct);
        await using var ctx = new ProductContext(new DbContextOptionsBuilder<ProductContext>()
            .UseBlazorSqlite(session.Connection)
            .Options);

        await ctx.Database.MigrateAsync(Ct);

        ctx.Categories.Add(new Category { Name = "Tools" });
        ctx.Products.Add(new Product { Name = "Widget", Price = 4.50m, CategoryId = 1 });
        await ctx.SaveChangesAsync(Ct);
        ctx.ChangeTracker.Clear();

        var loaded = await ctx.Products.Include(p => p.Category).SingleAsync(Ct);
        Assert.Equal("Widget", loaded.Name);
        Assert.Equal("Tools", loaded.Category!.Name);
        Assert.Equal(BlazorSqliteInMemoryStorageProvider.ProviderName, await store.GetProviderNameAsync("app.db", Ct));
    }

    private sealed class InProcessTransportFactory : IBlazorSqliteTransportFactory
    {
        public IBlazorSqliteTransport Create(IBlazorSqliteStorageProvider provider)
            => new BlazorSqliteInProcessTransport();
    }
}
