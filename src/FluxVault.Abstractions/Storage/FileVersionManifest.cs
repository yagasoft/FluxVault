using FluxVault.Abstractions.Sync;

namespace FluxVault.Abstractions.Storage;

public sealed record FileVersionManifest(
    string VersionId,
    string WatchedFolderId,
    string SourcePath,
    DateTimeOffset CapturedAtUtc,
    CaptureConsistency Consistency,
    long LogicalLength,
    IReadOnlyList<ManifestChunk> Chunks,
    VersionOperationType OperationType = VersionOperationType.Capture,
    IReadOnlyList<string>? ParentVersionIds = null,
    string? RestoredFromVersionId = null,
    string? ForkOriginVersionId = null,
    string? InheritedFromVersionId = null,
    string? InheritedFromSourcePath = null,
    string? ContentSignature = null,
    SyncOriginMetadata? SyncOrigin = null,
    RepositoryEntryKind EntryKind = RepositoryEntryKind.File,
    bool IsDeleted = false,
    IReadOnlyList<FolderVersionEntry>? FolderEntries = null,
    string? DeletedFromVersionId = null);

public sealed record FolderVersionEntry(
    string Name,
    string SourcePath,
    RepositoryEntryKind EntryKind,
    string VersionId,
    bool IsDeleted,
    long LogicalLength,
    DateTimeOffset CapturedAtUtc);

public sealed record ManifestChunk(
    string Digest,
    long Offset,
    int Length,
    int StoredLength,
    ChunkEncoding Encoding);
