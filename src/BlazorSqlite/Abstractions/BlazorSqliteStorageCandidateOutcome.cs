using BlazorSqlite.Storage;

namespace BlazorSqlite;

/// <summary>
/// One candidate's fate, recorded for every candidate so that both diagnostics and failure messages
/// can explain the whole decision rather than only its outcome.
/// </summary>
public sealed record BlazorSqliteStorageCandidateOutcome
{
    /// <summary>The configured provider name.</summary>
    public required string ProviderName { get; init; }

    /// <summary>What happened to this candidate.</summary>
    public required BlazorSqliteStorageCandidateStatus Status { get; init; }

    /// <summary>The probe result, when the candidate got as far as being probed.</summary>
    public BlazorSqliteProbeResult? Probe { get; init; }

    /// <summary>A sentence explaining this outcome, suitable for showing to a developer.</summary>
    public string? Explanation { get; init; }

    /// <summary>Renders this outcome as one line of a failure report.</summary>
    public override string ToString()
    {
        var detail = Explanation ?? Probe?.UnavailableReason;
        return detail is null
            ? $"{ProviderName}: {Status}"
            : $"{ProviderName}: {Status} - {detail}";
    }
}
