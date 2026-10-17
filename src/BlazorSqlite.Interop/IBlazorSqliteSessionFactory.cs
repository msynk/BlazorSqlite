namespace BlazorSqlite.Interop;

/// <summary>
/// Opens databases: resolves a storage backend, starts a worker against it, and hands back the
/// open connection together with the reasons for the choice.
/// </summary>
/// <remarks>
/// Registered by <c>AddBlazorSqlite</c>. Applications that need one database per session should
/// prefer <see cref="BlazorSqliteDatabase"/>, which caches the session and owns its lifetime.
/// </remarks>
public interface IBlazorSqliteSessionFactory
{
    /// <summary>Opens <paramref name="databaseName"/> on the backend selection chooses.</summary>
    Task<BlazorSqliteSession> OpenAsync(string databaseName, CancellationToken cancellationToken = default);
}
