namespace FluxVault.Abstractions.Storage;

public sealed record RepositoryInspection(
    FileVersionManifest Manifest,
    int ChunkCount,
    long LogicalLength,
    long StoredLength);
