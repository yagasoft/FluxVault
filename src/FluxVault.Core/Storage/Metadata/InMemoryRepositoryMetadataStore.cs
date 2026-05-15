using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Storage.Metadata;

public sealed class InMemoryRepositoryMetadataStore : IRepositoryMetadataStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, FileVersionManifest> manifests = new(StringComparer.OrdinalIgnoreCase);

    public Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (gate)
        {
            manifests[manifest.VersionId] = manifest;
        }

        return Task.CompletedTask;
    }

    public Task RecordVersionsAsync(IReadOnlyCollection<FileVersionManifest> manifestsToRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestsToRecord);
        lock (gate)
        {
            foreach (var manifest in manifestsToRecord)
            {
                cancellationToken.ThrowIfCancellationRequested();
                manifests[manifest.VersionId] = manifest;
            }
        }

        return Task.CompletedTask;
    }

    public Task<FileVersionManifest> ReadManifestAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        lock (gate)
        {
            if (manifests.TryGetValue(versionId, out var manifest))
            {
                return Task.FromResult(manifest);
            }
        }

        throw new FileNotFoundException($"Manifest {versionId} was not found.", versionId);
    }

    public Task<IReadOnlyList<FileVersionManifest>> ListManifestsAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<FileVersionManifest>>(
                manifests.Values
                    .OrderByDescending(manifest => manifest.CapturedAtUtc)
                    .ThenByDescending(manifest => manifest.VersionId, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public Task<FileVersionManifest?> FindLatestManifestAsync(
        string sourcePath,
        RepositoryEntryKind entryKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        lock (gate)
        {
            return Task.FromResult(manifests.Values
                .Where(manifest => PathEquals(manifest.SourcePath, fullPath))
                .Where(manifest => manifest.EntryKind == entryKind)
                .OrderByDescending(manifest => manifest.CapturedAtUtc)
                .ThenByDescending(manifest => manifest.VersionId, StringComparer.Ordinal)
                .FirstOrDefault());
        }
    }

    public Task<FileVersionManifest?> FindLiveFileByContentSignatureAsync(
        string sourcePath,
        string contentSignature,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSignature);
        var fullPath = Path.GetFullPath(sourcePath);
        lock (gate)
        {
            return Task.FromResult(manifests.Values
                .Where(manifest => !PathEquals(manifest.SourcePath, fullPath))
                .Where(manifest => manifest.EntryKind == RepositoryEntryKind.File && !manifest.IsDeleted)
                .Where(manifest => string.Equals(
                    RepositoryMetadataStoreHelpers.GetContentSignature(manifest),
                    contentSignature,
                    StringComparison.Ordinal))
                .OrderBy(manifest => manifest.CapturedAtUtc)
                .ThenBy(manifest => manifest.VersionId, StringComparer.Ordinal)
                .FirstOrDefault());
        }
    }

    public Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(RepositoryMetadataStoreHelpers.ToVersionSummaries(manifests.Values));
        }
    }

    public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(RepositoryMetadataStoreHelpers.ToLatestEntrySummaries(manifests.Values));
        }
    }

    public Task DeleteVersionsAsync(IReadOnlyCollection<string> versionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionIds);
        lock (gate)
        {
            foreach (var versionId in versionIds)
            {
                manifests.Remove(versionId);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, long>> CountChunkReferencesAsync(
        IReadOnlyCollection<string> digests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digests);
        var requested = digests.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = requested.ToDictionary(digest => digest, _ => 0L, StringComparer.OrdinalIgnoreCase);

        lock (gate)
        {
            foreach (var chunk in manifests.Values.SelectMany(manifest => manifest.Chunks))
            {
                if (requested.Contains(chunk.Digest))
                {
                    counts[chunk.Digest] = counts.GetValueOrDefault(chunk.Digest) + 1;
                }
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, long>>(counts);
    }

    public Task<MetadataStoreRuntimeStatus> GetRuntimeStatusAsync(
        TimeSpan exportLagWarningThreshold,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new MetadataStoreRuntimeStatus(
            Provider: MetadataStoreProvider.PostgreSql,
            Endpoint: "in-memory metadata store",
            SchemaInitialized: true,
            LastError: null,
            PendingOutboxCount: 0,
            OldestUnexportedUtc: null,
            OldestUnexportedAge: null,
            IsExportLagExceeded: false));
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
