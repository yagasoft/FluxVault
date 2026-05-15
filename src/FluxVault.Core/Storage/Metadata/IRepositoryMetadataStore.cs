using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Storage.Metadata;

public interface IRepositoryMetadataStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default);

    async Task RecordVersionsAsync(
        IReadOnlyCollection<FileVersionManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecordVersionAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
    }

    Task<FileVersionManifest> ReadManifestAsync(string versionId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This metadata store cannot read full manifests.");
    }

    Task<IReadOnlyList<FileVersionManifest>> ListManifestsAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This metadata store cannot list full manifests.");
    }

    Task<FileVersionManifest?> FindLatestManifestAsync(
        string sourcePath,
        RepositoryEntryKind entryKind,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This metadata store cannot perform targeted latest-manifest lookups.");
    }

    Task<FileVersionManifest?> FindLiveFileByContentSignatureAsync(
        string sourcePath,
        string contentSignature,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This metadata store cannot perform targeted content-signature lookups.");
    }

    Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default);

    Task DeleteVersionsAsync(IReadOnlyCollection<string> versionIds, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    Task<IReadOnlyDictionary<string, long>> CountChunkReferencesAsync(
        IReadOnlyCollection<string> digests,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
    }

    Task<MetadataStoreRuntimeStatus> GetRuntimeStatusAsync(
        TimeSpan exportLagWarningThreshold,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new MetadataStoreRuntimeStatus(
            Provider: FluxVault.Abstractions.Configuration.MetadataStoreProvider.PostgreSql,
            Endpoint: "metadata store",
            SchemaInitialized: true,
            LastError: null,
            PendingOutboxCount: 0,
            OldestUnexportedUtc: null,
            OldestUnexportedAge: null,
            IsExportLagExceeded: false));
    }

    Task<int> ExportOutboxAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }

    Task<int> ExportOutboxAsync(
        string repositoryPath,
        IReadOnlyCollection<string> versionIds,
        CancellationToken cancellationToken = default)
    {
        return ExportOutboxAsync(repositoryPath, cancellationToken);
    }
}
