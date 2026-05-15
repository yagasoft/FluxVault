using FluxVault.Abstractions.Sync;

namespace FluxVault.Abstractions.Storage;

public sealed record RepositoryVersionSummary(
    string VersionId,
    string SourcePath,
    DateTimeOffset CapturedAtUtc,
    CaptureConsistency Consistency,
    long LogicalLength,
    int ChunkCount,
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
