using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class MetadataBackedChunkRepositoryTests
{
    [Fact]
    public async Task Commit_records_manifest_after_inner_repository_commit()
    {
        var manifest = NewManifest("version-1", @"D:\Docs\note.txt");
        var inner = new FakeChunkRepository { CommitResult = new FileCommitResult(manifest, 1) };
        var metadata = new FakeMetadataStore();
        var repository = new MetadataBackedChunkRepository(inner, metadata);

        var result = await repository.CommitAsync(new FileCommitRequest(
            Content: new MemoryStream([1, 2, 3]),
            SourcePath: @"D:\Docs\note.txt",
            WatchedFolderId: "docs",
            WatchedFolderPath: @"D:\Docs",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.BestEffort,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 0));

        Assert.Same(manifest, result.Manifest);
        Assert.Equal(["version-1"], metadata.RecordedManifests.Select(recorded => recorded.VersionId));
    }

    [Fact]
    public async Task RecordDeletion_records_deleted_manifest_when_inner_repository_returns_result()
    {
        var manifest = NewManifest("delete-1", @"D:\Docs\note.txt") with { IsDeleted = true };
        var inner = new FakeChunkRepository { DeletionResult = new RepositoryDeletionResult(manifest, []) };
        var metadata = new FakeMetadataStore();
        var repository = new MetadataBackedChunkRepository(inner, metadata);

        await repository.RecordDeletionAsync(new RepositoryDeletionRequest(
            SourcePath: @"D:\Docs\note.txt",
            WatchedFolderId: "docs",
            WatchedFolderPath: @"D:\Docs",
            IsDirectory: false,
            DeletedAtUtc: DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.BestEffort));

        Assert.Equal(["delete-1"], metadata.RecordedManifests.Select(recorded => recorded.VersionId));
    }

    [Fact]
    public async Task List_versions_reads_from_metadata_store()
    {
        var expected = new RepositoryVersionSummary(
            VersionId: "version-1",
            SourcePath: @"D:\Docs\note.txt",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.BestEffort,
            LogicalLength: 10,
            ChunkCount: 1);
        var inner = new FakeChunkRepository();
        var metadata = new FakeMetadataStore { Versions = [expected] };
        var repository = new MetadataBackedChunkRepository(inner, metadata);

        var actual = await repository.ListVersionsAsync();

        Assert.Equal([expected], actual);
        Assert.False(inner.ListVersionsCalled);
    }

    private static FileVersionManifest NewManifest(string versionId, string sourcePath)
    {
        return new FileVersionManifest(
            VersionId: versionId,
            WatchedFolderId: "docs",
            SourcePath: sourcePath,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.BestEffort,
            LogicalLength: 10,
            Chunks: [new ManifestChunk("digest", 0, 10, 8, ChunkEncoding.Zstd)]);
    }

    private sealed class FakeMetadataStore : IRepositoryMetadataStore
    {
        public List<FileVersionManifest> RecordedManifests { get; } = [];

        public IReadOnlyList<RepositoryVersionSummary> Versions { get; init; } = [];

        public Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default)
        {
            RecordedManifests.Add(manifest);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Versions);
        }

        public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Versions);
        }
    }

    private sealed class FakeChunkRepository : IChunkRepository
    {
        public FileCommitResult? CommitResult { get; init; }

        public RepositoryDeletionResult? DeletionResult { get; init; }

        public bool ListVersionsCalled { get; private set; }

        public Task<FileCommitResult> CommitAsync(FileCommitRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CommitResult ?? throw new InvalidOperationException("Commit result was not configured."));
        }

        public Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
        {
            ListVersionsCalled = true;
            return Task.FromResult<IReadOnlyList<RepositoryVersionSummary>>([]);
        }

        public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RepositoryVersionSummary>>([]);
        }

        public Task<RepositoryDeletionResult?> RecordDeletionAsync(RepositoryDeletionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DeletionResult);
        }

        public Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task RestorePreviewAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<RepositoryScrubReport> ScrubAsync(bool autoRepairFromMirror, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<MirrorRepairReport> PreviewMirrorRepairAsync(string? mirrorNodeId = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<MirrorRepairReport> RunMirrorRepairAsync(string? mirrorNodeId = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<MirrorRebalancePreviewReport> PreviewMirrorRebalanceAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<MirrorRebalancePreviewReport> RunMirrorRebalanceAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<MirrorRebalancePreviewReport> PreviewMirrorDrainAsync(string mirrorNodeId, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<MirrorRebalancePreviewReport> RunMirrorDrainAsync(string mirrorNodeId, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<RestoreRehearsalReport> RunRestoreRehearsalAsync(string tempRoot, int maxVersions, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<RepositoryRetentionPreview> PreviewRetentionAsync(RetentionPolicy policy, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<RepositoryRetentionResult> ApplyRetentionAsync(RetentionPolicy policy, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
