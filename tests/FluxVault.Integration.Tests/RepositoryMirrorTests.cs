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
}
