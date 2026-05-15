using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Sync;

namespace FluxVault.Abstractions.Storage;

public sealed record FileCommitRequest(
    string WatchedFolderId,
    string SourcePath,
    DateTimeOffset CapturedAtUtc,
    CaptureConsistency Consistency,
    CompressionPreference Compression,
    int MinimumCompressionBytes,
    Stream Content,
    SyncOriginMetadata? SyncOrigin = null,
    string? WatchedFolderPath = null,
    DateTimeOffset? SourceLastWriteUtc = null);
