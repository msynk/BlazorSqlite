using Microsoft.JSInterop;

namespace BlazorSqlite.Interop;

/// <summary>
/// Asks the browser to keep origin storage out of the LRU eviction bucket.
/// </summary>
/// <remarks>
/// Not on the frozen admin contract - persistence is an origin setting, not a per-backend one.
/// </remarks>
public static class BlazorSqlitePersistence
{
    public static async Task<bool> RequestAsync(
        IJSRuntime js,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(js);
        var module = await js
            .InvokeAsync<IJSObjectReference>(
                "import",
                cancellationToken,
                "./_content/BlazorSqlite.Js/blazor-sqlite-persist.js")
            .ConfigureAwait(false);

        try
        {
            return await module
                .InvokeAsync<bool>("requestPersistence", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }
    }
}
