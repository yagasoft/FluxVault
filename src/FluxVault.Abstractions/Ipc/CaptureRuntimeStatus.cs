using FluxVault.Abstractions.Storage;

namespace FluxVault.Abstractions.Ipc;

public enum CaptureRuntimeState
{
    WaitingForQuietWindow = 0,
    ForcedHotFileSnapshot = 1,
    Capturing = 2,
    Captured = 3,
    Blocked = 4,
    Failed = 5
}

public sealed record CaptureRuntimeStatus(
    string SourcePath,
    string WatchedFolderId,
    CaptureRuntimeState State,
    DateTimeOffset? LastEventUtc,
    DateTimeOffset? NextForcedCaptureUtc,
    DateTimeOffset? LastCaptureAttemptUtc,
    string? DelayReason,
    string? BlockedReason,
    CaptureConsistency? Consistency,
    int AttemptCount);
