using System.Text;
using FluxVault.Abstractions.Configuration;
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
    public async Task Mirror_set_writes_committed_artifacts_to_all_enabled_nodes_atomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.Integration", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "local");
        var firstMirror = Path.Combine(root, "cloud-folder");
        var secondMirror = Path.Combine(root, "usb-folder");

        try
        {
            var repository = new FileSystemChunkRepository(
                local,
                new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
                new Blake3ContentHasher(),
                new ZstdChunkCodec(),
                new MirrorSetConfiguration(
                [
                    new MirrorNodeConfiguration("cloud", "Cloud folder", firstMirror, IsEnabled: true),
                    new MirrorNodeConfiguration("usb", "USB folder", secondMirror, IsEnabled: true)
                ]));

            var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("mirror set content ", 256)));
            var result = await repository.CommitAsync(new FileCommitRequest(
                WatchedFolderId: "docs",
                SourcePath: @"D:\Work\Docs\mirrorset.docx",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Consistency: CaptureConsistency.CrashConsistent,
                Compression: CompressionPreference.Zstd,
                MinimumCompressionBytes: 128,
                Content: new MemoryStream(payload)));

            foreach (var mirror in new[] { firstMirror, secondMirror })
            {
                Assert.True(File.Exists(Path.Combine(mirror, "manifests", $"{result.Manifest.VersionId}.json")));
                Assert.True(Directory.EnumerateFiles(Path.Combine(mirror, "chunks"), "*.chunk", SearchOption.AllDirectories).Any());
                Assert.Empty(Directory.EnumerateFiles(mirror, "*.tmp", SearchOption.AllDirectories));
            }

            Assert.Empty(result.MirrorWarnings);
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
    public async Task Unavailable_mirror_set_node_returns_warning_without_failing_primary_commit()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.Integration", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "local");
        var unavailableMirror = Path.Combine(root, "unavailable-mirror");

        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(unavailableMirror, "this file blocks directory creation");
            var repository = new FileSystemChunkRepository(
                local,
                new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
                new Blake3ContentHasher(),
                new ZstdChunkCodec(),
                new MirrorSetConfiguration(
                [
                    new MirrorNodeConfiguration("offline", "Offline mirror", unavailableMirror, IsEnabled: true)
                ]));

            var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("primary protection continues ", 256)));
            var result = await repository.CommitAsync(new FileCommitRequest(
                WatchedFolderId: "docs",
                SourcePath: @"D:\Work\Docs\offline-mirror.docx",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Consistency: CaptureConsistency.CrashConsistent,
                Compression: CompressionPreference.Zstd,
                MinimumCompressionBytes: 128,
                Content: new MemoryStream(payload)));

            Assert.True(File.Exists(Path.Combine(local, "manifests", $"{result.Manifest.VersionId}.json")));
            var warning = Assert.Single(result.MirrorWarnings);
            Assert.Contains("Offline mirror", warning);
            Assert.Contains(unavailableMirror, warning);
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
