using BlazorSqlite;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers BlazorSqlite with dependency injection.</summary>
public static class BlazorSqliteServiceCollectionExtensions
{
    /// <summary>
    /// Registers the session factory, storage selection, sticky bindings, and the worker transport.
    /// Storage providers are added on the builder.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddBlazorSqlite(sqlite => sqlite
    ///     .AddStorageProvider&lt;BlazorSqliteOpfsStorageProvider&gt;()
    ///     .AddStorageProvider&lt;BlazorSqliteIndexedDbStorageProvider&gt;()
    ///     .AddStorageProvider&lt;BlazorSqliteInMemoryStorageProvider&gt;()
    ///     .UseStorage(s => s.Prefer("opfs").Fallback("indexeddb").Fallback("in-memory").AllowNonPersistentFallback())
    ///     .AddDatabase("app.db"));
    /// </code>
    /// </example>
    public static IServiceCollection AddBlazorSqlite(
        this IServiceCollection services,
        Action<BlazorSqliteBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new BlazorSqliteBuilder(services);
        configure(builder);
        builder.Complete();
        return services;
    }
}

/// <summary>Collects the choices <c>AddBlazorSqlite</c> turns into registrations.</summary>
public sealed class BlazorSqliteBuilder
{
    private readonly List<string> _onOpenSql = [];
    private readonly List<BlazorSqliteJsModule> _extensions = [];
    private BlazorSqliteStorageSelection? _selection;
    private string? _hostModuleUrl;
    private string? _workerUrl;
    private bool _enableForeignKeys = true;
    private bool _bindingStoreRegistered;

    internal BlazorSqliteBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Registers a storage provider. Its constructor may take <see cref="IJSRuntime"/>.</summary>
    public BlazorSqliteBuilder AddStorageProvider<TProvider>()
        where TProvider : class, IBlazorSqliteStorageProvider
    {
        Services.AddSingleton<IBlazorSqliteStorageProvider, TProvider>();
        return this;
    }

    /// <summary>Registers a storage provider built by <paramref name="factory"/>.</summary>
    public BlazorSqliteBuilder AddStorageProvider(Func<IServiceProvider, IBlazorSqliteStorageProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>Registers an already-built storage provider.</summary>
    public BlazorSqliteBuilder AddStorageProvider(IBlazorSqliteStorageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Services.AddSingleton(provider);
        return this;
    }

    /// <summary>
    /// Sets the preference order among the registered providers. Required: selection never guesses.
    /// </summary>
    public BlazorSqliteBuilder UseStorage(Action<BlazorSqliteStorageSelectionBuilder> configure)
    {
        _selection = BlazorSqliteStorageSelectionBuilder.Create(configure);
        return this;
    }

    /// <summary>Uses an already-built selection.</summary>
    public BlazorSqliteBuilder UseStorage(BlazorSqliteStorageSelection selection)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        return this;
    }

    /// <summary>
    /// Replaces the sticky-binding store. The default remembers bindings in <c>localStorage</c>,
    /// which is what production wants; tests use <see cref="BlazorSqliteInMemoryStorageBindingStore"/>.
    /// </summary>
    public BlazorSqliteBuilder UseBindingStore<TStore>()
        where TStore : class, IBlazorSqliteStorageBindingStore
    {
        Services.AddSingleton<IBlazorSqliteStorageBindingStore, TStore>();
        _bindingStoreRegistered = true;
        return this;
    }

    /// <summary>Replaces the sticky-binding store with <paramref name="store"/>.</summary>
    public BlazorSqliteBuilder UseBindingStore(IBlazorSqliteStorageBindingStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Services.AddSingleton(store);
        _bindingStoreRegistered = true;
        return this;
    }

