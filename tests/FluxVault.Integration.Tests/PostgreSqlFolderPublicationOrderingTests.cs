using System.Text;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage;
using FluxVault.Integration.Tests.Fixtures;

namespace FluxVault.Integration.Tests;

[Trait("Category", "RequiresPostgreSql")]
public sealed class PostgreSqlFolderPublicationOrderingTests
{
    [Theory]
    [InlineData(-10000)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Folder_history_and_current_entry_follow_publication(long captureOffsetTicks)
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var repo = PostgreSqlRepositoryIntegrityTests.Create(fixture);
        var root = Path.Combine(fixture.Files.Root, "working");
        var captured = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await Capture(fixture, repo, root, "nested/project.txt", "first", captured);
        var previous = await LatestFolder(repo, root);
        var sibling = await Capture(fixture, repo, root, "sample.txt", "sibling", captured.AddTicks(captureOffsetTicks));
        Assert.Equal(captured.AddTicks(captureOffsetTicks), sibling.Manifest.CapturedAtUtc);
        var latest = await LatestFolder(repo, root);
        Assert.True(latest.CapturedAtUtc >= previous.CapturedAtUtc.AddTicks(10));
        await AssertCurrent(fixture, latest);
        await VerifyTree(repo, latest, Path.Combine(fixture.Files.Root, "first-restore"), "first");

        await Capture(fixture, repo, root, "nested/project.txt", "updated", captured.AddDays(-1));
        var updated = await LatestFolder(repo, root);
        Assert.True(updated.CapturedAtUtc >= latest.CapturedAtUtc.AddTicks(10));
        await AssertCurrent(fixture, updated);
        await VerifyTree(repo, updated, Path.Combine(fixture.Files.Root, "updated-restore"), "updated");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Folder_deletion_updates_current_entry_and_preserves_siblings(bool deleteRoot, bool sameTime)
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var repo = PostgreSqlRepositoryIntegrityTests.Create(fixture);
        var root = Path.Combine(fixture.Files.Root, "working");
        var captured = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await Capture(fixture, repo, root, "nested/project.txt", "nested", captured);
        var sibling = await Capture(fixture, repo, root, "sample.txt", "sibling", captured);
        var deletedPath = deleteRoot ? root : Path.Combine(root, "nested");
        var previous = await LatestFolder(repo, deletedPath);
        await repo.RecordDeletionAsync(new("fixture", root, deletedPath, true,
            sameTime ? previous.CapturedAtUtc : previous.CapturedAtUtc.AddDays(-1)));
        var deleted = await LatestFolder(repo, deletedPath);
        Assert.True(deleted.IsDeleted);
        Assert.True(deleted.CapturedAtUtc >= previous.CapturedAtUtc.AddTicks(10));
        await AssertCurrent(fixture, deleted);
        Assert.Equal(1, await fixture.ScalarAsync($"SELECT count(*) FROM fluxvault.current_entries WHERE version_id = '{deleted.VersionId}' AND is_deleted"));
        var destination = Path.Combine(fixture.Files.Root, "restore");
        if (deleteRoot) await VerifyTree(repo, deleted, destination, "nested");
        else
        {
            var parent = await LatestFolder(repo, root);
            await AssertCurrent(fixture, parent);
            var result = await repo.RestoreAsync(parent.VersionId, destination);
            Assert.Equal(1, result.RestoredFileCount);
            Assert.Equal("sibling", await File.ReadAllTextAsync(Path.Combine(destination, "sample.txt")));
            Assert.False(Directory.Exists(Path.Combine(destination, "nested")));
        }
        var fileDestination = Path.Combine(fixture.Files.Root, "sibling.txt");
        await repo.RestoreAsync(sibling.Manifest.VersionId, fileDestination);
        Assert.Equal("sibling", await File.ReadAllTextAsync(fileDestination));
    }

    private static Task<FileCommitResult> Capture(DisposablePostgreSqlFixture fixture, FileSystemChunkRepository repo,
        string root, string relative, string content, DateTimeOffset captured) => repo.CommitAsync(
        PostgreSqlRepositoryIntegrityTests.Request(fixture, Encoding.UTF8.GetBytes(content), false) with
        { SourcePath = Path.Combine(root, relative), WatchedFolderPath = root, CapturedAtUtc = captured });

    private static async Task<FileVersionManifest> LatestFolder(FileSystemChunkRepository repo, string path)
    {
        var version = (await repo.ListVersionsAsync()).First(version => version.EntryKind == RepositoryEntryKind.Folder &&
            string.Equals(version.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        return (await repo.InspectAsync(version.VersionId)).Manifest;
    }

    private static async Task AssertCurrent(DisposablePostgreSqlFixture fixture, FileVersionManifest folder) =>
        Assert.Equal(1, await fixture.ScalarAsync($"SELECT count(*) FROM fluxvault.current_entries WHERE version_id = '{folder.VersionId}'"));

    private static async Task VerifyTree(FileSystemChunkRepository repo, FileVersionManifest folder, string destination, string nested)
    {
        var result = await repo.RestoreAsync(folder.VersionId, destination);
        Assert.Equal(2, result.RestoredFileCount);
        Assert.Equal("sibling", await File.ReadAllTextAsync(Path.Combine(destination, "sample.txt")));
        Assert.Equal(nested, await File.ReadAllTextAsync(Path.Combine(destination, "nested", "project.txt")));
    }
}
