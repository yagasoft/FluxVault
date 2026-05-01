namespace FluxVault.Abstractions.Storage;

public enum RepositoryHealthState
{
    Healthy = 0,
    Warning = 1,
    Critical = 2
}

public enum RepositoryScrubIssueSeverity
{
    Warning = 0,
    Critical = 1
}

public enum RepositoryScrubIssueKind
{
    MissingChunk = 0,
    CorruptChunk = 1,
    DecodeFailure = 2,
    LengthMismatch = 3,
    DigestMismatch = 4,
    InvalidManifest = 5,
    MirrorDrift = 6
}

public enum RepositoryRepairAction
{
    None = 0,
    RepairedPrimaryFromMirror = 1,
    RepairedMirrorFromPrimary = 2,
    Unresolved = 3
}

public sealed record RepositoryScrubIssue(
    RepositoryScrubIssueSeverity Severity,
    RepositoryScrubIssueKind Kind,
    string Path,
    string? VersionId,
    string? ChunkDigest,
    string Message,
    RepositoryRepairAction RepairAction = RepositoryRepairAction.None);

public sealed record RepositoryScrubReport(
    DateTimeOffset CompletedAtUtc,
    RepositoryHealthState HealthState,
    int ManifestCount,
    int CheckedChunkCount,
    int IssueCount,
    int RepairedIssueCount,
    IReadOnlyList<RepositoryScrubIssue> Issues);

public sealed record RestoreRehearsalResult(
    string VersionId,
    string SourcePath,
    bool Success,
    long LogicalLength,
    string? Message);

public sealed record RestoreRehearsalReport(
    DateTimeOffset CompletedAtUtc,
    RepositoryHealthState HealthState,
    int RequestedVersionCount,
    int RehearsedVersionCount,
    int FailedVersionCount,
    IReadOnlyList<RestoreRehearsalResult> Results);

public sealed record RepositoryHealthSnapshot(
    DateTimeOffset CheckedAtUtc,
    RepositoryHealthState OverallState,
    string Summary,
    RepositoryScrubReport? LastScrub = null,
    RestoreRehearsalReport? LastRestoreRehearsal = null,
    MirrorRepairReport? LastMirrorRepair = null,
    MirrorRebalancePreviewReport? LastMirrorRebalance = null);
