using System.Text;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Tests;

public sealed class RepositoryTests
{
    [Fact]
    public async Task Commit_and_restore_preserves_file_bytes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("versioned file content ", 300)));

        var result = await repository.CommitAsync(new FileCommitRequest(
            WatchedFolderId: "docs",
            SourcePath: @"D:\Work\Docs\brief.docx",
            CapturedAtUtc: new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.Zero),
            Consistency: CaptureConsistency.CrashConsistent,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 128,
            Content: new MemoryStream(payload)));

        var restoredPath = Path.Combine(workspace.RootPath, "restored.bin");
        await repository.RestoreAsync(result.Manifest.VersionId, restoredPath);

        Assert.Equal(payload, await File.ReadAllBytesAsync(restoredPath));
        Assert.Equal(payload.Length, result.Manifest.LogicalLength);
        Assert.True(result.NewChunkCount > 0);
    }

    [Fact]
    public async Task Commit_reuses_existing_chunks_for_identical_content()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("same large content ", 256)));

        await repository.CommitAsync(NewRequest(payload));
        var second = await repository.CommitAsync(NewRequest(payload));

        Assert.Equal(0, second.NewChunkCount);
    }

    [Fact]
    public async Task List_versions_returns_newest_versions_first()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);

        var older = await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("older content"),
            new DateTimeOffset(2026, 4, 27, 8, 0, 0, TimeSpan.Zero)));
        var newer = await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("newer content"),
            new DateTimeOffset(2026, 4, 27, 9, 0, 0, TimeSpan.Zero)));

        var versions = await repository.ListVersionsAsync();

        Assert.Collection(
            versions,
            first => Assert.Equal(newer.Manifest.VersionId, first.VersionId),
            second => Assert.Equal(older.Manifest.VersionId, second.VersionId));
    }

    [Fact]
    public async Task Inspect_returns_manifest_for_existing_version()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var result = await repository.CommitAsync(NewRequest(Encoding.UTF8.GetBytes("inspect content")));

        var inspection = await repository.InspectAsync(result.Manifest.VersionId);

        Assert.Equal(result.Manifest.VersionId, inspection.Manifest.VersionId);
        Assert.Equal(result.Manifest.Chunks.Count, inspection.ChunkCount);
        Assert.Equal(result.Manifest.LogicalLength, inspection.LogicalLength);
    }

    private static FileSystemChunkRepository CreateRepository(string path)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec());
    }

    private static FileCommitRequest NewRequest(byte[] payload, DateTimeOffset? capturedAtUtc = null)
    {
        return new FileCommitRequest(
            WatchedFolderId: "docs",
            SourcePath: @"D:\Work\Docs\brief.docx",
            CapturedAtUtc: capturedAtUtc ?? DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.CrashConsistent,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 128,
            Content: new MemoryStream(payload));
    }
}
