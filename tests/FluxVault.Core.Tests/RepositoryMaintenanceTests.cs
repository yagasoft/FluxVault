using System.Text;
using FluxVault.Abstractions.Configuration;
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
    public async Task Scrub_ignores_missing_chunks_on_non_target_capacity_balanced_nodes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var firstMirror = Path.Combine(workspace.RootPath, "first-mirror");
        var secondMirror = Path.Combine(workspace.RootPath, "second-mirror");
        var repository = CreateRepository(
            workspace.RepositoryPath,
            MirrorSet(
                new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.CapacityBalanced),
                firstMirror,
                secondMirror));
        await repository.CommitAsync(NewRequest("capacity-balanced scrub content"));

        var report = await repository.ScrubAsync(autoRepairFromMirror: true);

        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        Assert.Empty(report.Issues);
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

    [Fact]
    public async Task Mirror_repair_preview_reports_mirror_chunk_metadata_and_manifest_drift_without_writing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirrorPath = Path.Combine(workspace.RootPath, "mirror");
        var repository = CreateRepository(workspace.RepositoryPath, MirrorSet(mirrorPath));
        var commit = await repository.CommitAsync(NewRequest("mirrored content"));
        var digest = Assert.Single(commit.Manifest.Chunks).Digest;
        File.Delete(ChunkPath(mirrorPath, digest));
        await File.WriteAllTextAsync(MetadataPath(mirrorPath, digest), "corrupt metadata");
        await File.WriteAllTextAsync(ManifestPath(mirrorPath, commit.Manifest.VersionId), "corrupt manifest");

        var report = await repository.PreviewMirrorRepairAsync();

        var node = Assert.Single(report.Nodes);
        Assert.True(report.IsPreview);
        Assert.Equal(RepositoryHealthState.Warning, report.HealthState);
        Assert.Equal("mirror", node.NodeId);
        Assert.Contains(node.Issues, issue => issue.ArtefactKind == MirrorRepairArtefactKind.Chunk);
        Assert.Contains(node.Issues, issue => issue.ArtefactKind == MirrorRepairArtefactKind.Metadata);
        Assert.Contains(node.Issues, issue => issue.ArtefactKind == MirrorRepairArtefactKind.Manifest);
        Assert.All(node.Issues, issue => Assert.Equal(MirrorRepairAction.None, issue.RepairAction));
        Assert.False(File.Exists(ChunkPath(mirrorPath, digest)));
        Assert.Equal("corrupt metadata", await File.ReadAllTextAsync(MetadataPath(mirrorPath, digest)));
        Assert.Equal("corrupt manifest", await File.ReadAllTextAsync(ManifestPath(mirrorPath, commit.Manifest.VersionId)));
        AssertNoTemporaryFiles(workspace.RootPath);
    }

    [Fact]
    public async Task Mirror_repair_all_repairs_primary_and_all_mirror_drift()
    {
        using var workspace = TemporaryWorkspace.Create();
        var firstMirror = Path.Combine(workspace.RootPath, "first-mirror");
        var secondMirror = Path.Combine(workspace.RootPath, "second-mirror");
        var repository = CreateRepository(workspace.RepositoryPath, MirrorSet(firstMirror, secondMirror));
        var commit = await repository.CommitAsync(NewRequest("mirrored content"));
        var digest = Assert.Single(commit.Manifest.Chunks).Digest;
        File.Delete(ChunkPath(workspace.RepositoryPath, digest));
        File.Delete(ChunkPath(secondMirror, digest));

        var report = await repository.RunMirrorRepairAsync();

        Assert.False(report.IsPreview);
        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        Assert.Contains(report.Issues, issue => issue.RepairAction == MirrorRepairAction.RepairedPrimaryFromMirror);
        Assert.Contains(report.Nodes.Single(node => node.NodeId == "second").Issues,
            issue => issue.RepairAction == MirrorRepairAction.RepairedMirrorFromPrimary);
        Assert.True(File.Exists(ChunkPath(workspace.RepositoryPath, digest)));
        Assert.True(File.Exists(ChunkPath(secondMirror, digest)));
        AssertNoTemporaryFiles(workspace.RootPath);
    }

    [Fact]
    public async Task Selected_mirror_repair_repairs_only_selected_mirror()
    {
        using var workspace = TemporaryWorkspace.Create();
        var firstMirror = Path.Combine(workspace.RootPath, "first-mirror");
        var secondMirror = Path.Combine(workspace.RootPath, "second-mirror");
        var repository = CreateRepository(workspace.RepositoryPath, MirrorSet(firstMirror, secondMirror));
        var commit = await repository.CommitAsync(NewRequest("mirrored content"));
        var digest = Assert.Single(commit.Manifest.Chunks).Digest;
        File.Delete(ChunkPath(firstMirror, digest));
        File.Delete(ChunkPath(secondMirror, digest));

        var report = await repository.RunMirrorRepairAsync("second");

        Assert.Equal("second", report.RequestedMirrorNodeId);
        Assert.True(File.Exists(ChunkPath(secondMirror, digest)));
        Assert.False(File.Exists(ChunkPath(firstMirror, digest)));
        Assert.Equal(RepositoryHealthState.Warning, report.Nodes.Single(node => node.NodeId == "mirror").HealthState);
        Assert.Equal(RepositoryHealthState.Healthy, report.Nodes.Single(node => node.NodeId == "second").HealthState);
        AssertNoTemporaryFiles(workspace.RootPath);
    }

    [Fact]
    public async Task Selected_mirror_repair_does_not_repair_corrupt_primary()
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirrorPath = Path.Combine(workspace.RootPath, "mirror");
        var repository = CreateRepository(workspace.RepositoryPath, MirrorSet(mirrorPath));
        var commit = await repository.CommitAsync(NewRequest("mirrored content"));
        var digest = Assert.Single(commit.Manifest.Chunks).Digest;
        File.Delete(ChunkPath(workspace.RepositoryPath, digest));

        var report = await repository.RunMirrorRepairAsync("mirror");

        Assert.False(File.Exists(ChunkPath(workspace.RepositoryPath, digest)));
        var issue = Assert.Single(report.Issues);
        Assert.Equal(MirrorRepairAction.Unresolved, issue.RepairAction);
        Assert.Contains("repair all", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Offline_mirror_repair_reports_unresolved_node_without_failing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var unavailableMirror = Path.Combine(workspace.RootPath, "unavailable-mirror");
        await File.WriteAllTextAsync(unavailableMirror, "this file blocks directory access");
        var repository = CreateRepository(workspace.RepositoryPath, new MirrorSetConfiguration(
        [
            new MirrorNodeConfiguration("offline", "Offline mirror", unavailableMirror, IsEnabled: true)
        ]));
        await repository.CommitAsync(NewRequest("local content"));

        var report = await repository.RunMirrorRepairAsync();

        var node = Assert.Single(report.Nodes);
        Assert.Equal("offline", node.NodeId);
        Assert.Equal(RepositoryHealthState.Warning, report.HealthState);
        Assert.Contains(node.Issues, issue => issue.RepairAction == MirrorRepairAction.Unresolved);
        AssertNoTemporaryFiles(workspace.RootPath);
    }

    [Fact]
    public async Task Mirror_repair_preview_ignores_missing_chunks_on_non_target_capacity_balanced_nodes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var firstMirror = Path.Combine(workspace.RootPath, "first-mirror");
        var secondMirror = Path.Combine(workspace.RootPath, "second-mirror");
        var repository = CreateRepository(
            workspace.RepositoryPath,
            MirrorSet(
                new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.CapacityBalanced),
                firstMirror,
                secondMirror));
        await repository.CommitAsync(NewRequest("capacity-balanced mirror content"));

        var report = await repository.PreviewMirrorRepairAsync();

        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        Assert.All(report.Nodes, node => Assert.Empty(node.Issues));
    }

    [Fact]
    public async Task Mirror_rebalance_preview_reports_missing_required_and_extra_non_target_copies_without_writing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var firstMirror = Path.Combine(workspace.RootPath, "first-mirror");
        var secondMirror = Path.Combine(workspace.RootPath, "second-mirror");
        var fullCopyRepository = CreateRepository(workspace.RepositoryPath, MirrorSet(firstMirror, secondMirror));
        var commit = await fullCopyRepository.CommitAsync(NewRequest("placement preview content"));
        var chunk = Assert.Single(commit.Manifest.Chunks);
        var placementMirrorSet = MirrorSet(
            new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.CapacityBalanced),
            firstMirror,
            secondMirror);
        var targetNodeId = new MirrorPlacementPlanner()
            .SelectChunkTargets(chunk.Digest, chunk.StoredLength, placementMirrorSet, new Dictionary<string, long>())
            .TargetNodeIds
            .Single();
        var targetPath = targetNodeId == "mirror" ? firstMirror : secondMirror;
        var extraPath = targetNodeId == "mirror" ? secondMirror : firstMirror;
        File.Delete(ChunkPath(targetPath, chunk.Digest));
        File.Delete(MetadataPath(targetPath, chunk.Digest));
        var placementRepository = CreateRepository(workspace.RepositoryPath, placementMirrorSet);

        var report = await placementRepository.PreviewMirrorRebalanceAsync();

        Assert.Equal(RepositoryHealthState.Warning, report.HealthState);
        Assert.Contains(report.Actions, action =>
            action.Action == MirrorRebalanceActionKind.CopyToMirror
            && action.MirrorNodeId == targetNodeId
            && action.ChunkDigest == chunk.Digest);
        Assert.Contains(report.Actions, action =>
            action.Action == MirrorRebalanceActionKind.DeleteFromMirror
            && action.MirrorNodeId != targetNodeId
            && action.ChunkDigest == chunk.Digest);
        Assert.True(report.EstimatedCopyBytes >= chunk.StoredLength);
        Assert.True(report.EstimatedDeleteBytes >= chunk.StoredLength);
        Assert.False(File.Exists(ChunkPath(targetPath, chunk.Digest)));
        Assert.True(File.Exists(ChunkPath(extraPath, chunk.Digest)));
        AssertNoTemporaryFiles(workspace.RootPath);
    }

    [Fact]
    public async Task Mirror_rebalance_preview_reports_unavailable_node_without_writing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var onlineMirror = Path.Combine(workspace.RootPath, "online-mirror");
        var offlineMirror = Path.Combine(workspace.RootPath, "offline-mirror");
        var repository = CreateRepository(
            workspace.RepositoryPath,
            new MirrorSetConfiguration(
            [
                new MirrorNodeConfiguration("online", "Online mirror", onlineMirror, IsEnabled: true),
                new MirrorNodeConfiguration("offline", "Offline mirror", offlineMirror, IsEnabled: true)
            ]));
        await repository.CommitAsync(NewRequest("offline placement preview content"));

        Directory.Delete(offlineMirror, recursive: true);

        var report = await repository.PreviewMirrorRebalanceAsync();

        Assert.Equal(RepositoryHealthState.Warning, report.HealthState);
        var action = Assert.Single(report.Actions, action => action.MirrorNodeId == "offline");
        Assert.Equal(MirrorRebalanceActionKind.Unresolved, action.Action);
        Assert.Contains("unavailable", action.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(offlineMirror));
        AssertNoTemporaryFiles(workspace.RootPath);
    }

    [Fact]
    public async Task Mirror_rebalance_run_copies_missing_required_copies_and_deletes_extra_non_targets()
    {
        using var workspace = TemporaryWorkspace.Create();
        var firstMirror = Path.Combine(workspace.RootPath, "first-mirror");
        var secondMirror = Path.Combine(workspace.RootPath, "second-mirror");
        var fullCopyRepository = CreateRepository(workspace.RepositoryPath, MirrorSet(firstMirror, secondMirror));
        var commit = await fullCopyRepository.CommitAsync(NewRequest("rebalance execution content"));
        var chunk = Assert.Single(commit.Manifest.Chunks);
        var placementMirrorSet = MirrorSet(
            new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.CapacityBalanced),
            firstMirror,
            secondMirror);
        var targetNodeId = new MirrorPlacementPlanner()
            .SelectChunkTargets(chunk.Digest, chunk.StoredLength, placementMirrorSet, new Dictionary<string, long>())
            .TargetNodeIds
            .Single();
        var targetPath = targetNodeId == "mirror" ? firstMirror : secondMirror;
        var extraPath = targetNodeId == "mirror" ? secondMirror : firstMirror;
        File.Delete(ChunkPath(targetPath, chunk.Digest));
        File.Delete(MetadataPath(targetPath, chunk.Digest));
        var placementRepository = CreateRepository(workspace.RepositoryPath, placementMirrorSet);

        var report = await placementRepository.RunMirrorRebalanceAsync();

        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        Assert.Empty(report.Actions);
        Assert.True(File.Exists(ChunkPath(targetPath, chunk.Digest)));
        Assert.True(File.Exists(MetadataPath(targetPath, chunk.Digest)));
        Assert.False(File.Exists(ChunkPath(extraPath, chunk.Digest)));
        Assert.False(File.Exists(MetadataPath(extraPath, chunk.Digest)));
        AssertNoTemporaryFiles(workspace.RootPath);
    }

    [Fact]
    public async Task Mirror_rebalance_run_preserves_extra_non_targets_when_required_target_is_unavailable()
    {
        using var workspace = TemporaryWorkspace.Create();
        var firstMirror = Path.Combine(workspace.RootPath, "first-mirror");
        var secondMirror = Path.Combine(workspace.RootPath, "second-mirror");
        var fullCopyRepository = CreateRepository(workspace.RepositoryPath, MirrorSet(firstMirror, secondMirror));
        var commit = await fullCopyRepository.CommitAsync(NewRequest("rebalance unavailable content"));
        var chunk = Assert.Single(commit.Manifest.Chunks);
        var placementMirrorSet = MirrorSet(
            new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.CapacityBalanced),
            firstMirror,
            secondMirror);
        var targetNodeId = new MirrorPlacementPlanner()
            .SelectChunkTargets(chunk.Digest, chunk.StoredLength, placementMirrorSet, new Dictionary<string, long>())
            .TargetNodeIds
            .Single();
        var targetPath = targetNodeId == "mirror" ? firstMirror : secondMirror;
        var extraPath = targetNodeId == "mirror" ? secondMirror : firstMirror;
        Directory.Delete(targetPath, recursive: true);
        var placementRepository = CreateRepository(workspace.RepositoryPath, placementMirrorSet);

        var report = await placementRepository.RunMirrorRebalanceAsync();

        Assert.Equal(RepositoryHealthState.Warning, report.HealthState);
        Assert.Contains(report.Actions, action =>
            action.MirrorNodeId == targetNodeId
            && action.Action == MirrorRebalanceActionKind.Unresolved);
        Assert.True(File.Exists(ChunkPath(extraPath, chunk.Digest)));
        Assert.True(File.Exists(MetadataPath(extraPath, chunk.Digest)));
        Assert.False(Directory.Exists(targetPath));
        AssertNoTemporaryFiles(workspace.RootPath);
    }

    [Fact]
    public async Task Mirror_rebalance_run_reports_missing_primary_and_preserves_mirror_copies()
    {
        using var workspace = TemporaryWorkspace.Create();
        var firstMirror = Path.Combine(workspace.RootPath, "first-mirror");
        var secondMirror = Path.Combine(workspace.RootPath, "second-mirror");
        var fullCopyRepository = CreateRepository(workspace.RepositoryPath, MirrorSet(firstMirror, secondMirror));
        var commit = await fullCopyRepository.CommitAsync(NewRequest("rebalance missing primary content"));
        var chunk = Assert.Single(commit.Manifest.Chunks);
        var placementMirrorSet = MirrorSet(
            new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.CapacityBalanced),
            firstMirror,
            secondMirror);
        var targetNodeId = new MirrorPlacementPlanner()
            .SelectChunkTargets(chunk.Digest, chunk.StoredLength, placementMirrorSet, new Dictionary<string, long>())
            .TargetNodeIds
            .Single();
        var targetPath = targetNodeId == "mirror" ? firstMirror : secondMirror;
        var extraPath = targetNodeId == "mirror" ? secondMirror : firstMirror;
        File.Delete(ChunkPath(targetPath, chunk.Digest));
        File.Delete(MetadataPath(targetPath, chunk.Digest));
        File.Delete(ChunkPath(workspace.RepositoryPath, chunk.Digest));
        var placementRepository = CreateRepository(workspace.RepositoryPath, placementMirrorSet);

        var report = await placementRepository.RunMirrorRebalanceAsync();

        Assert.Equal(RepositoryHealthState.Warning, report.HealthState);
        Assert.Contains(report.Actions, action =>
            action.MirrorNodeId == targetNodeId
            && action.Action == MirrorRebalanceActionKind.Unresolved
            && action.Message.Contains("primary", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(ChunkPath(targetPath, chunk.Digest)));
        Assert.True(File.Exists(ChunkPath(extraPath, chunk.Digest)));
        Assert.True(File.Exists(MetadataPath(extraPath, chunk.Digest)));
        AssertNoTemporaryFiles(workspace.RootPath);
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

    private static FileSystemChunkRepository CreateRepository(string path, MirrorSetConfiguration mirrorSet)
    {
        return new FileSystemChunkRepository(
            path,
            new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            mirrorSet);
    }

    private static MirrorSetConfiguration MirrorSet(params string[] paths)
    {
        return new MirrorSetConfiguration(paths
            .Select((path, index) => new MirrorNodeConfiguration(
                Id: index == 0 ? "mirror" : index == 1 ? "second" : $"mirror-{index + 1}",
                Label: index == 0 ? "Mirror" : $"Mirror {index + 1}",
                Path: path,
                IsEnabled: true))
            .ToArray());
    }

    private static MirrorSetConfiguration MirrorSet(
        MirrorPlacementPolicyConfiguration placementPolicy,
        params string[] paths)
    {
        return new MirrorSetConfiguration(paths
            .Select((path, index) => new MirrorNodeConfiguration(
                Id: index == 0 ? "mirror" : index == 1 ? "second" : $"mirror-{index + 1}",
                Label: index == 0 ? "Mirror" : $"Mirror {index + 1}",
                Path: path,
                IsEnabled: true))
            .ToArray(),
            placementPolicy);
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

    private static string MetadataPath(string root, string digest)
    {
        return Path.Combine(root, "chunks", digest[..2], $"{digest}.json");
    }

    private static string ManifestPath(string root, string versionId)
    {
        return Path.Combine(root, "manifests", $"{versionId}.json");
    }

    private static void AssertNoTemporaryFiles(string root)
    {
        Assert.False(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
    }
}
