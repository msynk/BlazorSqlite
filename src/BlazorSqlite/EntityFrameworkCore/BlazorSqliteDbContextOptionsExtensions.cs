using System.Data.Common;
using BlazorSqlite.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Registers the stock SQLite provider against a <see cref="DbConnection"/> BlazorSqlite owns,
/// replacing the two services that assume synchronous I/O.
/// </summary>
public static class BlazorSqliteDbContextOptionsExtensions
{
    /// <summary>
    /// Configures the context to use BlazorSqlite through <paramref name="connection"/>.
    /// </summary>
    /// <remarks>
    /// This is <c>UseSqlite(connection)</c> plus replacements for
    /// <see cref="IRelationalDatabaseCreator"/> and <see cref="IHistoryRepository"/>. SQL generation
    /// is the stock provider's; the replacements exist only so <c>EnsureCreatedAsync</c> and
    /// <c>MigrateAsync</c> do not call <c>DbConnection.Open()</c>.
    /// </remarks>
    public static DbContextOptionsBuilder UseBlazorSqlite(
        this DbContextOptionsBuilder optionsBuilder,
        DbConnection connection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        => optionsBuilder.UseBlazorSqlite(connection, contextOwnsConnection: false, sqliteOptionsAction);

    /// <summary>
    /// Configures the context to use BlazorSqlite through <paramref name="connection"/>.
    /// </summary>
    /// <remarks>
    /// When <paramref name="contextOwnsConnection"/> is <see langword="true"/>, disposing the
    /// context disposes the connection. Leave it <see langword="false"/> when the connection comes
    /// from a <c>BlazorSqliteSession</c> - the session owns the transport, and an ADO.NET close is
    /// bookkeeping only.
    /// </remarks>
    public static DbContextOptionsBuilder UseBlazorSqlite(
        this DbContextOptionsBuilder optionsBuilder,
        DbConnection connection,
        bool contextOwnsConnection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);

        // EF Core 10 reads new SqliteConnection().ServerVersion while compiling queries.
        // That requires a PCL provider; WASM has no e_sqlite3 bundle.
        BrowserSqlitePcl.EnsureInitialized();
        optionsBuilder.UseSqlite(connection, contextOwnsConnection, sqliteOptionsAction);
        ReplaceSyncBoundServices(optionsBuilder);
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseBlazorSqlite(DbContextOptionsBuilder, DbConnection, Action{SqliteDbContextOptionsBuilder}?)"/>
    public static DbContextOptionsBuilder<TContext> UseBlazorSqlite<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        DbConnection connection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseBlazorSqlite(connection, sqliteOptionsAction);
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseBlazorSqlite(DbContextOptionsBuilder, DbConnection, bool, Action{SqliteDbContextOptionsBuilder}?)"/>
    public static DbContextOptionsBuilder<TContext> UseBlazorSqlite<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        DbConnection connection,
        bool contextOwnsConnection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder)
            .UseBlazorSqlite(connection, contextOwnsConnection, sqliteOptionsAction);
        return optionsBuilder;
    }

    private static void ReplaceSyncBoundServices(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IRelationalDatabaseCreator, BlazorSqliteDatabaseCreator>();
        optionsBuilder.ReplaceService<IHistoryRepository, BlazorSqliteHistoryRepository>();
    }
}
