namespace FluxVault.Abstractions.Storage;

public sealed record FileVersionManifest(
    string VersionId,
    string WatchedFolderId,
    string SourcePath,
    DateTimeOffset CapturedAtUtc,
    CaptureConsistency Consistency,
    long LogicalLength,
    IReadOnlyList<ManifestChunk> Chunks);

public sealed record ManifestChunk(
    string Digest,
    long Offset,
    int Length,
    int StoredLength,
    ChunkEncoding Encoding);
