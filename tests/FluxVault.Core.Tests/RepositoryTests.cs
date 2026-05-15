using System.Text;
using FluxVault.Abstractions.Configuration;
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
    public async Task Commit_creates_folder_versions_for_parent_chain()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var sourcePath = Path.GetFullPath(@"D:\Work\Docs\Reports\brief.txt");

        var file = await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("folder cascade"),
            sourcePath: sourcePath,
            watchedFolderPath: @"D:\Work"));

        var versions = await repository.ListVersionsAsync();
        var reports = Assert.Single(versions, version => version.SourcePath == Path.GetFullPath(@"D:\Work\Docs\Reports"));
        var docs = Assert.Single(versions, version => version.SourcePath == Path.GetFullPath(@"D:\Work\Docs"));
        var root = Assert.Single(versions, version => version.SourcePath == Path.GetFullPath(@"D:\Work"));

        Assert.Equal(RepositoryEntryKind.File, file.Manifest.EntryKind);
        Assert.Equal(RepositoryEntryKind.Folder, reports.EntryKind);
        Assert.Equal(RepositoryEntryKind.Folder, docs.EntryKind);
        Assert.Equal(RepositoryEntryKind.Folder, root.EntryKind);
        Assert.Contains(reports.FolderEntries ?? [], entry =>
            entry.SourcePath == sourcePath
            && entry.VersionId == file.Manifest.VersionId
            && entry.EntryKind == RepositoryEntryKind.File
            && !entry.IsDeleted);
        Assert.Contains(docs.FolderEntries ?? [], entry =>
            entry.SourcePath == Path.GetFullPath(@"D:\Work\Docs\Reports")
            && entry.VersionId == reports.VersionId
            && entry.EntryKind == RepositoryEntryKind.Folder);
    }

    [Fact]
    public async Task Folder_version_restore_recreates_snapshot_children()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("alpha"),
            sourcePath: @"D:\Work\Docs\a.txt",
            watchedFolderPath: @"D:\Work"));
        await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("bravo"),
            sourcePath: @"D:\Work\Docs\Nested\b.txt",
            watchedFolderPath: @"D:\Work"));
        var folderVersion = (await repository.ListVersionsAsync())
            .Where(version => version.EntryKind == RepositoryEntryKind.Folder)
            .First(version => version.SourcePath == Path.GetFullPath(@"D:\Work\Docs"));
        var restoreRoot = Path.Combine(workspace.RootPath, "folder-restore");

        await repository.RestoreAsync(folderVersion.VersionId, restoreRoot);

        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(restoreRoot, "a.txt")));
        Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Combine(restoreRoot, "Nested", "b.txt")));
    }

    [Fact]
    public async Task Record_deletion_writes_tombstone_and_restores_previous_file_version()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var sourcePath = Path.GetFullPath(@"D:\Work\Docs\brief.txt");
        var payload = Encoding.UTF8.GetBytes("before delete");
        var original = await repository.CommitAsync(NewRequest(payload, sourcePath: sourcePath, watchedFolderPath: @"D:\Work"));

        var deletion = await repository.RecordDeletionAsync(new RepositoryDeletionRequest(
            WatchedFolderId: "docs",
            WatchedFolderPath: @"D:\Work",
            SourcePath: sourcePath,
            IsDirectory: false,
            DeletedAtUtc: DateTimeOffset.UtcNow));
        Assert.NotNull(deletion);

        var latest = await repository.ListLatestEntriesAsync();
        var tombstone = Assert.Single(latest, version => version.SourcePath == sourcePath && version.EntryKind == RepositoryEntryKind.File);
        var parentFolder = Assert.Single(latest, version => version.SourcePath == Path.GetFullPath(@"D:\Work\Docs") && version.EntryKind == RepositoryEntryKind.Folder);
        var restoredPath = Path.Combine(workspace.RootPath, "deleted.restore");
        await repository.RestoreAsync(deletion.Manifest.VersionId, restoredPath);

        Assert.True(tombstone.IsDeleted);
        Assert.Equal(original.Manifest.VersionId, tombstone.DeletedFromVersionId);
        Assert.Contains(parentFolder.FolderEntries ?? [], entry => entry.SourcePath == sourcePath && entry.IsDeleted);
        Assert.Equal(payload, await File.ReadAllBytesAsync(restoredPath));
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

    [Fact]
    public async Task Purge_file_removes_history_and_unreferenced_primary_and_mirror_chunks()
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirrorPath = Path.Combine(workspace.RootPath, "mirror");
        var repository = CreateRepository(workspace.RepositoryPath, mirrorPath);
        var removedPath = Path.GetFullPath(@"D:\Work\Docs\remove.txt");
        var keptPath = Path.GetFullPath(@"D:\Work\Docs\keep.txt");
        var removed = await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("remove unique ", 128))),
            sourcePath: removedPath,
            watchedFolderPath: @"D:\Work"));
        var kept = await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("keep unique ", 128))),
            sourcePath: keptPath,
            watchedFolderPath: @"D:\Work"));

        var result = await repository.PurgeAsync(new RepositoryPurgeRequest(
            [new RepositoryPurgeScope(removedPath, RepositoryPurgeScopeKind.File)]));

        var versions = await repository.ListVersionsAsync();
        Assert.True(result.PurgedVersionCount >= 1);
        Assert.True(result.DeletedChunkCount >= 1);
        Assert.True(result.ReclaimedBytes > 0);
        Assert.DoesNotContain(versions, version => version.SourcePath == removedPath);
        Assert.DoesNotContain(versions.SelectMany(version => version.FolderEntries ?? []), entry => entry.SourcePath == removedPath);
        Assert.Contains(versions, version => version.VersionId == kept.Manifest.VersionId);
        Assert.False(File.Exists(Path.Combine(workspace.RepositoryPath, "manifests", $"{removed.Manifest.VersionId}.json")));
        Assert.False(File.Exists(Path.Combine(mirrorPath, "manifests", $"{removed.Manifest.VersionId}.json")));

        var restoredPath = Path.Combine(workspace.RootPath, "kept.restore");
        await repository.RestoreAsync(kept.Manifest.VersionId, restoredPath);
        Assert.Equal(string.Concat(Enumerable.Repeat("keep unique ", 128)), await File.ReadAllTextAsync(restoredPath));
    }

    [Fact]
    public async Task Purge_immediate_folder_keeps_nested_file_history()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var directPath = Path.GetFullPath(@"D:\Work\Docs\direct.txt");
        var nestedPath = Path.GetFullPath(@"D:\Work\Docs\Nested\nested.txt");
        await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("direct"),
            sourcePath: directPath,
            watchedFolderPath: @"D:\Work"));
        var nested = await repository.CommitAsync(NewRequest(
            Encoding.UTF8.GetBytes("nested"),
            sourcePath: nestedPath,
            watchedFolderPath: @"D:\Work"));

        await repository.PurgeAsync(new RepositoryPurgeRequest(
            [new RepositoryPurgeScope(Path.GetFullPath(@"D:\Work\Docs"), RepositoryPurgeScopeKind.ImmediateFiles)]));

        var versions = await repository.ListVersionsAsync();
        Assert.DoesNotContain(versions, version => version.SourcePath == directPath);
        Assert.Contains(versions, version => version.VersionId == nested.Manifest.VersionId);
        var restoredPath = Path.Combine(workspace.RootPath, "nested.restore");
        await repository.RestoreAsync(nested.Manifest.VersionId, restoredPath);
        Assert.Equal("nested", await File.ReadAllTextAsync(restoredPath));
    }

    private static FileSystemChunkRepository CreateRepository(string path)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec());
    }

    private static FileSystemChunkRepository CreateRepository(string path, string mirrorPath)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            new MirrorSetConfiguration(
                [new MirrorNodeConfiguration("mirror", "Mirror", mirrorPath, IsEnabled: true)],
                MirrorPlacementPolicyConfiguration.CreateDefault()));
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
        string sourcePath = @"D:\Work\Docs\brief.docx",
        string? watchedFolderPath = null)
    {
        return new FileCommitRequest(
            WatchedFolderId: "docs",
            SourcePath: sourcePath,
            CapturedAtUtc: capturedAtUtc ?? DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.CrashConsistent,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 128,
            Content: new MemoryStream(payload),
            WatchedFolderPath: watchedFolderPath);
    }
}
