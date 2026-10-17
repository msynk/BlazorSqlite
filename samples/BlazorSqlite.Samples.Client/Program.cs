using BlazorSqlite.Samples.Client;
using BlazorSqlite.Samples.Data;
using BlazorSqlite.Storage.CacheStorage;
using BlazorSqlite.Storage.InMemory;
using BlazorSqlite.Storage.IndexedDb;
using BlazorSqlite.Storage.Opfs;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<SampleApp>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});

// One call registers selection, sticky bindings in localStorage, the worker transport, and the
// shared database. Existing data outranks preference: a file created on IndexedDB stays there
// even once OPFS becomes available, unless the application migrates it deliberately.
builder.Services.AddBlazorSqlite(sqlite => sqlite
    .AddStorageProvider<OpfsStorageProvider>()
    .AddStorageProvider<IndexedDbStorageProvider>()
    .AddStorageProvider<CacheStorageProvider>()
    .AddStorageProvider<InMemoryStorageProvider>()
    .UseStorage(s => s
        .Prefer(OpfsStorageProvider.ProviderName)
        .Fallback(IndexedDbStorageProvider.ProviderName)
        .Fallback(CacheStorageProvider.ProviderName)
        .Fallback(InMemoryStorageProvider.ProviderName)
        .AllowNonPersistentFallback())
    .AddDatabase(BrowserDatabase.DatabaseName));

// Contexts come from a factory bound to that database. Migrations run once per session, then
// the demo catalog is seeded if the database is empty.
builder.Services.AddBlazorSqliteDbContext<AppDbContext>(options =>
    options.OnFirstUse = (db, ct) => DemoData.SeedIfEmptyAsync(db, ct));

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
