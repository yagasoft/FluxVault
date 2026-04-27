namespace FluxVault.Abstractions.Storage;

public sealed record RepositoryRetentionPreview(
    IReadOnlyList<RepositoryVersionRetentionDecision> Decisions,
    int KeptVersionCount,
    int PrunableVersionCount,
    long EstimatedReclaimableBytes,
    long RepositorySizeBytes);
