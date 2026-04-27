namespace FluxVault.Abstractions.Storage;

public sealed record RepositoryRetentionResult(
    IReadOnlyList<RepositoryVersionRetentionDecision> Decisions,
    int KeptVersionCount,
    int PrunedVersionCount,
    int DeletedChunkCount,
    long ReclaimedBytes,
    long RepositorySizeBytes,
    IReadOnlyList<string> MirrorWarnings);
