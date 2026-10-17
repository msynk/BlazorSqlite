using BlazorSqlite.Interop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorSqlite.EntityFrameworkCore;

/// <summary>
/// Creates contexts bound to the application's <see cref="BlazorSqliteDatabase"/>, opening it on
/// first use and - when asked - applying migrations once per session.
/// </summary>
/// <typeparam name="TContext">A context with a constructor taking <see cref="DbContextOptions{TContext}"/>.</typeparam>
/// <remarks>
/// Opening a database is asynchronous in the browser, so the factory's main entry point is
/// <see cref="CreateDbContextAsync"/>. The synchronous <c>CreateDbContext</c> works only
/// once the database has been opened by an earlier call, and says so otherwise.
/// </remarks>
public interface IBlazorSqliteDbContextFactory<TContext> : IDbContextFactory<TContext>
    where TContext : DbContext
{
    /// <summary>The database every context this factory creates is bound to.</summary>
    BlazorSqliteDatabase Database { get; }

    /// <summary>Opens the database if needed, runs the configured first-use step, and returns a context.</summary>
    new Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default);
}

/// <summary>How <see cref="IBlazorSqliteDbContextFactory{TContext}"/> prepares the schema the first time a session opens.</summary>
public enum BlazorSqliteSchemaStrategy
{
    /// <summary>Do nothing; the application manages the schema itself.</summary>
    None,

    /// <summary>Call <c>Database.MigrateAsync()</c> once per session, applying pending migrations.</summary>
    Migrate,

    /// <summary>Call <c>Database.EnsureCreatedAsync()</c> once per session.</summary>
    EnsureCreated,
}

/// <summary>Options for <see cref="IBlazorSqliteDbContextFactory{TContext}"/>.</summary>
public sealed class BlazorSqliteDbContextOptions<TContext>
    where TContext : DbContext
{
    /// <summary>What to do to the schema the first time a context is created per session.</summary>
    public BlazorSqliteSchemaStrategy Schema { get; set; } = BlazorSqliteSchemaStrategy.Migrate;

    /// <summary>Further configuration of the options every context is built with.</summary>
    public Action<IServiceProvider, DbContextOptionsBuilder<TContext>>? Configure { get; set; }

    /// <summary>
    /// Runs after the schema step, once per session, with a context of its own: the place to seed
    /// demo data or run integrity checks.
    /// </summary>
    public Func<TContext, CancellationToken, Task>? OnFirstUse { get; set; }
}

internal sealed class BlazorSqliteDbContextFactory<TContext> : IBlazorSqliteDbContextFactory<TContext>
    where TContext : DbContext
{
    private readonly IServiceProvider _services;
    private readonly BlazorSqliteDbContextOptions<TContext> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BlazorSqliteSession? _preparedFor;

    public BlazorSqliteDbContextFactory(
        IServiceProvider services,
        BlazorSqliteDatabase database,
        BlazorSqliteDbContextOptions<TContext> options)
    {
        _services = services;
        Database = database;
        _options = options;
        database.Closing += OnClosingAsync;
    }

    public BlazorSqliteDatabase Database { get; }

    public TContext CreateDbContext()
    {
        var session = Database.Session
            ?? throw new InvalidOperationException(
                $"The database '{Database.Name}' is not open. Call CreateDbContextAsync() at least once "
                + "first - opening a database in the browser is asynchronous - or open it with "
                + "BlazorSqliteDatabase.OpenAsync().");

        return Build(session);
    }

    public async Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var session = await Database.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!ReferenceEquals(_preparedFor, session))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_preparedFor, session))
                {
                    await PrepareAsync(session, cancellationToken).ConfigureAwait(false);
                    _preparedFor = session;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return Build(session);
    }

    Task<TContext> IDbContextFactory<TContext>.CreateDbContextAsync(CancellationToken cancellationToken)
        => CreateDbContextAsync(cancellationToken);

    private async Task PrepareAsync(BlazorSqliteSession session, CancellationToken cancellationToken)
    {
        if (_options.Schema == BlazorSqliteSchemaStrategy.None && _options.OnFirstUse is null)
        {
            return;
        }

        await using var context = Build(session);

        switch (_options.Schema)
        {
            case BlazorSqliteSchemaStrategy.Migrate:
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                break;

            case BlazorSqliteSchemaStrategy.EnsureCreated:
                await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                break;
        }

        if (_options.OnFirstUse is { } firstUse)
        {
            await firstUse(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private TContext Build(BlazorSqliteSession session)
    {
        var builder = new DbContextOptionsBuilder<TContext>()
            .UseApplicationServiceProvider(_services)
            .UseBlazorSqlite(session.Connection);

        _options.Configure?.Invoke(_services, builder);

        return ActivatorUtilities.CreateInstance<TContext>(_services, builder.Options);
    }

    private Task OnClosingAsync()
    {
        _preparedFor = null;
        return Task.CompletedTask;
    }
}
