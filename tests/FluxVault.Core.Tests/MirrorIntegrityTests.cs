using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;

namespace FluxVault.Core.Tests;

public sealed class MirrorIntegrityTests
{
    [Fact]
    public async Task Drain_repairs_and_verifies_every_required_survivor_before_deleting_the_donor()
    {
        using var workspace = TemporaryWorkspace.Create();
        var set = Set(workspace);
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath, set);
        var version = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected bytes"));
        var digest = version.Manifest.Chunks[0].Digest;
        await Corrupt(workspace.RepositoryPath, digest);
        await Corrupt(set.Nodes[1].Path, digest);
        var report = await repo.RunMirrorDrainAsync("first");
        Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
        Assert.False(File.Exists(RepositoryIntegrityTests.ChunkPath(set.Nodes[0].Path, digest)));
        foreach (var root in new[] { workspace.RepositoryPath, set.Nodes[1].Path })
            Assert.Equal("protected bytes", await File.ReadAllTextAsync(RepositoryIntegrityTests.ChunkPath(root, digest)));
        var output = Path.Combine(workspace.RootPath, "restored.txt");
        await repo.RestoreAsync(version.Manifest.VersionId, output);
        Assert.Equal("protected bytes", await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData("headroom")]
    [InlineData("minimum")]
    [InlineData("last-mirror")]
    public async Task Insufficient_survivors_preserve_the_departing_copy(string reason)
    {
        using var workspace = TemporaryWorkspace.Create();
        var set = Set(workspace);
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath, set);
        var version = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected bytes"));
        var digest = version.Manifest.Chunks[0].Digest;
        await Corrupt(workspace.RepositoryPath, digest);
        await Corrupt(set.Nodes[1].Path, digest);
        var changed = reason switch
        {
            "last-mirror" => set with { Nodes = [set.Nodes[0]] },
            "minimum" => set with { PlacementPolicy = new(MirrorPlacementProfile.Redundant, MinimumMirrorCopies: 2) },
            _ => set with { Nodes = [set.Nodes[0], set.Nodes[1] with { CapacityBudgetBytes = Used(set.Nodes[1].Path) }] }
        };
        var report = await RepositoryIntegrityTests.Create(workspace.RepositoryPath, changed).RunMirrorDrainAsync("first");
        Assert.NotEqual(RepositoryHealthState.Healthy, report.HealthState);
        Assert.Contains(report.Actions, action => action.Action == MirrorRebalanceActionKind.Unresolved);
        Assert.Equal("protected bytes", await File.ReadAllTextAsync(RepositoryIntegrityTests.ChunkPath(set.Nodes[0].Path, digest)));
    }

    [Fact]
    public async Task A_locked_target_stops_drain_and_reports_unresolved()
    {
        using var workspace = TemporaryWorkspace.Create();
        var set = Set(workspace);
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath, set);
        var version = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected bytes"));
        var digest = version.Manifest.Chunks[0].Digest;
        using var locked = new FileStream(RepositoryIntegrityTests.ChunkPath(set.Nodes[1].Path, digest), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var report = await repo.RunMirrorDrainAsync("first");
        Assert.Contains(report.Actions, action => action.Action == MirrorRebalanceActionKind.Unresolved);
        Assert.Equal("protected bytes", await File.ReadAllTextAsync(RepositoryIntegrityTests.ChunkPath(set.Nodes[0].Path, digest)));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_interrupted_deletion_retains_verified_survivors_and_can_be_retried()
    {
        using var workspace = TemporaryWorkspace.Create();
        var set = Set(workspace);
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath, set);
        var version = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected bytes"));
        var digest = version.Manifest.Chunks[0].Digest;
        var interrupted = new FileSystemChunkRepository(workspace.RepositoryPath, new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(), new ZstdChunkCodec(), set, null, null, new((point, _) =>
            { if (point == RepositoryFaultPoint.DepartingPayloadDeleted) throw new IOException("injected interruption"); }));
        var report = await interrupted.RunMirrorDrainAsync("first");
        Assert.NotEqual(RepositoryHealthState.Healthy, report.HealthState);
        foreach (var root in new[] { workspace.RepositoryPath, set.Nodes[1].Path })
            Assert.Equal("protected bytes", await File.ReadAllTextAsync(RepositoryIntegrityTests.ChunkPath(root, digest)));
        var retried = await repo.RunMirrorDrainAsync("first");
        Assert.Equal(RepositoryHealthState.Healthy, retried.HealthState);
        Assert.False(File.Exists(RepositoryIntegrityTests.SidecarPath(set.Nodes[0].Path, digest)));
    }

    [Fact]
    public async Task Optional_busy_mirror_does_not_fail_primary_capture_and_is_reported()
    {
        using var workspace = TemporaryWorkspace.Create();
        var set = Set(workspace);
        Directory.CreateDirectory(set.Nodes[0].Path);
        using var locked = new FileStream(Path.Combine(set.Nodes[0].Path, StorageOwnership.LockName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var result = await RepositoryIntegrityTests.Create(workspace.RepositoryPath, set).CommitAsync(RepositoryIntegrityTests.Request("protected bytes"));
        Assert.NotEmpty(result.MirrorWarnings!);
        Assert.Single(await RepositoryIntegrityTests.Create(workspace.RepositoryPath).ListVersionsAsync());
    }

    private static MirrorSetConfiguration Set(TemporaryWorkspace workspace) => new(
        [new("first", "First", Path.Combine(workspace.RootPath, "first"), true),
         new("second", "Second", Path.Combine(workspace.RootPath, "second"), true)]);
    private static Task Corrupt(string root, string digest) => File.WriteAllTextAsync(RepositoryIntegrityTests.ChunkPath(root, digest), "corrupted bytes");
    private static long Used(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
}
