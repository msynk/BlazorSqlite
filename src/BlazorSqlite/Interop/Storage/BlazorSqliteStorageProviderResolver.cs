using BlazorSqlite.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorSqlite.Interop;

/// <summary>
/// Decides which storage backend serves a database, honouring configured preference but never at the
/// cost of data that already exists somewhere else.
/// </summary>
/// <remarks>
/// <para>
/// Two rules shape everything here. First, existing data outranks preference: if a database was
/// created on one backend, that backend is used, and if it cannot be reached the open fails rather
/// than quietly producing an empty database elsewhere. Second, nothing is substituted silently - a
/// fallback is logged, a non-persistent fallback needs an explicit opt-in, and total failure reports
/// every candidate's fate.
/// </para>
/// <para>
/// <see cref="ResolveAsync"/> makes no changes, so it is safe to call for diagnostics. The chosen
/// backend is recorded only when the caller confirms the database really opened, via
/// <see cref="CommitBindingAsync"/>.
/// </para>
/// </remarks>
public sealed class BlazorSqliteStorageProviderResolver
{
    private readonly Dictionary<string, IBlazorSqliteStorageProvider> _providers;
    private readonly IBlazorSqliteStorageBindingStore _bindingStore;
    private readonly ILogger _logger;
    private readonly Dictionary<string, BlazorSqliteProbeResult> _probeCache =
        new(StringComparer.OrdinalIgnoreCase);

