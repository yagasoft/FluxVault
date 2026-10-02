using System.Text.Json;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class FolderRestoreIntegrityTests
{
    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/file")]
    [InlineData("C:drive")]
    [InlineData("CON.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("NUL")]
    [InlineData("stream:secret")]
    public async Task Unsafe_child_names_publish_no_tree(string name)
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, _) = await Seed(workspace);
        await ReplaceManifest(workspace, folder with { FolderEntries = [folder.FolderEntries![0] with { Name = name }] });
        var destination = Path.Combine(workspace.RootPath, "restored");
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(folder.VersionId, destination));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "escape")));
    }

    [Fact]
    public async Task Existing_destination_is_never_merged()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, _) = await Seed(workspace);
        var destination = Path.Combine(workspace.RootPath, "restored");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "unrelated.txt"), "keep");
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(folder.VersionId, destination));
        Assert.Equal(["unrelated.txt"], Directory.EnumerateFiles(destination).Select(path => Path.GetFileName(path)!).ToArray());
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "unrelated.txt")));
    }

    [Fact]
    public async Task Late_corrupt_child_publishes_no_tree()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, last) = await Seed(workspace, twoFiles: true);
        var digest = last.Chunks[0].Digest;
        var bytes = await File.ReadAllBytesAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, digest));
        bytes[0] ^= 0xff;
        await File.WriteAllBytesAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, digest), bytes);
        var destination = Path.Combine(workspace.RootPath, "restored");
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(folder.VersionId, destination));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(workspace.RootPath, "*.tmp"));
    }

    internal static async Task<(FileSystemChunkRepository Repository, FileVersionManifest Folder, FileVersionManifest File)> Seed(
        TemporaryWorkspace workspace, bool twoFiles = false)
    {
        var sourceRoot = Path.Combine(workspace.RootPath, "source");
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var first = await repo.CommitAsync(RepositoryIntegrityTests.Request("first child") with
        { SourcePath = Path.Combine(sourceRoot, "a.txt"), WatchedFolderPath = sourceRoot });
        var last = twoFiles ? await repo.CommitAsync(RepositoryIntegrityTests.Request("last child") with
        { SourcePath = Path.Combine(sourceRoot, "z.txt"), WatchedFolderPath = sourceRoot }) : first;
        var summary = (await repo.ListVersionsAsync()).First(version => version.EntryKind == RepositoryEntryKind.Folder);
        return (repo, (await repo.InspectAsync(summary.VersionId)).Manifest, last.Manifest);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("tombstone-cycle")]
    [InlineData("missing")]
    [InlineData("case-collision")]
    [InlineData("length")]
    [InlineData("kind")]
    [InlineData("bad-id")]
    public async Task Invalid_graphs_are_rejected_before_staging(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, _) = await Seed(workspace);
        var entry = folder.FolderEntries![0];
        var changed = scenario switch
        {
            "cycle" => folder with { FolderEntries = [entry with { EntryKind = RepositoryEntryKind.Folder, VersionId = folder.VersionId }] },
            "tombstone-cycle" => folder with { IsDeleted = true, DeletedFromVersionId = folder.VersionId },
            "missing" => folder with { FolderEntries = [entry with { VersionId = new string('f', 32) }] },
            "case-collision" => folder with { LogicalLength = folder.LogicalLength * 2, FolderEntries = [entry, entry with { Name = entry.Name.ToUpperInvariant() }] },
            "length" => folder with { FolderEntries = [entry with { LogicalLength = entry.LogicalLength + 1 }] },
            "kind" => folder with { FolderEntries = [entry with { EntryKind = RepositoryEntryKind.Folder }] },
            _ => folder with { FolderEntries = [entry with { VersionId = "../../outside" }] }
        };
        await ReplaceManifest(workspace, changed);
        var destination = Path.Combine(workspace.RootPath, "restored");
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(folder.VersionId, destination));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(workspace.RootPath, "*.tmp"));
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("nodes")]
    [InlineData("entries")]
    [InlineData("chunks")]
    [InlineData("metadata")]
    [InlineData("manifest")]
    public async Task Limits_apply_to_every_expansion_of_a_shared_child(string limit)
    {
        using var workspace = TemporaryWorkspace.Create();
        var (_, folder, _) = await Seed(workspace);
        var entry = folder.FolderEntries![0];
        var repeated = folder with { LogicalLength = folder.LogicalLength * 2, FolderEntries = [entry, entry with { Name = "second.txt" }] };
        await ReplaceManifest(workspace, repeated);
        var limits = limit switch
        {
            "depth" => new RepositoryIntegrityLimits(MaxGraphDepth: 1),
            "nodes" => new RepositoryIntegrityLimits(MaxGraphNodes: 1),
            "entries" => new RepositoryIntegrityLimits(MaxExpandedEntries: 2),
            "chunks" => new RepositoryIntegrityLimits(MaxExpandedChunkReferences: 1),
            "metadata" => new RepositoryIntegrityLimits(MaxRestoreMetadataBytes: 1),
            _ => new RepositoryIntegrityLimits(MaxManifestBytes: 32)
        };
        var repo = RepositoryPublicationTests.Create(workspace.RepositoryPath, new((_, _) => { }), limits);
        var destination = Path.Combine(workspace.RootPath, "restored");
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.RestoreAsync(folder.VersionId, destination));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Shared_children_restore_to_both_paths_with_verified_counts()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, _) = await Seed(workspace);
        var entry = folder.FolderEntries![0];
        await ReplaceManifest(workspace, folder with { LogicalLength = folder.LogicalLength * 2,
            FolderEntries = [entry, entry with { Name = "second.txt" }] });
        var destination = Path.Combine(workspace.RootPath, "restored");
        var result = await repo.RestoreAsync(folder.VersionId, destination);
        Assert.Equal(2, result.RestoredFileCount);
        Assert.Equal(folder.LogicalLength * 2, result.VerifiedLogicalBytes);
        Assert.Equal("first child", await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")));
        Assert.Equal("first child", await File.ReadAllTextAsync(Path.Combine(destination, "second.txt")));
    }

    [Fact]
    public async Task Folder_created_during_staging_is_preserved()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (_, folder, _) = await Seed(workspace);
        var destination = Path.Combine(workspace.RootPath, "restored");
        var repo = RepositoryPublicationTests.Create(workspace.RepositoryPath, new((point, _) =>
        {
            if (point != RepositoryFaultPoint.BeforeRestorePublication) return;
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "sentinel.txt"), "new work");
        }));
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(folder.VersionId, destination));
        Assert.Equal("new work", await File.ReadAllTextAsync(Path.Combine(destination, "sentinel.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    internal static Task ReplaceManifest(TemporaryWorkspace workspace, FileVersionManifest manifest)
    {
        // These fixtures exercise graph rules, so keep the independent folder-signature check valid.
        if (manifest.EntryKind == RepositoryEntryKind.Folder && !manifest.IsDeleted)
            manifest = manifest with { ContentSignature = RepositoryMetadataStoreHelpers.ComputeFolderContentSignature(manifest.FolderEntries ?? []) };
        return File.WriteAllTextAsync(Path.Combine(workspace.RepositoryPath, "manifests", manifest.VersionId + ".json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
