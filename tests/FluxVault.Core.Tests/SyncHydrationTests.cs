using System.Text;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Sync;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Core.Sync;

namespace FluxVault.Core.Tests;

public sealed class SyncHydrationTests
{
    [Fact]
    public async Task Apply_remote_version_copies_missing_chunks_and_records_remote_sync_commit()
    {
        using var remote = TemporaryWorkspace.Create();
        using var local = TemporaryWorkspace.Create();
        var remoteRepository = CreateRepository(remote.RepositoryPath);
        var localRepository = CreateRepository(local.RepositoryPath);
        var remoteCommit = await remoteRepository.CommitAsync(NewRequest("remote payload", @"D:\Work\Docs\brief.docx"));
        var targetPath = Path.Combine(local.RootPath, "brief.docx");
        var hydrator = new FileSyncHydrator(local.RepositoryPath, localRepository);
        var origin = Origin(remoteCommit.Manifest.VersionId);

        var result = await hydrator.ApplyRemoteVersionAsync(remote.RepositoryPath, remoteCommit.Manifest, targetPath, origin);

        Assert.Equal(SyncHydrationState.Applied, result.State);
        Assert.Equal("remote payload", await File.ReadAllTextAsync(targetPath));
        Assert.True(File.Exists(ChunkPath(local.RepositoryPath, remoteCommit.Manifest.Chunks[0].Digest)));
        Assert.Empty(Directory.EnumerateFiles(local.RootPath, "*.tmp", SearchOption.AllDirectories));
        var localVersion = Assert.Single(await localRepository.ListVersionsAsync());
        Assert.Equal(VersionOperationType.RemoteSync, localVersion.OperationType);
        var applied = Assert.Single(await new FileSyncApplicationStore(local.RepositoryPath).ListAppliedVersionsAsync());
        Assert.Equal(remoteCommit.Manifest.VersionId, applied.SourceVersionId);
        Assert.Equal(localVersion.VersionId, applied.LocalVersionId);
    }

    [Fact]
    public async Task Apply_remote_version_blocks_locked_target_without_overwriting()
    {
        using var remote = TemporaryWorkspace.Create();
        using var local = TemporaryWorkspace.Create();
        var remoteRepository = CreateRepository(remote.RepositoryPath);
        var localRepository = CreateRepository(local.RepositoryPath);
        var remoteCommit = await remoteRepository.CommitAsync(NewRequest("remote payload", @"D:\Work\Docs\brief.docx"));
        var targetPath = Path.Combine(local.RootPath, "brief.docx");
        await File.WriteAllTextAsync(targetPath, "local payload");
        await using var locked = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var hydrator = new FileSyncHydrator(local.RepositoryPath, localRepository);

        var result = await hydrator.ApplyRemoteVersionAsync(remote.RepositoryPath, remoteCommit.Manifest, targetPath, Origin(remoteCommit.Manifest.VersionId));

        Assert.Equal(SyncHydrationState.Blocked, result.State);
        Assert.Contains("locked", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await localRepository.ListVersionsAsync());
    }

    [Fact]
    public async Task Apply_remote_version_records_conflict_and_preserves_existing_target()
    {
        using var remote = TemporaryWorkspace.Create();
        using var local = TemporaryWorkspace.Create();
        var remoteRepository = CreateRepository(remote.RepositoryPath);
        var localRepository = CreateRepository(local.RepositoryPath);
        var remoteCommit = await remoteRepository.CommitAsync(NewRequest("remote payload", @"D:\Work\Docs\brief.docx"));
        var targetPath = Path.Combine(local.RootPath, "brief.docx");
        await File.WriteAllTextAsync(targetPath, "local payload");
        var hydrator = new FileSyncHydrator(local.RepositoryPath, localRepository);

        var result = await hydrator.ApplyRemoteVersionAsync(remote.RepositoryPath, remoteCommit.Manifest, targetPath, Origin(remoteCommit.Manifest.VersionId));

        Assert.Equal(SyncHydrationState.Conflict, result.State);
        Assert.Equal("local payload", await File.ReadAllTextAsync(targetPath));
        var conflict = Assert.Single(await new FileSyncConflictStore(local.RepositoryPath).ListConflictsAsync());
        Assert.Equal(SyncConflictStatus.Open, conflict.Status);
        Assert.Contains(SyncConflictAction.KeepLocal, conflict.AvailableActions);
        Assert.Contains(SyncConflictAction.RestoreRemoteAsCopy, conflict.AvailableActions);
    }

    [Fact]
    public async Task Resolve_conflict_marks_record_resolved_without_overwriting_target()
    {
        using var workspace = TemporaryWorkspace.Create();
        var targetPath = Path.Combine(workspace.RootPath, "brief.docx");
        await File.WriteAllTextAsync(targetPath, "local payload");
        var store = new FileSyncConflictStore(workspace.RepositoryPath);
        var conflict = await store.RecordConflictAsync(new SyncConflictRecord(
            ConflictId: "conflict-1",
            LocalPath: targetPath,
            SourceDeviceId: "device-laptop",
            SourceVersionId: "remote-version-1",
            SourceOperationId: "operation-1",
            DetectedAtUtc: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            Status: SyncConflictStatus.Open,
            AvailableActions:
            [
                SyncConflictAction.KeepLocal,
                SyncConflictAction.KeepRemote,
                SyncConflictAction.RestoreRemoteAsCopy,
                SyncConflictAction.MarkResolved
            ]));

        var resolved = await store.ResolveConflictAsync(conflict.ConflictId, SyncConflictAction.KeepLocal);

        Assert.Equal(SyncConflictStatus.Resolved, resolved.Status);
        Assert.Equal(SyncConflictAction.KeepLocal, resolved.ResolutionAction);
        Assert.Equal("local payload", await File.ReadAllTextAsync(targetPath));
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

    private static SyncOriginMetadata Origin(string sourceVersionId)
    {
        return new SyncOriginMetadata(
            SourceDeviceId: "device-laptop",
            SourceOperationId: "operation-42",
            SourceVersionId: sourceVersionId,
            AppliedAtUtc: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            MappingId: "mapping-1");
    }

    private static string ChunkPath(string root, string digest)
    {
        return Path.Combine(root, "chunks", digest[..2], $"{digest}.chunk");
    }
}