    public BlazorSqliteStorageProviderResolver(
        IEnumerable<IBlazorSqliteStorageProvider> providers,
        IBlazorSqliteStorageBindingStore bindingStore,
        ILogger<BlazorSqliteStorageProviderResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(bindingStore);

        _bindingStore = bindingStore;
        _logger = logger ?? NullLogger<BlazorSqliteStorageProviderResolver>.Instance;
        _providers = new Dictionary<string, IBlazorSqliteStorageProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            if (!_providers.TryAdd(provider.Name, provider))
            {
                throw new InvalidOperationException(
                    $"Two storage providers are registered under the name '{provider.Name}'. "
                    + "Provider names identify where a database lives, so they must be unique.");
            }
        }
    }

    /// <summary>Chooses a backend for <paramref name="databaseName"/> without changing anything.</summary>
    /// <exception cref="BlazorSqliteStorageUnavailableException">
    /// No candidate could be used, or the database exists on a backend that cannot be reached.
    /// </exception>
    public async ValueTask<BlazorSqliteStorageResolution> ResolveAsync(
        string databaseName,
        BlazorSqliteStorageSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(selection);

        var boundProviderName = await _bindingStore
            .GetProviderNameAsync(databaseName, cancellationToken)
            .ConfigureAwait(false);

        return boundProviderName is null
            ? await ResolveNewDatabaseAsync(databaseName, selection, cancellationToken).ConfigureAwait(false)
            : await ResolveExistingDatabaseAsync(databaseName, boundProviderName, selection, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Records the resolved backend as the home of the database. Call this once the database has
    /// actually opened, so a failed open does not leave a binding pointing at nothing.
    /// </summary>
    public ValueTask CommitBindingAsync(
        BlazorSqliteStorageResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        return resolution.WasDecidedByExistingData
            ? ValueTask.CompletedTask
            : _bindingStore.SetProviderNameAsync(
                resolution.DatabaseName,
                resolution.Provider.Name,
                cancellationToken);
    }

    private async ValueTask<BlazorSqliteStorageResolution> ResolveExistingDatabaseAsync(
        string databaseName,
        string boundProviderName,
        BlazorSqliteStorageSelection selection,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(boundProviderName, out var bound))
        {
            // Falling through to preference order here would open a different, empty database and
            // report success, so the only safe answer is to stop and say what is missing.
            throw new BlazorSqliteStorageUnavailableException(
                databaseName,
                [
                    new BlazorSqliteStorageCandidateOutcome
                    {
                        ProviderName = boundProviderName,
                        Status = BlazorSqliteStorageCandidateStatus.NotRegistered,
                        Explanation =
                            $"Database '{databaseName}' was created by storage provider "
                            + $"'{boundProviderName}', which is not registered. Register it to reach "
                            + "the existing data, or delete the database to start again elsewhere.",
                    },
                ]);
        }

        var probe = await ProbeAsync(bound, cancellationToken).ConfigureAwait(false);

        if (!probe.IsAvailable)
        {
            throw new BlazorSqliteStorageUnavailableException(
                databaseName,
                [
                    new BlazorSqliteStorageCandidateOutcome
                    {
                        ProviderName = bound.Name,
                        Status = BlazorSqliteStorageCandidateStatus.Unavailable,
                        Probe = probe,
                        Explanation =
                            $"Database '{databaseName}' lives on storage provider '{bound.Name}', which "
                            + "is unavailable in this browser. BlazorSqlite will not open an empty "
                            + "database on another provider in its place.",
                    },
                ]);
        }

        var better = await FindBetterProviderAsync(bound.Name, selection, cancellationToken)
            .ConfigureAwait(false);

        if (better is not null)
        {
            _logger.LogInformation(
                "Database {DatabaseName} is on storage provider {Provider}, but {BetterProvider} is "
                + "now available and ranked higher. Migration mode is {MigrationMode}.",
                databaseName,
                bound.Name,
                better.Name,
                selection.MigrationMode);
        }

        return new BlazorSqliteStorageResolution
        {
            DatabaseName = databaseName,
            Provider = bound,
            Probe = probe,
            IsFirstChoice = selection.Candidates.Count > 0
                && string.Equals(selection.Candidates[0], bound.Name, StringComparison.OrdinalIgnoreCase),
            WasDecidedByExistingData = true,
            BetterProviderAvailable = better,
            Attempts =
            [
                new BlazorSqliteStorageCandidateOutcome
                {
                    ProviderName = bound.Name,
                    Status = BlazorSqliteStorageCandidateStatus.Selected,
                    Probe = probe,
                    Explanation = $"Holds the existing database '{databaseName}'.",
                },
            ],
        };
    }

    private async ValueTask<BlazorSqliteStorageResolution> ResolveNewDatabaseAsync(
        string databaseName,
        BlazorSqliteStorageSelection selection,
        CancellationToken cancellationToken)
    {
        var attempts = new List<BlazorSqliteStorageCandidateOutcome>(selection.Candidates.Count);

        for (var index = 0; index < selection.Candidates.Count; index++)
        {
            var candidateName = selection.Candidates[index];

            if (!_providers.TryGetValue(candidateName, out var provider))
            {
                attempts.Add(new BlazorSqliteStorageCandidateOutcome
                {
                    ProviderName = candidateName,
                    Status = BlazorSqliteStorageCandidateStatus.NotRegistered,
                    Explanation = "No storage provider is registered under this name.",
                });
                continue;
            }

            var probe = await ProbeAsync(provider, cancellationToken).ConfigureAwait(false);

            if (!probe.IsAvailable)
            {
                attempts.Add(new BlazorSqliteStorageCandidateOutcome
                {
                    ProviderName = candidateName,
                    Status = BlazorSqliteStorageCandidateStatus.Unavailable,
                    Probe = probe,
                });
                continue;
            }

            // A non-persistent first choice is what the application asked for. A non-persistent
            // *fallback* is a silent downgrade to storage that empties on reload, so it needs consent.
            if (index > 0
                && !provider.Capabilities.IsPersistent
                && !selection.AllowNonPersistentFallback)
            {
                attempts.Add(new BlazorSqliteStorageCandidateOutcome
                {
                    ProviderName = candidateName,
                    Status = BlazorSqliteStorageCandidateStatus.RejectedAsNonPersistent,
                    Probe = probe,
                    Explanation =
                        $"'{candidateName}' does not persist across reloads, and falling back to "
                        + "non-persistent storage would lose data without warning. Call "
                        + "AllowNonPersistentFallback() to accept that.",
                });
                continue;
            }

            attempts.Add(new BlazorSqliteStorageCandidateOutcome
            {
                ProviderName = candidateName,
                Status = BlazorSqliteStorageCandidateStatus.Selected,
                Probe = probe,
            });

            for (var rest = index + 1; rest < selection.Candidates.Count; rest++)
            {
                attempts.Add(new BlazorSqliteStorageCandidateOutcome
                {
                    ProviderName = selection.Candidates[rest],
                    Status = BlazorSqliteStorageCandidateStatus.NotProbed,
                    Explanation = $"'{candidateName}' was selected first.",
                });
            }

            if (index > 0)
            {
                _logger.LogWarning(
                    "Storage provider {Provider} was selected for database {DatabaseName} after "
                    + "{RejectedCount} higher-ranked provider(s) were rejected: {Rejected}",
                    provider.Name,
                    databaseName,
                    index,
                    string.Join("; ", attempts.Take(index)));
            }

            return new BlazorSqliteStorageResolution
            {
                DatabaseName = databaseName,
                Provider = provider,
                Probe = probe,
                Attempts = attempts,
                IsFirstChoice = index == 0,
            };
        }

        throw new BlazorSqliteStorageUnavailableException(databaseName, attempts);
    }

    /// <summary>
    /// Finds an available backend ranked above <paramref name="currentProviderName"/>, which is what
    /// makes "a better option exists" reportable rather than something the user has to guess at.
    /// </summary>
    private async ValueTask<IBlazorSqliteStorageProvider?> FindBetterProviderAsync(
        string currentProviderName,
        BlazorSqliteStorageSelection selection,
        CancellationToken cancellationToken)
    {
        foreach (var candidateName in selection.Candidates)
        {
            if (string.Equals(candidateName, currentProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (_providers.TryGetValue(candidateName, out var provider)
                && provider.Capabilities.IsPersistent)
            {
                var probe = await ProbeAsync(provider, cancellationToken).ConfigureAwait(false);
                if (probe.IsAvailable)
                {
                    return provider;
                }
            }
        }

        return null;
    }

    private async ValueTask<BlazorSqliteProbeResult> ProbeAsync(
        IBlazorSqliteStorageProvider provider,
        CancellationToken cancellationToken)
    {
        if (_probeCache.TryGetValue(provider.Name, out var cached))
        {
            return cached;
        }

        BlazorSqliteProbeResult probe;
        try
        {
            probe = await provider.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider that throws instead of reporting unavailability must not take the whole
            // selection down with it; the next candidate still deserves a chance.
            probe = BlazorSqliteProbeResult.Unavailable(
                $"Probing '{provider.Name}' threw {ex.GetType().Name}: {ex.Message}");
        }

        _probeCache[provider.Name] = probe;
        return probe;
    }
}
