using System.Text;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class MetadataPrimaryRepositoryTests
{
    [Fact]
    public async Task Commit_records_versions_in_metadata_store_without_manifest_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var metadataStore = new InMemoryRepositoryMetadataStore();
        var repository = CreateRepository(workspace.RepositoryPath, metadataStore);
        var sourcePath = Path.GetFullPath(@"D:\Work\Docs\Reports\brief.txt");

        var result = await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("metadata primary"),
            sourcePath,
            watchedFolderPath: @"D:\Work"));

        var manifestFiles = Directory.Exists(Path.Combine(workspace.RepositoryPath, "manifests"))
            ? Directory.GetFiles(Path.Combine(workspace.RepositoryPath, "manifests"), "*.json")
            : [];
        var versions = await metadataStore.ListVersionsAsync();
        var inspection = await repository.InspectAsync(result.Manifest.VersionId);

        Assert.Empty(manifestFiles);
        Assert.Contains(versions, version => version.VersionId == result.Manifest.VersionId);
        Assert.Contains(versions, version => version.EntryKind == RepositoryEntryKind.Folder);
        Assert.Equal(result.Manifest.VersionId, inspection.Manifest.VersionId);
    }

    [Fact]
    public async Task Metadata_backed_commit_batches_file_and_folder_manifests_without_full_manifest_listing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var metadataStore = new CountingMetadataStore();
        var repository = CreateRepository(workspace.RepositoryPath, metadataStore);

        var result = await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("metadata primary"),
            @"D:\Work\Docs\brief.txt",
            watchedFolderPath: @"D:\Work"));

        var versions = await metadataStore.ListVersionsAsync();

        Assert.Equal(0, metadataStore.ListManifestsCallCount);
        Assert.Equal(0, metadataStore.RecordVersionCallCount);
        Assert.Equal([3], metadataStore.RecordVersionsBatchSizes);
        Assert.Equal([result.Manifest.VersionId], versions.Where(version => version.EntryKind == RepositoryEntryKind.File).Select(version => version.VersionId).ToArray());
        Assert.Contains(versions, version => version.EntryKind == RepositoryEntryKind.Folder && version.SourcePath == Path.GetFullPath(@"D:\Work\Docs"));
        Assert.Contains(versions, version => version.EntryKind == RepositoryEntryKind.Folder && version.SourcePath == Path.GetFullPath(@"D:\Work"));
        var scopedExport = Assert.Single(metadataStore.ScopedOutboxExports);
        Assert.Equal(3, scopedExport.Count);
        Assert.Contains(result.Manifest.VersionId, scopedExport);
    }

    [Fact]
    public async Task Metadata_backed_delete_batches_tombstone_and_folder_cascade_without_full_manifest_listing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var metadataStore = new CountingMetadataStore();
        var repository = CreateRepository(workspace.RepositoryPath, metadataStore);
        await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("metadata primary"),
            @"D:\Work\Docs\brief.txt",
            watchedFolderPath: @"D:\Work"));

        metadataStore.ResetCounters();
        var deletion = await repository.RecordDeletionAsync(new RepositoryDeletionRequest(
            "docs",
            @"D:\Work",
            @"D:\Work\Docs\brief.txt",
            IsDirectory: false,
            DateTimeOffset.UtcNow,
            CaptureConsistency.CrashConsistent));

        Assert.NotNull(deletion);
        Assert.Equal(0, metadataStore.ListManifestsCallCount);
        Assert.Equal([3], metadataStore.RecordVersionsBatchSizes);
        var scopedExport = Assert.Single(metadataStore.ScopedOutboxExports);
        Assert.Contains(deletion.Manifest.VersionId, scopedExport);
    }

    [Fact]
    public async Task Metadata_backed_retention_prunes_db_versions_and_unreferenced_chunks()
    {
        using var workspace = TemporaryWorkspace.Create();
        var metadataStore = new InMemoryRepositoryMetadataStore();
        var repository = CreateRepository(workspace.RepositoryPath, metadataStore);
        var now = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < 5; index++)
        {
            await repository.CommitAsync(NewRequest(
                Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat($"payload {index} ", 64))),
                @"D:\Work\Docs\brief.txt",
                now.AddDays(-120).AddMinutes(index)));
        }

        var result = await repository.ApplyRetentionAsync(new RetentionPolicy(
            IsEnabled: true,
            KeepAllFor: TimeSpan.Zero,
            KeepHourlyFor: TimeSpan.Zero,
            KeepDailyFor: TimeSpan.Zero,
            MinimumVersionsPerFile: 2), now);

        var versions = await metadataStore.ListVersionsAsync();

        Assert.Equal(3, result.PrunedVersionCount);
        Assert.Equal(2, versions.Count);
        Assert.True(result.DeletedChunkCount > 0);
    }

    private static FileSystemChunkRepository CreateRepository(
        string path,
        IRepositoryMetadataStore metadataStore)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            mirrorSet: null,
            metadataStore);
    }

    private static FileCommitRequest NewRequest(
        byte[] payload,
        string sourcePath,
        DateTimeOffset? capturedAtUtc = null,
        string? watchedFolderPath = null)
    {
        return new FileCommitRequest(
            WatchedFolderId: "docs",
            SourcePath: sourcePath,
            CapturedAtUtc: capturedAtUtc ?? DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.CrashConsistent,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 128,
            Content: new MemoryStream(payload),
            WatchedFolderPath: watchedFolderPath);
    }

    private sealed class CountingMetadataStore : IRepositoryMetadataStore
    {
        private readonly InMemoryRepositoryMetadataStore inner = new();

        public int ListManifestsCallCount { get; private set; }

        public int RecordVersionCallCount { get; private set; }

        public List<int> RecordVersionsBatchSizes { get; } = [];

        public List<IReadOnlyList<string>> ScopedOutboxExports { get; } = [];

        public void ResetCounters()
        {
            ListManifestsCallCount = 0;
            RecordVersionCallCount = 0;
            RecordVersionsBatchSizes.Clear();
            ScopedOutboxExports.Clear();
        }

        public Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default)
        {
            RecordVersionCallCount++;
            return inner.RecordVersionAsync(manifest, cancellationToken);
        }

        public Task RecordVersionsAsync(IReadOnlyCollection<FileVersionManifest> manifests, CancellationToken cancellationToken = default)
        {
            RecordVersionsBatchSizes.Add(manifests.Count);
            return inner.RecordVersionsAsync(manifests, cancellationToken);
        }

        public Task<FileVersionManifest> ReadManifestAsync(string versionId, CancellationToken cancellationToken = default)
        {
            return inner.ReadManifestAsync(versionId, cancellationToken);
        }

        public Task<IReadOnlyList<FileVersionManifest>> ListManifestsAsync(CancellationToken cancellationToken = default)
        {
            ListManifestsCallCount++;
            throw new InvalidOperationException("Full manifest listing should not be used for metadata-backed mutations.");
        }

        public Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
        {
            return inner.ListVersionsAsync(cancellationToken);
        }

        public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
        {
            return inner.ListLatestEntriesAsync(cancellationToken);
        }

        public Task<FileVersionManifest?> FindLatestManifestAsync(
            string sourcePath,
            RepositoryEntryKind entryKind,
            CancellationToken cancellationToken = default)
        {
            return inner.FindLatestManifestAsync(sourcePath, entryKind, cancellationToken);
        }

        public Task<FileVersionManifest?> FindLiveFileByContentSignatureAsync(
            string sourcePath,
            string contentSignature,
            CancellationToken cancellationToken = default)
        {
            return inner.FindLiveFileByContentSignatureAsync(sourcePath, contentSignature, cancellationToken);
        }

        public Task<int> ExportOutboxAsync(
            string repositoryPath,
            IReadOnlyCollection<string> versionIds,
            CancellationToken cancellationToken = default)
        {
            ScopedOutboxExports.Add(versionIds.ToArray());
            return Task.FromResult(versionIds.Count);
        }
    }
}
