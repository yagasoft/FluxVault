namespace FluxVault.Abstractions.Storage;

using FluxVault.Abstractions.Policies;

public interface IChunkRepository
{
    Task<FileCommitResult> CommitAsync(FileCommitRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<RepositoryVersionSummary>> ListRecentVersionsAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        cancellationToken.ThrowIfCancellationRequested();
        return (await ListVersionsAsync(cancellationToken).ConfigureAwait(false)).Take(maximumCount).ToArray();
    }

    Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default);

    Task<RepositoryCurrentEntriesPage> ListCurrentEntriesPageAsync(RepositoryCurrentEntriesQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository cannot page current entries.");

    Task<RepositoryHistoryPage> ListHistoryPageAsync(RepositoryHistoryQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository cannot page history.");

    Task<RepositorySnapshotPage> GetSnapshotPageAsync(RepositorySnapshotQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository cannot page snapshot contents.");

    Task<RepositoryDeletionResult?> RecordDeletionAsync(RepositoryDeletionRequest request, CancellationToken cancellationToken = default);

    Task<RepositoryPurgeResult> PurgeAsync(RepositoryPurgeRequest request, CancellationToken cancellationToken = default);

    Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default);

    Task<RepositoryRestoreResult> RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default);

    Task<RepositoryRestoreResult> RestoreAsync(string versionId, IRepositoryRestoreTarget target, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support an owned recovery target.");

    Task<RepositoryRestoreResult> RestoreFilesAsync(IReadOnlyList<RepositoryRestoreFileSelection> files, IRepositoryRestoreTarget target, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support an owned file selection recovery target.");

    Task RestorePreviewAsync(string versionId, string outputPath, CancellationToken cancellationToken = default);

    Task<RepositoryScrubReport> ScrubAsync(
        bool autoRepairFromMirror,
        CancellationToken cancellationToken = default);

    Task<MirrorRepairReport> PreviewMirrorRepairAsync(
        string? mirrorNodeId = null,
        CancellationToken cancellationToken = default);

    Task<MirrorRepairReport> RunMirrorRepairAsync(
        string? mirrorNodeId = null,
        CancellationToken cancellationToken = default);

    Task<MirrorRebalancePreviewReport> PreviewMirrorRebalanceAsync(
        CancellationToken cancellationToken = default);

    Task<MirrorRebalancePreviewReport> RunMirrorRebalanceAsync(
        CancellationToken cancellationToken = default);

    Task<MirrorRebalancePreviewReport> PreviewMirrorDrainAsync(
        string mirrorNodeId,
        CancellationToken cancellationToken = default);

    Task<MirrorRebalancePreviewReport> RunMirrorDrainAsync(
        string mirrorNodeId,
        CancellationToken cancellationToken = default);

    Task<RestoreRehearsalReport> RunRestoreRehearsalAsync(
        string tempRoot,
        int maxVersions,
        CancellationToken cancellationToken = default);

    Task<RepositoryRetentionPreview> PreviewRetentionAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<RepositoryRetentionResult> ApplyRetentionAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}
