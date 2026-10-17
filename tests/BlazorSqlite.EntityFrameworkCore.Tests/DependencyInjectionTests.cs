using BlazorSqlite.Data;
using BlazorSqlite.EntityFrameworkCore;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage;
using BlazorSqlite.Storage.InMemory;
using BlazorSqlite.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

/// <summary>
/// The registration surface an application actually calls: <c>AddBlazorSqlite</c>,
/// <c>AddDatabase</c>, and <c>AddBlazorSqliteDbContext</c>. The worker transport is replaced by
/// the in-process one so the whole composition runs on desktop .NET.
/// </summary>
public sealed class DependencyInjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(Action<BlazorSqliteDbContextOptions<ProductContext>>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddBlazorSqlite(sqlite => sqlite
            .AddStorageProvider<InMemoryStorageProvider>()
            .UseStorage(s => s.Prefer(InMemoryStorageProvider.ProviderName))
            .UseBindingStore<InMemoryStorageBindingStore>()
            .AddDatabase("di.db"));

        // Desktop has no worker; the in-process engine stands in for it.
        services.AddSingleton<ISqliteTransportFactory>(new InProcessTransportFactory());

        services.AddBlazorSqliteDbContext<ProductContext>(options =>
        {
            options.Schema = BlazorSqliteSchemaStrategy.EnsureCreated;
            options.Configure = (_, builder) => builder.ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
            configure?.Invoke(options);
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AddBlazorSqlite_RegistersTheCompositionRoot()
    {
        await using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<IBlazorSqliteSessionFactory>());
        Assert.NotNull(provider.GetRequiredService<StorageProviderResolver>());
        Assert.NotNull(provider.GetRequiredService<BlazorSqliteStorageSelection>());
        Assert.Equal("di.db", provider.GetRequiredService<BlazorSqliteDatabase>().Name);
        Assert.Single(provider.GetServices<IBlazorSqliteStorageProvider>());
    }

    [Fact]
    public void AddBlazorSqlite_WithoutAPreference_FailsAtRegistration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddBlazorSqlite(sqlite => sqlite.AddStorageProvider<InMemoryStorageProvider>()));

        Assert.Contains("UseStorage", error.Message);
    }

    [Fact]
    public async Task ContextFactory_OpensTheDatabase_PreparesTheSchemaOnce_AndHandsOutContexts()
    {
        var firstUse = 0;
        await using var provider = Build(o => o.OnFirstUse = (_, _) =>
        {
            firstUse++;
            return Task.CompletedTask;
        });

        var factory = provider.GetRequiredService<IBlazorSqliteDbContextFactory<ProductContext>>();
        Assert.False(factory.Database.IsOpen);

        await using (var first = await factory.CreateDbContextAsync(Ct))
        {
            first.Categories.Add(new Category { Name = "Tools" });
            await first.SaveChangesAsync(Ct);
        }

        await using (var second = await factory.CreateDbContextAsync(Ct))
        {
            Assert.Equal(1, await second.Categories.CountAsync(Ct));
        }

        Assert.True(factory.Database.IsOpen);
        Assert.Equal(1, firstUse);

        // The generic EF factory interface resolves to the same instance.
        Assert.Same(factory, provider.GetRequiredService<IDbContextFactory<ProductContext>>());
    }

    [Fact]
    public async Task ContextFactory_SynchronousCreate_WorksOnceOpen_AndExplainsOtherwise()
    {
        await using var provider = Build();
        var factory = provider.GetRequiredService<IBlazorSqliteDbContextFactory<ProductContext>>();

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateDbContext());
        Assert.Contains("CreateDbContextAsync", error.Message);

        await using var _ = await factory.CreateDbContextAsync(Ct);
        using var sync = factory.CreateDbContext();
        Assert.NotNull(sync);
    }

    [Fact]
    public async Task Database_Close_RaisesClosing_AndReopenPreparesAgain()
    {
        var firstUse = 0;
        await using var provider = Build(o => o.OnFirstUse = (_, _) =>
        {
            firstUse++;
            return Task.CompletedTask;
        });

        var database = provider.GetRequiredService<BlazorSqliteDatabase>();
        var factory = provider.GetRequiredService<IBlazorSqliteDbContextFactory<ProductContext>>();
        var closing = 0;
        var opened = 0;
        database.Closing += () => { closing++; return Task.CompletedTask; };
        database.Opened += () => { opened++; return Task.CompletedTask; };

        await using (var _ = await factory.CreateDbContextAsync(Ct))
        {
        }

        await database.CloseAsync(Ct);
        Assert.False(database.IsOpen);

        await using (var _ = await factory.CreateDbContextAsync(Ct))
        {
        }

        Assert.Equal(1, closing);
        Assert.Equal(2, opened);
        Assert.Equal(2, firstUse);
    }

    [Fact]
    public async Task Database_WhileClosed_RunsWorkWithTheSessionGone_ThenReopens()
    {
        await using var provider = Build();
        var database = provider.GetRequiredService<BlazorSqliteDatabase>();
        await database.OpenAsync(Ct);

        var sawClosed = false;
        await database.WhileClosedAsync(_ =>
        {
            sawClosed = !database.IsOpen;
            return Task.CompletedTask;
        }, Ct);

        Assert.True(sawClosed);
        Assert.True(database.IsOpen);
    }

    private sealed class InProcessTransportFactory : ISqliteTransportFactory
    {
        public ISqliteTransport Create(IBlazorSqliteStorageProvider provider) => new InProcessSqliteTransport();
    }
}
