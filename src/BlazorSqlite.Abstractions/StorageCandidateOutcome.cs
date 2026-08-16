using BlazorSqlite.Storage;

namespace BlazorSqlite;

/// <summary>What happened to one candidate during storage selection.</summary>
public enum StorageCandidateStatus
{
    /// <summary>Not examined, because an earlier candidate had already won.</summary>
    NotProbed,

    /// <summary>Named in the configuration, but no provider with that name is registered.</summary>
    NotRegistered,

    /// <summary>Probed and reported unusable in this browser.</summary>
    Unavailable,

    /// <summary>
    /// Usable, but rejected because it does not survive a reload and
    /// <see cref="BlazorSqliteStorageSelection.AllowNonPersistentFallback"/> was not set.
    /// </summary>
    RejectedAsNonPersistent,

    /// <summary>Rejected because the database already exists on a different backend.</summary>
    RejectedByExistingBinding,

    /// <summary>Chosen.</summary>
    Selected,
}

/// <summary>
/// One candidate's fate, recorded for every candidate so that both diagnostics and failure messages
/// can explain the whole decision rather than only its outcome.
/// </summary>
public sealed record StorageCandidateOutcome
{
    /// <summary>The configured provider name.</summary>
    public required string ProviderName { get; init; }

    /// <summary>What happened to this candidate.</summary>
    public required StorageCandidateStatus Status { get; init; }

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
