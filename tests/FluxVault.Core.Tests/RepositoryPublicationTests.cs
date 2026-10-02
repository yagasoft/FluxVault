using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class RepositoryPublicationTests
{
    [Fact]
    public async Task Restore_preserves_a_file_created_during_staging()
    {
        using var workspace = TemporaryWorkspace.Create();
        var destination = Path.Combine(workspace.RootPath, "late.txt");
        var repo = Create(workspace.RepositoryPath, new((point, _) =>
        {
            if (point == RepositoryFaultPoint.BeforeRestorePublication) File.WriteAllText(destination, "new work");
        }));
        var version = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected"));
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(version.Manifest.VersionId, destination));
        Assert.Equal("new work", await File.ReadAllTextAsync(destination));
        Assert.False(Directory.Exists(Path.Combine(workspace.RepositoryPath, "lineage")));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_post_publication_hint_failure_is_verified_success_with_a_warning(bool cancelled)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = Create(workspace.RepositoryPath, new((point, _) =>
        {
            if (point == RepositoryFaultPoint.BeforeRestoreHint)
            {
                if (cancelled) throw new OperationCanceledException();
                throw new IOException("injected hint failure");
            }
        }));
        var version = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var destination = Path.Combine(workspace.RootPath, "restored.txt");
        var result = await repo.RestoreAsync(version.Manifest.VersionId, destination);
        Assert.Equal("protected", await File.ReadAllTextAsync(destination));
        Assert.Equal(9, result.VerifiedLogicalBytes);
        Assert.Equal(1, result.RestoredFileCount);
        Assert.Contains("lineage", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task Cancellation_before_publication_preserves_destination()
    {
        using var workspace = TemporaryWorkspace.Create();
        using var cancellation = new CancellationTokenSource();
        var repo = Create(workspace.RepositoryPath, new((point, _) =>
        {
            if (point == RepositoryFaultPoint.BeforeRestorePublication) cancellation.Cancel();
        }));
        var version = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var destination = Path.Combine(workspace.RootPath, "restored.txt");
        await File.WriteAllTextAsync(destination, "keep");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.RestoreAsync(version.Manifest.VersionId, destination, cancellation.Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Partial_object_publication_records_no_version_and_retry_fails_closed()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = Create(workspace.RepositoryPath, new((point, _) =>
        {
            if (point == RepositoryFaultPoint.ObjectPayloadPublished) throw new IOException("interrupted pair publication");
        }));
        await Assert.ThrowsAnyAsync<IOException>(() => repo.CommitAsync(RepositoryIntegrityTests.Request("protected")));
        Assert.Empty(await repo.ListVersionsAsync());
        var digest = new Blake3ContentHasher().Hash(Encoding.UTF8.GetBytes("protected"));
        var payload = await File.ReadAllBytesAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, digest));
        await Assert.ThrowsAnyAsync<IOException>(() => RepositoryIntegrityTests.Create(workspace.RepositoryPath).CommitAsync(RepositoryIntegrityTests.Request("protected")));
        Assert.Equal(payload, await File.ReadAllBytesAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, digest)));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Conflicting_acknowledged_descriptors_block_deduplication()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var version = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var conflict = version.Manifest with { VersionId = Guid.NewGuid().ToString("N"),
            Chunks = [version.Manifest.Chunks[0] with { Encoding = ChunkEncoding.Zstd }] };
        await File.WriteAllTextAsync(Path.Combine(workspace.RepositoryPath, "manifests", conflict.VersionId + ".json"),
            JsonSerializer.Serialize(conflict, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var exception = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.CommitAsync(RepositoryIntegrityTests.Request("protected")));
        Assert.Equal(RepositoryIntegrityFailure.DescriptorConflict, exception.Code);
        Assert.Equal(2, (await repo.ListVersionsAsync()).Count);
    }

    [Theory]
    [InlineData("encoding")]
    [InlineData("stored")]
    [InlineData("logical")]
    public async Task Metadata_recording_refuses_a_conflicting_representation(string field)
    {
        var store = new InMemoryRepositoryMetadataStore();
        var chunk = new ManifestChunk(new string('a', 64), 0, 10, 10, ChunkEncoding.Raw);
        var first = new FileVersionManifest(Guid.NewGuid().ToString("N"), "test", "source", DateTimeOffset.UtcNow,
            CaptureConsistency.BestEffort, 10, [chunk]);
        await store.RecordVersionAsync(first);
        var second = first with { VersionId = Guid.NewGuid().ToString("N"), Chunks = [field switch
        {
            "encoding" => chunk with { Encoding = ChunkEncoding.Zstd },
            "stored" => chunk with { StoredLength = 9 },
            _ => chunk with { Length = 9 }
        }] };
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => store.RecordVersionAsync(second));
        Assert.Single(await store.ListVersionsAsync());
    }

    internal static FileSystemChunkRepository Create(string root, RepositoryFaults faults, RepositoryIntegrityLimits? limits = null) =>
        new(root, new FastCdcChunker(new ChunkingOptions(128, 256, 512)), new Blake3ContentHasher(),
            new ZstdChunkCodec(), null, null, limits, faults);
}
