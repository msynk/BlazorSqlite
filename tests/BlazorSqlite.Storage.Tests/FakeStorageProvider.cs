using BlazorSqlite.Storage;

namespace BlazorSqlite.Storage.Tests;

/// <summary>
/// A provider whose availability, persistence, and probe behaviour are dictated by the test, so that
/// selection can be driven through browser conditions that are awkward to reproduce for real.
/// </summary>
internal sealed class FakeStorageProvider(
    string name,
    bool isAvailable = true,
    bool isPersistent = true,
    Exception? probeThrows = null) : IBlazorSqliteStorageProvider
{
    public string Name { get; } = name;

    /// <summary>How many times the provider was probed, used to assert the resolver caches.</summary>
    public int ProbeCount { get; private set; }

    public BlazorSqliteStorageCapabilities Capabilities { get; } = new()
    {
        RequiredBuild = BlazorSqliteEngineBuild.AsyncCapable,
        IsPersistent = isPersistent,
    };

    public BlazorSqliteJsModule? VfsModule => null;

    public IBlazorSqliteStorageAdmin Admin => throw new NotSupportedException(
        "Selection must not touch a provider's admin surface.");

    public ValueTask<BlazorSqliteProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        ProbeCount++;

        if (probeThrows is not null)
        {
            throw probeThrows;
        }

        return ValueTask.FromResult(isAvailable
            ? BlazorSqliteProbeResult.Available(quotaBytes: 1024 * 1024)
            : BlazorSqliteProbeResult.Unavailable($"'{Name}' is switched off for this test."));
    }
}
