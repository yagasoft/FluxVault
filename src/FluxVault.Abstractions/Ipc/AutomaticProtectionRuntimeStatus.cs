using FluxVault.Abstractions.Configuration;

namespace FluxVault.Abstractions.Ipc;

public enum AutomaticProtectionState
{
    Disabled, Paused, WaitingForChanges, Pending, Capturing, Blocked, Failed, OutcomeUncertain, Reconciling
}

public sealed record AutomaticProtectionRuntimeStatus(
    AutomaticProtectionPolicy Policy,
    AutomaticProtectionState State,
    string Reason,
    bool RequiresCreatorApproval = false,
    int PendingCount = 0,
    int UncertainCount = 0,
    string? LastSuccessfulVersionId = null,
    DateTimeOffset? LastSuccessfulUtc = null,
    DateTimeOffset? LastObservationUtc = null,
    int? CreatorSessionId = null,
    string? DiscoveryMode = null);
