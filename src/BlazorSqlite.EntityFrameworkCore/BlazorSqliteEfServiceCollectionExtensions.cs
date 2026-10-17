using BlazorSqlite.EntityFrameworkCore;
using BlazorSqlite.Interop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers EF Core contexts on top of <c>AddBlazorSqlite</c>.</summary>
public static class BlazorSqliteEfServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IBlazorSqliteDbContextFactory{TContext}"/> (and
    /// <see cref="IDbContextFactory{TContext}"/>) for a context bound to the registered
    /// <see cref="BlazorSqliteDatabase"/>. Migrations are applied once per session unless
    /// <see cref="BlazorSqliteDbContextOptions{TContext}.Schema"/> says otherwise.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddBlazorSqlite(sqlite => sqlite
    ///     .AddStorageProvider&lt;OpfsStorageProvider&gt;()
    ///     .UseStorage(s => s.Prefer("opfs"))
    ///     .AddDatabase("app.db"));
    ///
    /// builder.Services.AddBlazorSqliteDbContext&lt;AppDbContext&gt;(o =>
    ///     o.OnFirstUse = (db, ct) => DemoData.SeedIfEmptyAsync(db, ct));
    ///
    /// // In a component:
    /// await using var db = await Factory.CreateDbContextAsync();
    /// </code>
    /// </example>
    public static IServiceCollection AddBlazorSqliteDbContext<TContext>(
        this IServiceCollection services,
        Action<BlazorSqliteDbContextOptions<TContext>>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new BlazorSqliteDbContextOptions<TContext>();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IBlazorSqliteDbContextFactory<TContext>>(sp =>
            new BlazorSqliteDbContextFactory<TContext>(
                sp,
                sp.GetRequiredService<BlazorSqliteDatabase>(),
                sp.GetRequiredService<BlazorSqliteDbContextOptions<TContext>>()));
        services.TryAddSingleton<IDbContextFactory<TContext>>(sp =>
            sp.GetRequiredService<IBlazorSqliteDbContextFactory<TContext>>());

        return services;
    }
}