    /// <summary>
    /// Registers a <see cref="BlazorSqliteDatabase"/> for <paramref name="databaseName"/>, injectable
    /// as the application's one shared database. Call once per database the application opens.
    /// </summary>
    public BlazorSqliteBuilder AddDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        Services.AddSingleton(sp => new BlazorSqliteDatabase(
            sp.GetRequiredService<IBlazorSqliteSessionFactory>(),
            databaseName));
        return this;
    }

    /// <summary>
    /// Whether every connection runs <c>PRAGMA foreign_keys = ON</c> on open. On by default, as
    /// with Microsoft.Data.Sqlite.
    /// </summary>
    public BlazorSqliteBuilder EnableForeignKeys(bool enable = true)
    {
        _enableForeignKeys = enable;
        return this;
    }

    /// <summary>Adds a statement to run on every open, after the provider's own configuration.</summary>
    public BlazorSqliteBuilder OnOpen(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        _onOpenSql.Add(sql);
        return this;
    }

    /// <summary>
    /// Adds a JavaScript module that registers user-defined functions, collations, or virtual
    /// tables on every open database. See <see cref="BlazorSqliteWorkerTransportOptions.ExtensionModules"/>.
    /// </summary>
    public BlazorSqliteBuilder AddExtensionModule(string moduleUrl, string registerExport = "register")
    {
        _extensions.Add(new BlazorSqliteJsModule(moduleUrl, registerExport));
        return this;
    }

    /// <summary>Overrides where the worker host module and worker script are served from.</summary>
    public BlazorSqliteBuilder UseWorker(string? hostModuleUrl = null, string? workerUrl = null)
    {
        _hostModuleUrl = hostModuleUrl;
        _workerUrl = workerUrl;
        return this;
    }

    internal void Complete()
    {
        if (_selection is null)
        {
            throw new InvalidOperationException(
                "AddBlazorSqlite needs a storage preference: call UseStorage(s => s.Prefer(\"opfs\")...).");
        }

        if (!_bindingStoreRegistered)
        {
            Services.TryAddSingleton<IBlazorSqliteStorageBindingStore>(sp =>
                new BlazorSqliteLocalStorageBindingStore(sp.GetRequiredService<IJSRuntime>()));
        }

        var selection = _selection;
        var defaults = new BlazorSqliteWorkerTransportDefaults
        {
            HostModuleUrl = _hostModuleUrl ?? BlazorSqliteWorkerTransport.VersionedHostModuleUrl,
            WorkerUrl = _workerUrl,
            EnableForeignKeys = _enableForeignKeys,
            OnOpenSql = [.. _onOpenSql],
            ExtensionModules = [.. _extensions],
        };

        Services.TryAddSingleton(selection);
        Services.TryAddSingleton(defaults);
        Services.TryAddSingleton<IBlazorSqliteTransportFactory>(sp =>
            new BlazorSqliteWorkerTransportFactory(sp.GetRequiredService<IJSRuntime>(), defaults));
        Services.TryAddSingleton<BlazorSqliteStorageMigrator>();
        Services.TryAddSingleton(sp => new BlazorSqliteStorageProviderResolver(
            sp.GetServices<IBlazorSqliteStorageProvider>(),
            sp.GetRequiredService<IBlazorSqliteStorageBindingStore>(),
            sp.GetService<ILogger<BlazorSqliteStorageProviderResolver>>()));
        Services.TryAddSingleton<IBlazorSqliteSessionFactory>(sp => new BlazorSqliteSessionFactory(
            sp.GetRequiredService<BlazorSqliteStorageProviderResolver>(),
            sp.GetRequiredService<IBlazorSqliteTransportFactory>(),
            sp.GetRequiredService<BlazorSqliteStorageSelection>(),
            sp.GetRequiredService<IBlazorSqliteStorageBindingStore>(),
            sp.GetRequiredService<BlazorSqliteStorageMigrator>(),
            sp.GetService<ILogger<BlazorSqliteSessionFactory>>()));
        Services.TryAddSingleton(sp => (BlazorSqliteSessionFactory)sp.GetRequiredService<IBlazorSqliteSessionFactory>());
    }
}
