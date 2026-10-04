using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Integrity;

namespace FluxVault.Core.Storage.Metadata;

public sealed class InMemoryRepositoryMetadataStore : IRepositoryMetadataStore
{
    private readonly object gate = new();
    private long historyGeneration;
    private readonly Dictionary<string, FileVersionManifest> manifests = new(StringComparer.OrdinalIgnoreCase);

    public Task<ChunkDescriptor?> FindChunkDescriptorAsync(string digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(ChunkDescriptorLookup.Resolve(manifests.Values.SelectMany(manifest => manifest.Chunks)
                .Where(chunk => string.Equals(chunk.Digest, digest, StringComparison.OrdinalIgnoreCase))));
    }

    public Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default) =>
        RecordVersionsAsync([manifest], cancellationToken);

    public Task RecordVersionsAsync(IReadOnlyCollection<FileVersionManifest> manifestsToRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestsToRecord);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var requestedDigests = manifestsToRecord.SelectMany(manifest => manifest.Chunks).Select(chunk => chunk.Digest)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in manifests.Values.Concat(manifestsToRecord).SelectMany(manifest => manifest.Chunks)
                .Where(chunk => requestedDigests.Contains(chunk.Digest)).GroupBy(chunk => chunk.Digest, StringComparer.OrdinalIgnoreCase))
                ChunkDescriptorLookup.Resolve(group);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var manifest in manifestsToRecord) manifests[manifest.VersionId] = manifest;
            if (manifestsToRecord.Count > 0) historyGeneration = checked(historyGeneration + 1);
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

    public Task<IReadOnlyList<RepositoryVersionSummary>> ListRecentVersionsAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(RepositoryMetadataStoreHelpers.ToVersionSummaries(manifests.Values
                .OrderByDescending(manifest => manifest.CapturedAtUtc)
                .ThenByDescending(manifest => manifest.VersionId, StringComparer.Ordinal)
                .Take(maximumCount)));
        }
    }

    public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(RepositoryMetadataStoreHelpers.ToLatestEntrySummaries(manifests.Values));
        }
    }

    public Task<RepositoryHistoryPage> ListHistoryPageAsync(RepositoryHistoryQuery query, CancellationToken cancellationToken = default)
    {
        query = RepositoryHistoryPaging.Validate(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (query.Cursor is { } cursor && cursor.Generation != historyGeneration) throw new RepositoryHistoryChangedException();
            var rows = manifests.Values.Where(m => m.VaultId == query.RepositoryId).Select(RepositoryHistoryPaging.Header)
                .Where(r => RepositoryHistoryPaging.Matches(r, query))
                .Where(r => query.Cursor is null || (query.Cursor.Direction == HistoryPageDirection.Older
                    ? RepositoryHistoryPaging.Compare(r, query.Cursor) < 0 : RepositoryHistoryPaging.Compare(r, query.Cursor) > 0));
            var newer = query.Cursor?.Direction == HistoryPageDirection.Newer;
            var selected = (newer ? rows.OrderBy(r => r.CapturedAtUtc.UtcTicks).ThenBy(r => r.VersionId, StringComparer.Ordinal) :
                rows.OrderByDescending(r => r.CapturedAtUtc.UtcTicks).ThenByDescending(r => r.VersionId, StringComparer.Ordinal)).Take(query.PageSize + 1).ToArray();
            var result = selected.Take(query.PageSize).ToArray();
            if (newer) Array.Reverse(result);
            return Task.FromResult(RepositoryHistoryPaging.Page(query, historyGeneration, result,
                newer ? query.Cursor is not null : selected.Length > query.PageSize,
                newer ? selected.Length > query.PageSize : query.Cursor is not null));
        }
    }
    public async Task<RepositorySnapshotPage> GetSnapshotPageAsync(RepositorySnapshotQuery query, CancellationToken cancellationToken = default)
    {
        RepositoryHistoryPaging.Validate(query); cancellationToken.ThrowIfCancellationRequested();
        return RepositoryHistoryPaging.Snapshot(query, await ReadManifestAsync(query.VersionId, cancellationToken));
    }

    public Task DeleteVersionsAsync(IReadOnlyCollection<string> versionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionIds);
        lock (gate)
        {
            var removed = false;
            foreach (var versionId in versionIds)
            {
                removed |= manifests.Remove(versionId);
            }
            if (removed) historyGeneration = checked(historyGeneration + 1);
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
