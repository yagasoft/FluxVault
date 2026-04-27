using System.Text;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;

namespace FluxVault.Integration.Tests;

public sealed class RepositoryMirrorTests
{
    [Fact]
    public async Task Cloud_folder_mirror_receives_committed_manifest_atomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.Integration", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "local");
        var mirror = Path.Combine(root, "cloud-folder");

        try
        {
            var repository = new FileSystemChunkRepository(
                local,
                new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
                new Blake3ContentHasher(),
                new ZstdChunkCodec(),
                mirror);

            var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("mirrored content ", 256)));
            var result = await repository.CommitAsync(new FileCommitRequest(
                WatchedFolderId: "docs",
                SourcePath: @"D:\Work\Docs\mirrored.docx",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Consistency: CaptureConsistency.CrashConsistent,
                Compression: CompressionPreference.Zstd,
                MinimumCompressionBytes: 128,
                Content: new MemoryStream(payload)));

            var mirrorManifest = Path.Combine(mirror, "manifests", $"{result.Manifest.VersionId}.json");

            Assert.True(File.Exists(mirrorManifest));
            Assert.Empty(Directory.EnumerateFiles(mirror, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Retention_removes_pruned_mirror_artifacts_without_temporary_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.Integration", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "local");
        var mirror = Path.Combine(root, "cloud-folder");

        try
        {
            var repository = new FileSystemChunkRepository(
                local,
                new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
                new Blake3ContentHasher(),
                new ZstdChunkCodec(),
                mirror);

            var now = new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);
            for (var index = 0; index < 3; index++)
            {
                var payload = Encoding.UTF8.GetBytes($"mirrored retention content {index}");
                await repository.CommitAsync(new FileCommitRequest(
                    WatchedFolderId: "docs",
                    SourcePath: @"D:\Work\Docs\mirrored.docx",
                    CapturedAtUtc: now.AddDays(-220).AddMinutes(index),
                    Consistency: CaptureConsistency.CrashConsistent,
                    Compression: CompressionPreference.Off,
                    MinimumCompressionBytes: 128,
                    Content: new MemoryStream(payload)));
            }

            var result = await repository.ApplyRetentionAsync(new RetentionPolicy(
                IsEnabled: true,
                KeepAllFor: TimeSpan.Zero,
                KeepHourlyFor: TimeSpan.Zero,
                KeepDailyFor: TimeSpan.Zero,
                MinimumVersionsPerFile: 1), now);

            Assert.Equal(2, result.PrunedVersionCount);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(mirror, "manifests"), "*.json"));
            Assert.Empty(Directory.EnumerateFiles(mirror, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Retention_reports_mirror_delete_failure_without_blocking_local_prune()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.Integration", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "local");
        var mirror = Path.Combine(root, "cloud-folder");

        try
        {
            var repository = new FileSystemChunkRepository(
                local,
                new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
                new Blake3ContentHasher(),
                new ZstdChunkCodec(),
                mirror);

            var now = new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);
            var pruned = await repository.CommitAsync(new FileCommitRequest(
                WatchedFolderId: "docs",
                SourcePath: @"D:\Work\Docs\mirrored.docx",
                CapturedAtUtc: now.AddDays(-220),
                Consistency: CaptureConsistency.CrashConsistent,
                Compression: CompressionPreference.Off,
                MinimumCompressionBytes: 128,
                Content: new MemoryStream(Encoding.UTF8.GetBytes("old mirrored content"))));
            await repository.CommitAsync(new FileCommitRequest(
                WatchedFolderId: "docs",
                SourcePath: @"D:\Work\Docs\mirrored.docx",
                CapturedAtUtc: now.AddDays(-219),
                Consistency: CaptureConsistency.CrashConsistent,
                Compression: CompressionPreference.Off,
                MinimumCompressionBytes: 128,
                Content: new MemoryStream(Encoding.UTF8.GetBytes("new mirrored content"))));
            var mirrorManifest = Path.Combine(mirror, "manifests", $"{pruned.Manifest.VersionId}.json");
            File.SetAttributes(mirrorManifest, FileAttributes.ReadOnly);

            var result = await repository.ApplyRetentionAsync(new RetentionPolicy(
                IsEnabled: true,
                KeepAllFor: TimeSpan.Zero,
                KeepHourlyFor: TimeSpan.Zero,
                KeepDailyFor: TimeSpan.Zero,
                MinimumVersionsPerFile: 1), now);

            Assert.Equal(1, result.PrunedVersionCount);
            Assert.NotEmpty(result.MirrorWarnings);
            Assert.Single(await repository.ListVersionsAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
        }
    }
}
