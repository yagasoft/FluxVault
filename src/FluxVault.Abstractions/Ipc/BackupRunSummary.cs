namespace FluxVault.Abstractions.Ipc;

public sealed record BackupRunSummary(
    bool Success,
    string Message,
    int CapturedFileCount,
    int FailedFileCount,
    DateTimeOffset CompletedAtUtc);
