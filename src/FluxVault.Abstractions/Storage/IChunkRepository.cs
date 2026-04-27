namespace FluxVault.Abstractions.Storage;

public interface IChunkRepository
{
    Task<FileCommitResult> CommitAsync(FileCommitRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default);

    Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default);

    Task RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default);
}
