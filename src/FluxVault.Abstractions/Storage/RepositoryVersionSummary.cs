namespace FluxVault.Abstractions.Storage;

public sealed record RepositoryVersionSummary(
    string VersionId,
    string SourcePath,
    DateTimeOffset CapturedAtUtc,
    CaptureConsistency Consistency,
    long LogicalLength,
    int ChunkCount);
