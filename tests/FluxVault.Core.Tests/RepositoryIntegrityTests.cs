using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Tests;

public sealed class RepositoryIntegrityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_rejects_same_length_corruption_without_publishing(bool existingDestination)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = Create(workspace.RepositoryPath);
        var version = await repo.CommitAsync(Request("good payload to protect"));
        var chunk = Assert.Single(version.Manifest.Chunks);
        var payload = await File.ReadAllBytesAsync(ChunkPath(workspace.RepositoryPath, chunk.Digest));
        payload[0] ^= 0xff;
        await File.WriteAllBytesAsync(ChunkPath(workspace.RepositoryPath, chunk.Digest), payload);
        var destination = Path.Combine(workspace.RootPath, "restored.txt");
        if (existingDestination) await File.WriteAllTextAsync(destination, "existing work");

        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(version.Manifest.VersionId, destination));

        if (existingDestination) Assert.Equal("existing work", await File.ReadAllTextAsync(destination));
        else Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("sidecar")]
    [InlineData("both")]
    public async Task Commit_rejects_incomplete_acknowledged_object(string missing)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = Create(workspace.RepositoryPath);
        var version = await repo.CommitAsync(Request("good payload to protect"));
        var chunk = Assert.Single(version.Manifest.Chunks);
        if (missing != "sidecar") File.Delete(ChunkPath(workspace.RepositoryPath, chunk.Digest));
        if (missing != "payload") File.Delete(SidecarPath(workspace.RepositoryPath, chunk.Digest));

        await Assert.ThrowsAnyAsync<IOException>(() => repo.CommitAsync(Request("good payload to protect")));

        Assert.Single(await repo.ListVersionsAsync());
    }

    [Fact]
    public async Task Rehearsal_detects_same_length_corruption_and_preserves_other_temporary_work()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = Create(workspace.RepositoryPath);
        var version = await repo.CommitAsync(Request("good payload to protect"));
        var digest = Assert.Single(version.Manifest.Chunks).Digest;
        await File.WriteAllTextAsync(ChunkPath(workspace.RepositoryPath, digest), "bad! payload to protect");
        var tempRoot = Path.Combine(workspace.RootPath, "rehearsals");
        Directory.CreateDirectory(tempRoot);
        var sentinel = Path.Combine(tempRoot, "unrelated.txt");
        await File.WriteAllTextAsync(sentinel, "keep me");

        var result = await repo.RunRestoreRehearsalAsync(tempRoot, 1);

        Assert.Equal(1, result.FailedVersionCount);
        Assert.Equal(RepositoryHealthState.Critical, result.HealthState);
        Assert.Equal("keep me", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task Commit_rejects_corrupt_dedup_hit_without_recording_a_version()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = Create(workspace.RepositoryPath);
        var first = await repo.CommitAsync(Request("good payload to protect"));
        var digest = Assert.Single(first.Manifest.Chunks).Digest;
        await File.WriteAllTextAsync(ChunkPath(workspace.RepositoryPath, digest), "bad! payload to protect");

        await Assert.ThrowsAnyAsync<IOException>(() => repo.CommitAsync(Request("good payload to protect")));

        Assert.Single(await repo.ListVersionsAsync());
    }

    [Fact]
    public async Task Commit_preserves_the_existing_representation_when_compression_changes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = Create(workspace.RepositoryPath);
        var text = new string('x', 200);
        var first = await repo.CommitAsync(Request(text));
        var second = await repo.CommitAsync(Request(text) with { Compression = CompressionPreference.Zstd });
        Assert.Equal(first.Manifest.Chunks, second.Manifest.Chunks);
        Assert.Equal(0, second.NewChunkCount);
        var destination = Path.Combine(workspace.RootPath, "restored.txt");
        await repo.RestoreAsync(first.Manifest.VersionId, destination);
        Assert.Equal(text, await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Drain_must_not_destroy_the_only_healthy_copy()
    {
        using var workspace = TemporaryWorkspace.Create();
        var first = Path.Combine(workspace.RootPath, "first");
        var second = Path.Combine(workspace.RootPath, "second");
        var repo = Create(workspace.RepositoryPath, new MirrorSetConfiguration(
            [new("first", "First", first, true), new("second", "Second", second, true)]));
        var version = await repo.CommitAsync(Request("good payload to protect"));
        var digest = Assert.Single(version.Manifest.Chunks).Digest;
        await File.WriteAllTextAsync(ChunkPath(workspace.RepositoryPath, digest), "bad! payload to protect");
        await File.WriteAllTextAsync(ChunkPath(second, digest), "bad! payload to protect");

        await repo.RunMirrorDrainAsync("first");

        var healthySurvivor = new[] { workspace.RepositoryPath, first, second }
            .Any(root => File.Exists(ChunkPath(root, digest)) &&
                File.ReadAllText(ChunkPath(root, digest)) == "good payload to protect");
        Assert.True(healthySurvivor, "Drain removed every healthy copy.");
        var scrub = await repo.ScrubAsync(true);
        Assert.NotEqual(RepositoryHealthState.Critical, scrub.HealthState);
    }

    [Theory]
    [InlineData(-1, 22)]
    [InlineData(1, 22)]
    [InlineData(0, 21)]
    public async Task Restore_rejects_invalid_chunk_layout(long offset, long logicalLength)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = Create(workspace.RepositoryPath);
        var version = await repo.CommitAsync(Request("good payload to protect"));
        var manifest = version.Manifest with
        {
            LogicalLength = logicalLength,
            Chunks = [version.Manifest.Chunks.Single() with { Offset = offset }]
        };
        await File.WriteAllTextAsync(Path.Combine(workspace.RepositoryPath, "manifests", manifest.VersionId + ".json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var destination = Path.Combine(workspace.RootPath, "restored.txt");
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(manifest.VersionId, destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void Decoder_rejects_unknown_codec()
    {
        var codec = new ZstdChunkCodec();
        Assert.ThrowsAny<Exception>(() => codec.Decompress([1, 2, 3], 3, (ChunkEncoding)999));
    }

    [Theory]
    [InlineData(ChunkEncoding.Raw)]
    [InlineData(ChunkEncoding.Zstd)]
    [InlineData(ChunkEncoding.Lz4)]
    [InlineData(ChunkEncoding.Brotli)]
    [InlineData(ChunkEncoding.Lzma)]
    public void Decoder_rejects_extra_decoded_content(ChunkEncoding encoding)
    {
        var codec = new ZstdChunkCodec();
        var encoded = codec.Compress(Encoding.UTF8.GetBytes("too much data"), encoding, 3);
        Assert.ThrowsAny<Exception>(() => codec.Decompress(encoded, 2, encoding));
    }

    [Fact]
    public async Task A_foreign_process_lease_blocks_commit()
    {
        using var workspace = TemporaryWorkspace.Create();
        Directory.CreateDirectory(workspace.RepositoryPath);
        using var owner = new FileStream(Path.Combine(workspace.RepositoryPath, ".fluxvault.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => Create(workspace.RepositoryPath).CommitAsync(Request("content")));
    }

    [Fact]
    public async Task Unmarked_nonempty_root_is_not_silently_adopted()
    {
        using var workspace = TemporaryWorkspace.Create();
        Directory.CreateDirectory(workspace.RepositoryPath);
        var sentinel = Path.Combine(workspace.RepositoryPath, "foreign-data.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        await Assert.ThrowsAnyAsync<IOException>(() => Create(workspace.RepositoryPath).CommitAsync(Request("content")));
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task Nested_mirror_root_is_rejected()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = Create(workspace.RepositoryPath, MirrorSetConfiguration.FromLegacyPath(Path.Combine(workspace.RepositoryPath, "mirror")));
        await Assert.ThrowsAnyAsync<IOException>(() => repo.CommitAsync(Request("content")));
    }

    internal static FileSystemChunkRepository Create(string root, MirrorSetConfiguration? mirrors = null) => new(
        root, new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
        new Blake3ContentHasher(), new ZstdChunkCodec(), mirrors);

    internal static FileCommitRequest Request(string text) => new(
        "integrity", Path.GetFullPath("integrity-source.txt"), DateTimeOffset.UtcNow,
        CaptureConsistency.CrashConsistent, CompressionPreference.Off, 128,
        new MemoryStream(Encoding.UTF8.GetBytes(text)));

    internal static string ChunkPath(string root, string digest) => Path.Combine(root, "chunks", digest[..2], digest + ".chunk");
    internal static string SidecarPath(string root, string digest) => Path.Combine(root, "chunks", digest[..2], digest + ".json");
}
