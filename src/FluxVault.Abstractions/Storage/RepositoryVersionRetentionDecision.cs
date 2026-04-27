namespace FluxVault.Abstractions.Storage;

public sealed record RepositoryVersionRetentionDecision(
    string VersionId,
    string SourcePath,
    DateTimeOffset CapturedAtUtc,
    bool Keep,
    string Reason,
    long LogicalLength,
    int ChunkCount);
