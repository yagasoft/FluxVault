namespace FluxVault.Abstractions.Storage;

public enum MirrorRebalanceActionKind
{
    CopyToMirror = 0,
    DeleteFromMirror = 1,
    Unresolved = 2
}

public enum MirrorRebalanceArtefactKind
{
    Chunk = 0,
    Metadata = 1
}

public sealed record MirrorRebalanceAction(
    MirrorRebalanceActionKind Action,
    MirrorRebalanceArtefactKind ArtefactKind,
    string MirrorNodeId,
    string MirrorNodeLabel,
    string Path,
    string ChunkDigest,
    long EstimatedBytes,
    string Message);

public sealed record MirrorNodeRebalancePreview(
    string NodeId,
    string Label,
    string Path,
    bool IsEnabled,
    RepositoryHealthState HealthState,
    int ActionCount,
    long EstimatedCopyBytes,
    long EstimatedDeleteBytes);

public sealed record MirrorRebalancePreviewReport(
    DateTimeOffset CompletedAtUtc,
    RepositoryHealthState HealthState,
    int CheckedChunkCount,
    int ActionCount,
    long EstimatedCopyBytes,
    long EstimatedDeleteBytes,
    IReadOnlyList<MirrorNodeRebalancePreview> Nodes,
    IReadOnlyList<MirrorRebalanceAction> Actions);
