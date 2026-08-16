using BlazorSqlite.Storage;

namespace BlazorSqlite.Storage.Tests;

/// <summary>
/// A backend that can be told to break any one rule the conformance kit checks, so that the kit can be
/// shown to catch each violation rather than merely to pass on a correct provider.
/// </summary>
internal sealed class LyingStorageProvider : IBlazorSqliteStorageProvider
{
    private readonly LyingStorageAdmin _admin = new();

    public string Name { get; init; } = "lying";

    public bool IsPersistent { get; init; } = true;

    public bool SupportsRelaxedDurability { get; init; }

    public BlazorSqliteEngineBuild RequiredBuild { get; init; } = BlazorSqliteEngineBuild.Synchronous;

    public BlazorSqliteExecutionContexts SupportedContexts { get; init; }
        = BlazorSqliteExecutionContexts.DedicatedWorker;

    /// <summary>Hands out the stored array itself, so callers can scribble on live storage.</summary>
    public bool ExportsLiveArray
    {
        get => _admin.ExportsLiveArray;
        init => _admin.ExportsLiveArray = value;
    }

    /// <summary>Writes a smaller image over a larger one, leaving the old tail behind.</summary>
    public bool OverwritesInPlace
    {
        get => _admin.OverwritesInPlace;
        init => _admin.OverwritesInPlace = value;
    }

    /// <summary>Accepts blank and null database names instead of rejecting them.</summary>
    public bool SkipsNameValidation
    {
        get => _admin.SkipsNameValidation;
        init => _admin.SkipsNameValidation = value;
    }

    public BlazorSqliteStorageCapabilities Capabilities => new()
    {
        RequiredBuild = RequiredBuild,
        IsPersistent = IsPersistent,
        SupportsRelaxedDurability = SupportsRelaxedDurability,
        SupportedContexts = SupportedContexts,
    };

    public BlazorSqliteJsModule? VfsModule => null;

    public IBlazorSqliteStorageAdmin Admin => _admin;

    public ValueTask<BlazorSqliteProbeResult> ProbeAsync(
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(BlazorSqliteProbeResult.Available());

    private sealed class LyingStorageAdmin : IBlazorSqliteStorageAdmin
    {
        private readonly Dictionary<string, byte[]> _databases = new(StringComparer.Ordinal);

        internal bool ExportsLiveArray { get; set; }

        internal bool OverwritesInPlace { get; set; }

        internal bool SkipsNameValidation { get; set; }

        public ValueTask<bool> ExistsAsync(
            string databaseName,
            CancellationToken cancellationToken = default)
        {
            Validate(databaseName);
            return ValueTask.FromResult(_databases.ContainsKey(databaseName ?? string.Empty));
        }

        public ValueTask<IReadOnlyList<string>> ListAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>([.. _databases.Keys]);

        public ValueTask DeleteAsync(
            string databaseName,
            CancellationToken cancellationToken = default)
        {
            Validate(databaseName);
            _databases.Remove(databaseName ?? string.Empty);
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ExportAsync(
            string databaseName,
            CancellationToken cancellationToken = default)
        {
            Validate(databaseName);

            if (!_databases.TryGetValue(databaseName ?? string.Empty, out var image))
            {
                throw new FileNotFoundException("No such database.", databaseName);
            }

            return ValueTask.FromResult(ExportsLiveArray ? image : image.ToArray());
        }

        public ValueTask ImportAsync(
            string databaseName,
            ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken = default)
        {
            Validate(databaseName);
            var key = databaseName ?? string.Empty;

            if (OverwritesInPlace
                && _databases.TryGetValue(key, out var existing)
                && existing.Length > contents.Length)
            {
                contents.Span.CopyTo(existing);
                return ValueTask.CompletedTask;
            }

            _databases[key] = contents.ToArray();
            return ValueTask.CompletedTask;
        }

        private void Validate(string? databaseName)
        {
            if (!SkipsNameValidation)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
            }
        }
    }
}
