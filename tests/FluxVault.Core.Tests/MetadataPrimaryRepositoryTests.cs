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
}
