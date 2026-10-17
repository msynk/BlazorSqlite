using Microsoft.JSInterop;

namespace BlazorSqlite.Interop;

/// <summary>
/// Remembers sticky bindings in <c>localStorage</c> so they survive a reload.
/// </summary>
public sealed class BlazorSqliteLocalStorageBindingStore(IJSRuntime js) : IBlazorSqliteStorageBindingStore
{
    private const string Prefix = "blazor-sqlite.binding.";

    public async ValueTask<string?> GetProviderNameAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        return await js
            .InvokeAsync<string?>("localStorage.getItem", cancellationToken, Prefix + databaseName)
            .ConfigureAwait(false);
    }

    public async ValueTask SetProviderNameAsync(
        string databaseName,
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        await js
            .InvokeVoidAsync("localStorage.setItem", cancellationToken, Prefix + databaseName, providerName)
            .ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        await js
            .InvokeVoidAsync("localStorage.removeItem", cancellationToken, Prefix + databaseName)
            .ConfigureAwait(false);
    }
}
