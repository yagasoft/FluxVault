namespace FluxVault.Abstractions.Ipc;

public sealed record BackupRunSummary(
    bool Success,
    string Message,
    int CapturedFileCount,
    int FailedFileCount,
    DateTimeOffset CompletedAtUtc,
    int EnumeratedFileCount = 0,
    int SkippedUnchangedFileCount = 0,
    int RecordedDeletionCount = 0,
    TimeSpan? Elapsed = null);
