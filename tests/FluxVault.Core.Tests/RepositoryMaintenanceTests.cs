using System.Text;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Tests;

public sealed class RepositoryMaintenanceTests
{
    [Fact]
    public async Task Healthy_repository_scrub_reports_healthy()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        await repository.CommitAsync(NewRequest("healthy content"));

        var report = await repository.ScrubAsync(autoRepairFromMirror: true);

        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        Assert.Equal(1, report.ManifestCount);
        Assert.True(report.CheckedChunkCount > 0);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task Missing_primary_chunk_is_repaired_from_healthy_mirror()
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirrorPath = Path.Combine(workspace.RootPath, "mirror");
        var repository = CreateRepository(workspace.RepositoryPath, mirrorPath);
        var commit = await repository.CommitAsync(NewRequest("mirrored content"));
        var digest = Assert.Single(commit.Manifest.Chunks).Digest;
        File.Delete(ChunkPath(workspace.RepositoryPath, digest));

        var report = await repository.ScrubAsync(autoRepairFromMirror: true);

        var issue = Assert.Single(report.Issues);
        Assert.Equal(RepositoryScrubIssueKind.MissingChunk, issue.Kind);
        Assert.Equal(RepositoryRepairAction.RepairedPrimaryFromMirror, issue.RepairAction);
        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        Assert.True(File.Exists(ChunkPath(workspace.RepositoryPath, digest)));
        await AssertRestoresAsync(repository, commit.Manifest.VersionId, workspace);
    }

    [Fact]
    public async Task Corrupt_primary_chunk_is_repaired_from_healthy_mirror()
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirrorPath = Path.Combine(workspace.RootPath, "mirror");
        var repository = CreateRepository(workspace.RepositoryPath, mirrorPath);
        var commit = await repository.CommitAsync(NewRequest("mirrored content"));
        var digest = Assert.Single(commit.Manifest.Chunks).Digest;
        await File.WriteAllTextAsync(ChunkPath(workspace.RepositoryPath, digest), "corrupt");

        var report = await repository.ScrubAsync(autoRepairFromMirror: true);

        var issue = Assert.Single(report.Issues);
        Assert.Equal(RepositoryScrubIssueKind.CorruptChunk, issue.Kind);
        Assert.Equal(RepositoryRepairAction.RepairedPrimaryFromMirror, issue.RepairAction);
        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        await AssertRestoresAsync(repository, commit.Manifest.VersionId, workspace);
    }

    [Fact]
    public async Task Missing_mirror_chunk_is_repaired_from_healthy_primary()
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirrorPath = Path.Combine(workspace.RootPath, "mirror");
        var repository = CreateRepository(workspace.RepositoryPath, mirrorPath);
        var commit = await repository.CommitAsync(NewRequest("mirrored content"));
        var digest = Assert.Single(commit.Manifest.Chunks).Digest;
        File.Delete(ChunkPath(mirrorPath, digest));

        var report = await repository.ScrubAsync(autoRepairFromMirror: true);

        var issue = Assert.Single(report.Issues);
        Assert.Equal(RepositoryScrubIssueKind.MirrorDrift, issue.Kind);
        Assert.Equal(RepositoryRepairAction.RepairedMirrorFromPrimary, issue.RepairAction);
        Assert.True(File.Exists(ChunkPath(mirrorPath, digest)));
    }

    [Fact]
    public async Task Missing_chunk_without_healthy_copy_is_reported_as_unresolved()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var commit = await repository.CommitAsync(NewRequest("local only content"));
        var digest = Assert.Single(commit.Manifest.Chunks).Digest;
        File.Delete(ChunkPath(workspace.RepositoryPath, digest));

        var report = await repository.ScrubAsync(autoRepairFromMirror: true);

        var issue = Assert.Single(report.Issues);
        Assert.Equal(RepositoryHealthState.Critical, report.HealthState);
        Assert.Equal(RepositoryScrubIssueSeverity.Critical, issue.Severity);
        Assert.Equal(RepositoryRepairAction.Unresolved, issue.RepairAction);
    }

    [Fact]
    public async Task Retention_pruned_chunk_absence_is_not_reported_by_scrub()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        await repository.CommitAsync(NewRequest("old unique content", now.AddDays(-10)));
        await repository.CommitAsync(NewRequest("new unique content", now));

        await repository.ApplyRetentionAsync(
            new RetentionPolicy(true, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, MinimumVersionsPerFile: 1),
            now);
        var report = await repository.ScrubAsync(autoRepairFromMirror: true);

        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task Restore_rehearsal_restores_to_temp_verifies_and_cleans_output_without_restore_hint()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        await repository.CommitAsync(NewRequest("rehearsal content"));
        var tempRoot = Path.Combine(workspace.RootPath, "rehearsal-temp");

        var report = await repository.RunRestoreRehearsalAsync(tempRoot, maxVersions: 1);

        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        var result = Assert.Single(report.Results);
        Assert.True(result.Success);
        Assert.Equal(1, report.RehearsedVersionCount);
        Assert.False(Directory.Exists(tempRoot) && Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories).Any());
        Assert.False(Directory.Exists(Path.Combine(workspace.RepositoryPath, "lineage", "restore-hints")));
    }

    [Fact]
    public async Task Restore_rehearsal_reports_failure_and_cleans_temp_when_chunk_is_missing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = CreateRepository(workspace.RepositoryPath);
        var commit = await repository.CommitAsync(NewRequest("rehearsal content"));
        var digest = Assert.Single(commit.Manifest.Chunks).Digest;
        File.Delete(ChunkPath(workspace.RepositoryPath, digest));
        var tempRoot = Path.Combine(workspace.RootPath, "rehearsal-temp");

        var report = await repository.RunRestoreRehearsalAsync(tempRoot, maxVersions: 1);

        Assert.Equal(RepositoryHealthState.Critical, report.HealthState);
        var result = Assert.Single(report.Results);
        Assert.False(result.Success);
        Assert.Equal(1, report.FailedVersionCount);
        Assert.False(Directory.Exists(tempRoot) && Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories).Any());
    }

    private static FileSystemChunkRepository CreateRepository(string path, string? mirrorPath = null)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            mirrorPath);
    }

    private static FileCommitRequest NewRequest(string payload, DateTimeOffset? capturedAtUtc = null)
    {
        return new FileCommitRequest(
            WatchedFolderId: "docs",
            SourcePath: @"D:\Work\Docs\brief.docx",
            CapturedAtUtc: capturedAtUtc ?? DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.CrashConsistent,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 128,
            Content: new MemoryStream(Encoding.UTF8.GetBytes(payload)));
    }

    private static async Task AssertRestoresAsync(FileSystemChunkRepository repository, string versionId, TemporaryWorkspace workspace)
    {
        var restorePath = Path.Combine(workspace.RootPath, $"{versionId}.restore");
        await repository.RestoreAsync(versionId, restorePath);
        Assert.True(new FileInfo(restorePath).Length > 0);
    }

    private static string ChunkPath(string root, string digest)
    {
        return Path.Combine(root, "chunks", digest[..2], $"{digest}.chunk");
    }
}
