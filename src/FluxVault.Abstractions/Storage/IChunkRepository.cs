namespace FluxVault.Abstractions.Storage;

using FluxVault.Abstractions.Policies;

public interface IChunkRepository
{
    Task<FileCommitResult> CommitAsync(FileCommitRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default);

    Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default);

    Task RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default);

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
