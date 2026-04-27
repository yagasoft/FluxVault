namespace FluxVault.Abstractions.Storage;

using FluxVault.Abstractions.Policies;

public interface IChunkRepository
{
    Task<FileCommitResult> CommitAsync(FileCommitRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default);

    Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default);

    Task RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default);

    Task<RepositoryRetentionPreview> PreviewRetentionAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<RepositoryRetentionResult> ApplyRetentionAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}
