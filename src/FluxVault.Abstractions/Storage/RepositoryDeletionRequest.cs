namespace FluxVault.Abstractions.Storage;

public sealed record RepositoryDeletionRequest(
    string WatchedFolderId,
    string WatchedFolderPath,
    string SourcePath,
    bool IsDirectory,
    DateTimeOffset DeletedAtUtc,
    CaptureConsistency Consistency = CaptureConsistency.BestEffort);
