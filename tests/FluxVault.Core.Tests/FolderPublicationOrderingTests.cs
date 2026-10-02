using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;

namespace FluxVault.Core.Tests;

public sealed class FolderPublicationOrderingTests
{
    [Theory]
    [InlineData(-10000)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Folder_history_follows_publication_and_preserves_siblings(long captureOffsetTicks)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var root = Path.Combine(workspace.RootPath, "working");
        var captured = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await Capture(repo, root, "nested/project.txt", "first", captured);
        var previous = await LatestFolder(repo, root);
        var sibling = await Capture(repo, root, "sample.txt", "sibling", captured.AddTicks(captureOffsetTicks));
        Assert.Equal(captured.AddTicks(captureOffsetTicks), sibling.Manifest.CapturedAtUtc);
        var latest = await LatestFolder(repo, root);
        Assert.True(latest.CapturedAtUtc >= previous.CapturedAtUtc.AddTicks(10));
        await VerifyTree(repo, latest, Path.Combine(workspace.RootPath, "first-restore"), "first");

        await Capture(repo, root, "nested/project.txt", "updated", captured.AddDays(-1));
        var updated = await LatestFolder(repo, root);
        Assert.True(updated.CapturedAtUtc >= latest.CapturedAtUtc.AddTicks(10));
        await VerifyTree(repo, updated, Path.Combine(workspace.RootPath, "updated-restore"), "updated");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Folder_deletion_advances_its_own_history_and_preserves_siblings(bool deleteRoot, bool sameTime)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var root = Path.Combine(workspace.RootPath, "working");
        var captured = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await Capture(repo, root, "nested/project.txt", "nested", captured);
        var sibling = await Capture(repo, root, "sample.txt", "sibling", captured);
        var deletedPath = deleteRoot ? root : Path.Combine(root, "nested");
        var previous = await LatestFolder(repo, deletedPath);
        await repo.RecordDeletionAsync(new("integrity", root, deletedPath, true,
            sameTime ? previous.CapturedAtUtc : previous.CapturedAtUtc.AddDays(-1)));
        var deleted = await LatestFolder(repo, deletedPath);
        Assert.True(deleted.IsDeleted);
        Assert.True(deleted.CapturedAtUtc >= previous.CapturedAtUtc.AddTicks(10));
        var destination = Path.Combine(workspace.RootPath, "restore");
        if (deleteRoot)
        {
            await VerifyTree(repo, deleted, destination, "nested");
        }
        else
        {
            var parent = await LatestFolder(repo, root);
            var result = await repo.RestoreAsync(parent.VersionId, destination);
            Assert.Equal(1, result.RestoredFileCount);
            Assert.Equal("sibling", await File.ReadAllTextAsync(Path.Combine(destination, "sample.txt")));
            Assert.False(Directory.Exists(Path.Combine(destination, "nested")));
        }
        var fileDestination = Path.Combine(workspace.RootPath, "sibling.txt");
        await repo.RestoreAsync(sibling.Manifest.VersionId, fileDestination);
        Assert.Equal("sibling", await File.ReadAllTextAsync(fileDestination));
    }

    [Fact]
    public async Task Unrepresentable_folder_publication_time_fails_without_acknowledging_new_versions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var root = Path.Combine(workspace.RootPath, "working");
        await Capture(repo, root, "first.txt", "first", DateTimeOffset.MaxValue);
        var before = (await repo.ListVersionsAsync()).Select(version => version.VersionId).Order().ToArray();
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() =>
            Capture(repo, root, "second.txt", "second", DateTimeOffset.UtcNow));
        Assert.Equal(RepositoryIntegrityFailure.InvalidManifest, error.Code);
        Assert.Equal(before, (await repo.ListVersionsAsync()).Select(version => version.VersionId).Order().ToArray());
    }

    private static Task<FileCommitResult> Capture(FileSystemChunkRepository repo, string root, string relative,
        string content, DateTimeOffset captured) => repo.CommitAsync(RepositoryIntegrityTests.Request(content) with
        { SourcePath = Path.Combine(root, relative), WatchedFolderPath = root, CapturedAtUtc = captured });

    private static async Task<FileVersionManifest> LatestFolder(FileSystemChunkRepository repo, string path)
    {
        var version = (await repo.ListVersionsAsync()).First(version => version.EntryKind == RepositoryEntryKind.Folder &&
            string.Equals(version.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        return (await repo.InspectAsync(version.VersionId)).Manifest;
    }

    private static async Task VerifyTree(FileSystemChunkRepository repo, FileVersionManifest folder, string destination, string nested)
    {
        var result = await repo.RestoreAsync(folder.VersionId, destination);
        Assert.Equal(2, result.RestoredFileCount);
        Assert.Equal("sibling", await File.ReadAllTextAsync(Path.Combine(destination, "sample.txt")));
        Assert.Equal(nested, await File.ReadAllTextAsync(Path.Combine(destination, "nested", "project.txt")));
    }
}
