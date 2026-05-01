using System.Text;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Sync;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Core.Sync;

namespace FluxVault.Core.Tests;

public sealed class SyncLoopPreventionTests
{
    [Fact]
    public async Task Commit_with_sync_origin_marks_manifest_and_publish_gate_blocks_republication()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var syncOrigin = new SyncOriginMetadata(
            SourceDeviceId: "device-laptop",
            SourceOperationId: "operation-42",
            SourceVersionId: "remote-version-42",
            AppliedAtUtc: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            MappingId: "mapping-1");

        var commit = await repository.CommitAsync(NewRequest("remote payload", @"D:\Work\Docs\brief.docx") with
        {
            SyncOrigin = syncOrigin
        });
        var versions = await repository.ListVersionsAsync();

        Assert.Equal(VersionOperationType.RemoteSync, commit.Manifest.OperationType);
        Assert.Equal(syncOrigin, commit.Manifest.SyncOrigin);
        Assert.False(SyncPublishGate.ShouldPublishLocalChange(commit.Manifest, localDeviceId: "device-local"));
        var summary = Assert.Single(versions);
        Assert.Equal(VersionOperationType.RemoteSync, summary.OperationType);
        Assert.Equal("remote-version-42", summary.SyncOrigin!.SourceVersionId);
    }

    [Fact]
    public async Task Applied_remote_version_records_round_trip_and_deduplicate_by_source_operation()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileSyncApplicationStore(workspace.RepositoryPath);
        var first = new SyncAppliedVersionRecord(
            SourceDeviceId: "device-laptop",
            SourceOperationId: "operation-42",
            SourceVersionId: "remote-version-42",
            LocalVersionId: "local-version-1",
            LocalPath: @"D:\Work\Docs\brief.docx",
            ContentSignature: "sig-42",
            AppliedAtUtc: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            MappingId: "mapping-1");
        var second = first with
        {
            LocalVersionId = "local-version-2",
            AppliedAtUtc = first.AppliedAtUtc.AddMinutes(1)
        };

        await store.RecordAppliedVersionAsync(first);
        await store.RecordAppliedVersionAsync(second);

        var applied = Assert.Single(await store.ListAppliedVersionsAsync());
        Assert.Equal("local-version-2", applied.LocalVersionId);
        Assert.True(await store.HasAppliedSourceOperationAsync("device-laptop", "operation-42"));
        Assert.False(await store.HasAppliedSourceOperationAsync("device-laptop", "operation-43"));
    }

    private static FileSystemChunkRepository CreateRepository(string path)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec());
    }

    private static FileCommitRequest NewRequest(string payload, string sourcePath)
    {
        return new FileCommitRequest(
            WatchedFolderId: "docs",
            SourcePath: sourcePath,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.CrashConsistent,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 128,
            Content: new MemoryStream(Encoding.UTF8.GetBytes(payload)));
    }
}
