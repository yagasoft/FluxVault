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

    private static FileSystemChunkRepository CreateRepository(string path)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec());
    }

    private static FileCommitRequest NewRequest(byte[] payload)
    {
        return new FileCommitRequest(
            WatchedFolderId: "docs",
            SourcePath: @"D:\Work\Docs\brief.docx",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.CrashConsistent,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 128,
            Content: new MemoryStream(payload));
    }
}
