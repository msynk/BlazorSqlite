using BlazorSqlite.Storage;

namespace BlazorSqlite;

/// <summary>
/// The backend chosen for a database, together with enough context to explain the choice.
/// </summary>
public sealed record BlazorSqliteStorageResolution
{
    /// <summary>The database this decision applies to.</summary>
    public required string DatabaseName { get; init; }

    /// <summary>The chosen backend.</summary>
    public required IBlazorSqliteStorageProvider Provider { get; init; }

    /// <summary>The probe that cleared it.</summary>
    public required BlazorSqliteProbeResult Probe { get; init; }

    /// <summary>Every candidate's outcome, in preference order.</summary>
    public required IReadOnlyList<BlazorSqliteStorageCandidateOutcome> Attempts { get; init; }

    /// <summary>
    /// Whether the most preferred backend was chosen. False means a fallback was used, which is
    /// reported as a warning rather than passed over in silence.
    /// </summary>
    public bool IsFirstChoice { get; init; }

    /// <summary>
    /// Whether the choice was dictated by where the data already lives rather than by preference
    /// order.
    /// </summary>
    public bool WasDecidedByExistingData { get; init; }

    /// <summary>
    /// A backend ranked above <see cref="Provider"/> that is available but not holding the data.
    /// Non-null only for an existing database, and what
    /// <see cref="BlazorSqliteStorageMigrationMode"/> decides the response to.
    /// </summary>
    public IBlazorSqliteStorageProvider? BetterProviderAvailable { get; init; }
}
