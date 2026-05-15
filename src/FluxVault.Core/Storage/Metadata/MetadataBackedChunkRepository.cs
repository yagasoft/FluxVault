using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Storage.Metadata;

public sealed class MetadataBackedChunkRepository(
    IChunkRepository innerRepository,
    IRepositoryMetadataStore metadataStore) : IChunkRepository
{
    public async Task<FileCommitResult> CommitAsync(
        FileCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await innerRepository.CommitAsync(request, cancellationToken).ConfigureAwait(false);
        await metadataStore.RecordVersionAsync(result.Manifest, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        return metadataStore.ListVersionsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
    {
        return metadataStore.ListLatestEntriesAsync(cancellationToken);
    }

    public async Task<RepositoryDeletionResult?> RecordDeletionAsync(
        RepositoryDeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await innerRepository.RecordDeletionAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            await metadataStore.RecordVersionAsync(result.Manifest, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default)
    {
        return innerRepository.InspectAsync(versionId, cancellationToken);
    }

    public Task RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
    {
        return innerRepository.RestoreAsync(versionId, outputPath, cancellationToken);
    }

    public Task RestorePreviewAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
    {
        return innerRepository.RestorePreviewAsync(versionId, outputPath, cancellationToken);
    }

    public Task<RepositoryScrubReport> ScrubAsync(bool autoRepairFromMirror, CancellationToken cancellationToken = default)
    {
        return innerRepository.ScrubAsync(autoRepairFromMirror, cancellationToken);
    }

    public Task<MirrorRepairReport> PreviewMirrorRepairAsync(
        string? mirrorNodeId = null,
        CancellationToken cancellationToken = default)
    {
        return innerRepository.PreviewMirrorRepairAsync(mirrorNodeId, cancellationToken);
    }

    public Task<MirrorRepairReport> RunMirrorRepairAsync(
        string? mirrorNodeId = null,
        CancellationToken cancellationToken = default)
    {
        return innerRepository.RunMirrorRepairAsync(mirrorNodeId, cancellationToken);
    }

    public Task<MirrorRebalancePreviewReport> PreviewMirrorRebalanceAsync(CancellationToken cancellationToken = default)
    {
        return innerRepository.PreviewMirrorRebalanceAsync(cancellationToken);
    }

    public Task<MirrorRebalancePreviewReport> RunMirrorRebalanceAsync(CancellationToken cancellationToken = default)
    {
        return innerRepository.RunMirrorRebalanceAsync(cancellationToken);
    }

    public Task<MirrorRebalancePreviewReport> PreviewMirrorDrainAsync(
        string mirrorNodeId,
        CancellationToken cancellationToken = default)
    {
        return innerRepository.PreviewMirrorDrainAsync(mirrorNodeId, cancellationToken);
    }

    public Task<MirrorRebalancePreviewReport> RunMirrorDrainAsync(
        string mirrorNodeId,
        CancellationToken cancellationToken = default)
    {
        return innerRepository.RunMirrorDrainAsync(mirrorNodeId, cancellationToken);
    }

    public Task<RestoreRehearsalReport> RunRestoreRehearsalAsync(
        string tempRoot,
        int maxVersions,
        CancellationToken cancellationToken = default)
    {
        return innerRepository.RunRestoreRehearsalAsync(tempRoot, maxVersions, cancellationToken);
    }

    public Task<RepositoryRetentionPreview> PreviewRetentionAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        return innerRepository.PreviewRetentionAsync(policy, nowUtc, cancellationToken);
    }

    public Task<RepositoryRetentionResult> ApplyRetentionAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        return innerRepository.ApplyRetentionAsync(policy, nowUtc, cancellationToken);
    }
}
