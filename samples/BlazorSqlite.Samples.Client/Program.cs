using BlazorSqlite.Samples.Client;
using BlazorSqlite;
using BlazorSqlite.Interop;
using BlazorSqlite.Storage;
using BlazorSqlite.Storage.CacheStorage;
using BlazorSqlite.Storage.InMemory;
using BlazorSqlite.Storage.IndexedDb;
using BlazorSqlite.Storage.Opfs;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<SampleApp>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});

builder.Services.AddSingleton<IBlazorSqliteStorageBindingStore>(sp =>
    new BlazorSqliteLocalStorageBindingStore(sp.GetRequiredService<IJSRuntime>()));

builder.Services.AddSingleton<IReadOnlyList<IBlazorSqliteStorageProvider>>(sp =>
{
    var js = sp.GetRequiredService<IJSRuntime>();
    return new IBlazorSqliteStorageProvider[]
    {
        new BlazorSqliteOpfsStorageProvider(js),
        new BlazorSqliteIndexedDbStorageProvider(js),
        new BlazorSqliteCacheStorageProvider(js),
        new BlazorSqliteInMemoryStorageProvider(),
    };
});

builder.Services.AddSingleton(sp =>
{
    var js = sp.GetRequiredService<IJSRuntime>();
    var providers = sp.GetRequiredService<IReadOnlyList<IBlazorSqliteStorageProvider>>();
    var bindings = sp.GetRequiredService<IBlazorSqliteStorageBindingStore>();
    return new BlazorSqliteSessionFactory(
        new BlazorSqliteStorageProviderResolver(providers, bindings),
        new BlazorSqliteWorkerTransportFactory(js),
        BlazorSqliteStorageSelectionBuilder.Create(s => s
            .Prefer(BlazorSqliteOpfsStorageProvider.ProviderName)
            .Fallback(BlazorSqliteIndexedDbStorageProvider.ProviderName)
            .Fallback(BlazorSqliteCacheStorageProvider.ProviderName)
            .Fallback(BlazorSqliteInMemoryStorageProvider.ProviderName)
            .AllowNonPersistentFallback()),
        bindings);
});
builder.Services.AddSingleton<BrowserDatabase>();

TaskScheduler.UnobservedTaskException += (_, args) => SampleLog.Exception(args.Exception);
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    if (args.ExceptionObject is Exception exception)
    {
        SampleLog.Exception(exception);
    }
    else
    {
        Console.Error.WriteLine($"[BlazorSqlite sample] Unhandled: {args.ExceptionObject}");
    }
};

await builder.Build().RunAsync();
