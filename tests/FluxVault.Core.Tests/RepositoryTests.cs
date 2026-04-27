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

    [Theory]
    [InlineData(CompressionPreference.Lz4)]
    [InlineData(CompressionPreference.Brotli)]
    [InlineData(CompressionPreference.Lzma)]
    public async Task Commit_and_restore_supports_expanded_codecs(CompressionPreference compression)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("codec payload ", 500)));

        var result = await repository.CommitAsync(NewRequest(payload) with
        {
            Compression = compression,
            MinimumCompressionBytes = 1
        });

        var restoredPath = Path.Combine(workspace.RootPath, $"{compression}.restore");
        await repository.RestoreAsync(result.Manifest.VersionId, restoredPath);

        Assert.Equal(payload, await File.ReadAllBytesAsync(restoredPath));
    }

    [Fact]
    public async Task Preview_retention_reports_prunable_versions_without_mutating_repository()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var now = new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);
        await CommitSeriesAsync(repository, 25, now.AddDays(-220));

        var preview = await repository.PreviewRetentionAsync(new RetentionPolicy(
            IsEnabled: true,
            KeepAllFor: TimeSpan.FromHours(24),
            KeepHourlyFor: TimeSpan.FromDays(30),
            KeepDailyFor: TimeSpan.FromDays(180),
            MinimumVersionsPerFile: 20), now);

        var versionsAfterPreview = await repository.ListVersionsAsync();
        Assert.Equal(25, versionsAfterPreview.Count);
        Assert.Equal(20, preview.KeptVersionCount);
        Assert.Equal(5, preview.PrunableVersionCount);
        Assert.True(preview.EstimatedReclaimableBytes > 0);
    }

    [Fact]
    public async Task Apply_retention_prunes_manifests_and_kept_versions_restore()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var now = new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);
        await CommitSeriesAsync(repository, 25, now.AddDays(-220));

        var result = await repository.ApplyRetentionAsync(new RetentionPolicy(
            IsEnabled: true,
            KeepAllFor: TimeSpan.FromHours(24),
            KeepHourlyFor: TimeSpan.FromDays(30),
            KeepDailyFor: TimeSpan.FromDays(180),
            MinimumVersionsPerFile: 20), now);

        var versions = await repository.ListVersionsAsync();
        Assert.Equal(20, versions.Count);
        Assert.Equal(5, result.PrunedVersionCount);
        Assert.True(result.DeletedChunkCount > 0);
        foreach (var version in versions)
        {
            var restoredPath = Path.Combine(workspace.RootPath, $"{version.VersionId}.restore");
            await repository.RestoreAsync(version.VersionId, restoredPath);
            Assert.True(new FileInfo(restoredPath).Length > 0);
        }
    }

    [Fact]
    public async Task Apply_retention_keeps_chunks_referenced_by_remaining_manifests()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var now = new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);
        var sharedPayload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("shared content ", 300)));

        await repository.CommitAsync(NewRequest(
            sharedPayload,
            now.AddDays(-220),
            sourcePath: @"D:\Work\Docs\first.docx"));
        var kept = await repository.CommitAsync(NewRequest(
            sharedPayload,
            now.AddDays(-219),
            sourcePath: @"D:\Work\Docs\second.docx"));

        var result = await repository.ApplyRetentionAsync(new RetentionPolicy(
            IsEnabled: true,
            KeepAllFor: TimeSpan.Zero,
            KeepHourlyFor: TimeSpan.Zero,
            KeepDailyFor: TimeSpan.Zero,
            MinimumVersionsPerFile: 1), now);

        Assert.Equal(0, result.DeletedChunkCount);
        var restoredPath = Path.Combine(workspace.RootPath, "second.restore");
        await repository.RestoreAsync(kept.Manifest.VersionId, restoredPath);
        Assert.Equal(sharedPayload, await File.ReadAllBytesAsync(restoredPath));
    }

    private static FileSystemChunkRepository CreateRepository(string path)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec());
    }

    private static async Task CommitSeriesAsync(
        FileSystemChunkRepository repository,
        int count,
        DateTimeOffset firstCaptureAtUtc)
    {
        for (var index = 0; index < count; index++)
        {
            var payload = Encoding.UTF8.GetBytes(string.Concat(
                Enumerable.Repeat($"version {index:D2} unique content ", 128)));
            await repository.CommitAsync(NewRequest(payload, firstCaptureAtUtc.AddMinutes(index)));
        }
    }

    private static FileCommitRequest NewRequest(
        byte[] payload,
        DateTimeOffset? capturedAtUtc = null,
        string sourcePath = @"D:\Work\Docs\brief.docx")
    {
        return new FileCommitRequest(
            WatchedFolderId: "docs",
            SourcePath: sourcePath,
            CapturedAtUtc: capturedAtUtc ?? DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.CrashConsistent,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 128,
            Content: new MemoryStream(payload));
    }
}
