using FluxVault.Abstractions.Policies;

namespace FluxVault.Abstractions.Storage;

public sealed record FileCommitRequest(
    string WatchedFolderId,
    string SourcePath,
    DateTimeOffset CapturedAtUtc,
    CaptureConsistency Consistency,
    CompressionPreference Compression,
    int MinimumCompressionBytes,
    Stream Content);
