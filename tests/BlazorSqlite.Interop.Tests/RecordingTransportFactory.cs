using BlazorSqlite.Data;
using BlazorSqlite.Storage;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// Hands out scripted transports and remembers which provider each one was created for, so a test
/// can see that the factory asked for the backend selection actually chose.
/// </summary>
internal sealed class RecordingTransportFactory : IBlazorSqliteTransportFactory
{
    private readonly Func<ScriptedTransport> _create;

    public RecordingTransportFactory(Func<ScriptedTransport>? create = null)
        => _create = create ?? (() => new ScriptedTransport());

    public List<(IBlazorSqliteStorageProvider Provider, ScriptedTransport Transport)> Created { get; } = [];

    public ScriptedTransport Last
        => Created.Count > 0
            ? Created[^1].Transport
            : throw new InvalidOperationException("No transport has been created.");

    public IBlazorSqliteTransport Create(IBlazorSqliteStorageProvider provider)
    {
        var transport = _create();
        Created.Add((provider, transport));
        return transport;
    }
}

/// <summary>An <see cref="IBlazorSqliteTransport"/> whose open can be told to fail.</summary>
internal sealed class ScriptedTransport : IBlazorSqliteTransport
{
    public string? OpenedAs { get; private set; }

    public int OpenCount { get; private set; }

    public bool Disposed { get; private set; }

    public Exception? OpenThrows { get; init; }

    public Task OpenAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        OpenCount++;
        OpenedAs = databaseName;

        if (OpenThrows is not null)
        {
            throw OpenThrows;
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<BlazorSqliteCommandResult>> ExecuteAsync(
        IReadOnlyList<BlazorSqliteCommandRequest> batch,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BlazorSqliteCommandResult>>([]);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ConfigurableProvider : IBlazorSqliteStorageProvider
{
    public required string Name { get; init; }

    public required BlazorSqliteEngineBuild RequiredBuild { get; init; }

    public bool IsPersistent { get; init; } = true;

    public bool IsAvailable { get; init; } = true;

    public BlazorSqliteJsModule? Vfs { get; init; }

    public BlazorSqliteStorageCapabilities Capabilities => new()
    {
        RequiredBuild = RequiredBuild,
        IsPersistent = IsPersistent,
    };

    public BlazorSqliteJsModule? VfsModule => Vfs;

    public IBlazorSqliteStorageAdmin Admin => throw new NotSupportedException();

    public ValueTask<BlazorSqliteProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(IsAvailable
            ? BlazorSqliteProbeResult.Available()
            : BlazorSqliteProbeResult.Unavailable($"'{Name}' is switched off."));
}
