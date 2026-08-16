namespace BlazorSqlite;

/// <summary>
/// Builds a <see cref="BlazorSqliteStorageSelection"/>, as used by
/// <c>UseStorage(s => s.Prefer("opfs").Fallback("indexeddb"))</c>.
/// </summary>
public sealed class BlazorSqliteStorageSelectionBuilder
{
    private readonly List<string> _candidates = [];
    private bool _allowNonPersistentFallback;
    private StorageMigrationMode _migrationMode = StorageMigrationMode.KeepExisting;

    /// <summary>Sets the first choice. Calling it more than once is a configuration mistake.</summary>
    public BlazorSqliteStorageSelectionBuilder Prefer(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (_candidates.Count > 0)
        {
            throw new InvalidOperationException(
                $"A preferred storage provider is already set ('{_candidates[0]}'). "
                + $"Use Fallback(\"{providerName}\") to add it below the first choice.");
        }

        _candidates.Add(providerName);
        return this;
    }

    /// <summary>
    /// Adds the next choice, below everything already added. Order is the order given.
    /// </summary>
    public BlazorSqliteStorageSelectionBuilder Fallback(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (_candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Call Prefer(\"{providerName}\") first - a fallback needs something to fall back from.");
        }

        if (_candidates.Contains(providerName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Storage provider '{providerName}' is already a candidate. "
                + "Listing it twice cannot change the outcome.");
        }

        _candidates.Add(providerName);
        return this;
    }

    /// <summary>
    /// Permits falling back to a backend that does not survive a reload, accepting that data written
    /// in such a session is lost when the page closes.
    /// </summary>
    public BlazorSqliteStorageSelectionBuilder AllowNonPersistentFallback()
    {
        _allowNonPersistentFallback = true;
        return this;
    }

    /// <summary>Sets what happens when a better backend becomes available later.</summary>
    public BlazorSqliteStorageSelectionBuilder WithMigrationMode(StorageMigrationMode mode)
    {
        _migrationMode = mode;
        return this;
    }

    internal BlazorSqliteStorageSelection Build()
    {
        if (_candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No storage provider was selected. Call Prefer(...) with a registered provider name.");
        }

        return new BlazorSqliteStorageSelection(
            [.. _candidates],
            _allowNonPersistentFallback,
            _migrationMode);
    }

    /// <summary>Runs <paramref name="configure"/> and produces the selection it describes.</summary>
    public static BlazorSqliteStorageSelection Create(
        Action<BlazorSqliteStorageSelectionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new BlazorSqliteStorageSelectionBuilder();
        configure(builder);
        return builder.Build();
    }
}
