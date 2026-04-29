using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Tests;

public sealed class RepositoryLineageTests
{
    [Fact]
    public void Old_manifest_json_without_lineage_defaults_to_capture()
    {
        const string json = """
            {
              "versionId": "old-version",
              "watchedFolderId": "docs",
              "sourcePath": "D:\\Work\\Docs\\brief.docx",
              "capturedAtUtc": "2026-04-27T10:30:00+00:00",
              "consistency": 2,
              "logicalLength": 12,
              "chunks": []
            }
            """;

        var manifest = JsonSerializer.Deserialize<FileVersionManifest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(manifest);
        Assert.Equal(VersionOperationType.Capture, manifest.OperationType);
        Assert.Empty(manifest.ParentVersionIds ?? []);
        Assert.Null(manifest.RestoredFromVersionId);
        Assert.Null(manifest.ForkOriginVersionId);
    }

    [Fact]
    public async Task Same_path_capture_records_parent_and_content_signature()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var sourcePath = @"D:\Work\Docs\brief.docx";

        var first = await repository.CommitAsync(NewRequest("first version", sourcePath));
        var second = await repository.CommitAsync(NewRequest("second version", sourcePath));

        Assert.Equal(VersionOperationType.Capture, second.Manifest.OperationType);
        Assert.Equal([first.Manifest.VersionId], second.Manifest.ParentVersionIds);
        Assert.False(string.IsNullOrWhiteSpace(second.Manifest.ContentSignature));
        Assert.NotEqual(first.Manifest.ContentSignature, second.Manifest.ContentSignature);
    }

    [Fact]
    public async Task Restore_records_no_immediate_version_and_next_capture_records_restore_lineage()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var payload = Encoding.UTF8.GetBytes("restored payload");
        var original = await repository.CommitAsync(NewRequest(payload, @"D:\Work\Docs\brief.docx"));
        var destination = Path.Combine(workspace.RootPath, "watched", "brief-restored.docx");

        await repository.RestoreAsync(original.Manifest.VersionId, destination);

        Assert.Single(await repository.ListVersionsAsync());

        var restoreCapture = await repository.CommitAsync(NewRequest(payload, destination));

        Assert.Equal(VersionOperationType.Restore, restoreCapture.Manifest.OperationType);
        Assert.Equal(original.Manifest.VersionId, restoreCapture.Manifest.RestoredFromVersionId);
        Assert.Equal(original.Manifest.VersionId, restoreCapture.Manifest.ForkOriginVersionId);
        Assert.Empty(restoreCapture.Manifest.ParentVersionIds ?? []);
    }

    [Fact]
    public async Task Different_path_identical_content_records_visible_inherited_copy_without_new_chunks()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var payload = Encoding.UTF8.GetBytes("shared payload");
        var original = await repository.CommitAsync(NewRequest(payload, @"D:\Work\Docs\brief.docx"));

        var copy = await repository.CommitAsync(NewRequest(payload, @"D:\Work\Copies\brief-copy.docx"));

        Assert.Equal(0, copy.NewChunkCount);
        Assert.Equal(VersionOperationType.InheritedCopy, copy.Manifest.OperationType);
        Assert.Equal(original.Manifest.VersionId, copy.Manifest.InheritedFromVersionId);
        Assert.Equal(original.Manifest.SourcePath, copy.Manifest.InheritedFromSourcePath);
        Assert.Equal([original.Manifest.VersionId], copy.Manifest.ParentVersionIds);
        Assert.Equal(original.Manifest.VersionId, copy.Manifest.ForkOriginVersionId);
        Assert.Equal(2, (await repository.ListVersionsAsync()).Count);
    }

    [Fact]
    public async Task Modified_inherited_copy_keeps_parent_and_fork_origin_lineage()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var copyPath = @"D:\Work\Copies\brief-copy.docx";
        var original = await repository.CommitAsync(NewRequest("shared payload", @"D:\Work\Docs\brief.docx"));
        var inherited = await repository.CommitAsync(NewRequest("shared payload", copyPath));

        var changed = await repository.CommitAsync(NewRequest("changed payload", copyPath));

        Assert.Equal(VersionOperationType.Capture, changed.Manifest.OperationType);
        Assert.Equal([inherited.Manifest.VersionId], changed.Manifest.ParentVersionIds);
        Assert.Equal(original.Manifest.VersionId, changed.Manifest.ForkOriginVersionId);
    }

    [Fact]
    public async Task Retention_preserves_chunks_referenced_by_remaining_inherited_copy_manifest()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var payload = Encoding.UTF8.GetBytes("shared payload");
        await repository.CommitAsync(NewRequest(payload, @"D:\Work\Docs\brief.docx", now.AddDays(-2)));
        var latestOriginal = await repository.CommitAsync(NewRequest("latest original", @"D:\Work\Docs\brief.docx", now.AddDays(-1)));
        var inherited = await repository.CommitAsync(NewRequest(payload, @"D:\Work\Copies\brief-copy.docx", now));

        var result = await repository.ApplyRetentionAsync(new RetentionPolicy(
            IsEnabled: true,
            KeepAllFor: TimeSpan.Zero,
            KeepHourlyFor: TimeSpan.Zero,
            KeepDailyFor: TimeSpan.Zero,
            MinimumVersionsPerFile: 1), now);

        var versions = await repository.ListVersionsAsync();
        var restorePath = Path.Combine(workspace.RootPath, "copy.restore");
        await repository.RestoreAsync(inherited.Manifest.VersionId, restorePath);

        Assert.Equal(1, result.PrunedVersionCount);
        Assert.Contains(versions, version => version.VersionId == latestOriginal.Manifest.VersionId);
        Assert.Contains(versions, version => version.VersionId == inherited.Manifest.VersionId);
        Assert.Equal(payload, await File.ReadAllBytesAsync(restorePath));
    }

    private static FileSystemChunkRepository CreateRepository(string path)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec());
    }

    private static FileCommitRequest NewRequest(
        string payload,
        string sourcePath,
        DateTimeOffset? capturedAtUtc = null)
    {
        return NewRequest(Encoding.UTF8.GetBytes(payload), sourcePath, capturedAtUtc);
    }

    private static FileCommitRequest NewRequest(
        byte[] payload,
        string sourcePath,
        DateTimeOffset? capturedAtUtc = null)
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
