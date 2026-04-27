namespace FluxVault.Abstractions.ChangeTracking;

public interface IUsnChangeJournalReader
{
    Task<UsnChangeJournalReadResult> ReadChangesAsync(
        IReadOnlyList<UsnWatchedFolderScope> watchedFolders,
        IReadOnlyList<UsnJournalCheckpoint> checkpoints,
        CancellationToken cancellationToken = default);
}

public sealed record UsnWatchedFolderScope(
    string WatchedFolderId,
    string Path,
    bool Recursive);

public sealed record UsnJournalCheckpoint(
    string WatchedFolderId,
    string VolumeRoot,
    ulong JournalId,
    long NextUsn,
    uint ReasonMask,
    DateTimeOffset CheckedAtUtc);

public sealed record UsnChangedFile(
    string WatchedFolderId,
    string Path,
    uint Reason,
    bool IsDirectory);

public sealed record DurableChangeRuntimeStatus(
    DateTimeOffset? LastUsnCatchUpUtc,
    string Status,
    string? FallbackReason,
    IReadOnlyList<UsnJournalCheckpoint> Checkpoints)
{
    public IReadOnlyList<DurableChangeDetail> Details { get; init; } = [];
}

public sealed record DurableChangeDetail(
    string? WatchedFolderId,
    string? Path,
    string? VolumeRoot,
    string Operation,
    string Reason,
    int? Win32ErrorCode);

public sealed record UsnChangeJournalReadResult(
    bool IsAvailable,
    bool RequiresFullScan,
    string Status,
    string? FallbackReason,
    IReadOnlyList<UsnChangedFile> ChangedFiles,
    IReadOnlyList<UsnJournalCheckpoint> Checkpoints)
{
    public IReadOnlyList<DurableChangeDetail> Details { get; init; } = [];

    public static UsnChangeJournalReadResult Active(
        string status,
        IReadOnlyList<UsnChangedFile> changedFiles,
        IReadOnlyList<UsnJournalCheckpoint> checkpoints,
        IReadOnlyList<DurableChangeDetail>? details = null)
    {
        return new UsnChangeJournalReadResult(true, false, status, null, changedFiles, checkpoints)
        {
            Details = details ?? []
        };
    }

    public static UsnChangeJournalReadResult FullScanRequired(
        string fallbackReason,
        IReadOnlyList<UsnJournalCheckpoint> checkpoints,
        IReadOnlyList<DurableChangeDetail>? details = null)
    {
        return new UsnChangeJournalReadResult(true, true, "USN journal reset.", fallbackReason, [], checkpoints)
        {
            Details = details ?? []
        };
    }

    public static UsnChangeJournalReadResult Unavailable(
        string fallbackReason,
        IReadOnlyList<DurableChangeDetail>? details = null)
    {
        return new UsnChangeJournalReadResult(false, true, "USN unavailable.", fallbackReason, [], [])
        {
            Details = details ?? []
        };
    }
}
