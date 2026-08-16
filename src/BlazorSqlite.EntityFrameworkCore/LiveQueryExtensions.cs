using System.Runtime.CompilerServices;
using BlazorSqlite.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore;

/// <summary>EF entry point for table-level live queries.</summary>
public static class LiveQueryExtensions
{
    /// <summary>
    /// Re-executes <paramref name="query"/> whenever a table it reads is written.
    /// </summary>
    public static ILiveQuery<IReadOnlyList<T>> AsLiveQuery<T>(this IQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return AsLiveQuery(query, ConnectionOf(query));
    }

    /// <summary>
    /// Re-executes <paramref name="query"/> whenever a table it reads is written.
    /// </summary>
    public static ILiveQuery<IReadOnlyList<T>> AsLiveQuery<T>(
        this IQueryable<T> query,
        BlazorSqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(connection);

        var tables = SqliteTableNames.Extract(query.ToQueryString());
        return new LiveQuery<IReadOnlyList<T>>(
            connection,
            async ct => await query.ToListAsync(ct).ConfigureAwait(false),
            tables);
    }

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> implements <c>IInfrastructure&lt;IServiceProvider&gt;</c>.
    /// <c>Where</c>/<c>OrderBy</c> replace it with <c>EntityQueryable</c>, whose provider holds
    /// the compiler that still knows the context.
    /// </summary>
    private static BlazorSqliteConnection ConnectionOf<T>(IQueryable<T> query)
    {
        var context = DbContextOf(query)
            ?? throw new InvalidOperationException(
                "AsLiveQuery() needs an EF query whose provider exposes the current DbContext.");

        if (context.Database.GetDbConnection() is BlazorSqliteConnection connection)
        {
            return connection;
        }

        throw new InvalidOperationException(
            "AsLiveQuery() requires the context to be using a BlazorSqliteConnection.");
    }

    private static DbContext? DbContextOf(IQueryable query)
        => ContextFromInfrastructure(query as IInfrastructure<IServiceProvider>)
            ?? ContextFromInfrastructure(query.Provider as IInfrastructure<IServiceProvider>)
            ?? ContextFromQueryProvider(query.Provider);

    private static DbContext? ContextFromInfrastructure(IInfrastructure<IServiceProvider>? accessor)
        => accessor?.GetService<ICurrentDbContext>()?.Context;

    private static DbContext? ContextFromQueryProvider(IQueryProvider provider)
    {
        if (provider is not EntityQueryProvider entityProvider)
        {
            return null;
        }

        var compiler = GetQueryCompiler(entityProvider);
        if (compiler is QueryCompiler queryCompiler)
        {
            return GetQueryContextFactory(queryCompiler).Create().Context;
        }

        return null;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_queryCompiler")]
    private static extern ref IQueryCompiler GetQueryCompiler(EntityQueryProvider provider);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_queryContextFactory")]
    private static extern ref IQueryContextFactory GetQueryContextFactory(QueryCompiler compiler);
}
