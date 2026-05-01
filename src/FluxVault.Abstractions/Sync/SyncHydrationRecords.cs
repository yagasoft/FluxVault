namespace FluxVault.Abstractions.Sync;

public enum SyncHydrationState
{
    Applied = 0,
    Blocked = 1,
    Conflict = 2
}

public enum SyncConflictStatus
{
    Open = 0,
    Resolved = 1
}

public enum SyncConflictAction
{
    KeepLocal = 0,
    KeepRemote = 1,
    RestoreRemoteAsCopy = 2,
    MarkResolved = 3
}

public sealed record SyncHydrationRecord(
    string HydrationId,
    string SourceDeviceId,
    string SourceOperationId,
    string SourceVersionId,
    string LocalPath,
    SyncHydrationState State,
    string Message,
    DateTimeOffset CompletedAtUtc,
    string? ConflictId = null);

public sealed record SyncConflictRecord(
    string ConflictId,
    string LocalPath,
    string SourceDeviceId,
    string SourceVersionId,
    string SourceOperationId,
    DateTimeOffset DetectedAtUtc,
    SyncConflictStatus Status,
    IReadOnlyList<SyncConflictAction> AvailableActions,
    SyncConflictAction? ResolutionAction = null,
    DateTimeOffset? ResolvedAtUtc = null);
