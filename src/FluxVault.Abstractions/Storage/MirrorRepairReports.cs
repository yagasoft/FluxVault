namespace FluxVault.Abstractions.Storage;

public enum MirrorRepairArtefactKind
{
    Repository = 0,
    Manifest = 1,
    Chunk = 2,
    Metadata = 3
}

public enum MirrorRepairAction
{
    None = 0,
    RepairedPrimaryFromMirror = 1,
    RepairedMirrorFromPrimary = 2,
    Unresolved = 3
}

public sealed record MirrorRepairIssue(
    string? MirrorNodeId,
    string? MirrorNodeLabel,
    RepositoryScrubIssueSeverity Severity,
    MirrorRepairArtefactKind ArtefactKind,
    string Path,
    string? VersionId,
    string? ChunkDigest,
    string Message,
    MirrorRepairAction RepairAction = MirrorRepairAction.None);

public sealed record MirrorNodeRepairReport(
    string NodeId,
    string Label,
    string Path,
    bool IsEnabled,
    RepositoryHealthState HealthState,
    int IssueCount,
    int RepairedIssueCount,
    IReadOnlyList<MirrorRepairIssue> Issues);

public sealed record MirrorRepairReport(
    DateTimeOffset CompletedAtUtc,
    bool IsPreview,
    string? RequestedMirrorNodeId,
    RepositoryHealthState HealthState,
    int IssueCount,
    int RepairedIssueCount,
    IReadOnlyList<MirrorNodeRepairReport> Nodes,
    IReadOnlyList<MirrorRepairIssue> Issues);
