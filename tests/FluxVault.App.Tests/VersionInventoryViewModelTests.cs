using System.IO;
using FluxVault.Abstractions.Storage;
using FluxVault.App.ViewModels;

namespace FluxVault.App.Tests;

public sealed class VersionInventoryViewModelTests
{
    [Fact]
    public void Groups_repository_versions_by_file_under_selected_folder()
    {
        var folder = Path.GetFullPath(@"D:\Work\Docs");
        var viewModel = new VersionInventoryViewModel(
            folder,
            [
                Version("new", @"D:\Work\Docs\brief.docx", minutesAgo: 0, 20),
                Version("old", @"D:\Work\Docs\brief.docx", minutesAgo: 10, 10),
                Version("outside", @"D:\Work\Other\notes.txt", minutesAgo: 0, 5)
            ],
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);

        var file = Assert.Single(viewModel.Files);
        Assert.Equal(Path.GetFullPath(@"D:\Work\Docs\brief.docx"), file.Path);
        Assert.Equal("brief.docx", file.File);
        Assert.Equal("new", file.LatestVersionId);
        Assert.Contains("Best effort", file.Status);
        Assert.Equal(["new", "old"], file.Versions.Select(version => version.VersionId).ToArray());
    }

    [Fact]
    public async Task Restore_and_open_version_commands_invoke_supplied_callbacks()
    {
        var restored = new List<string>();
        var opened = new List<string>();
        var viewModel = new VersionInventoryViewModel(
            Path.GetFullPath(@"D:\Work"),
            [Version("v1", @"D:\Work\brief.docx", minutesAgo: 0, 20)],
            version =>
            {
                restored.Add(version.VersionId);
                return Task.CompletedTask;
            },
            version =>
            {
                opened.Add(version.VersionId);
                return Task.CompletedTask;
            });
        var versionRow = Assert.Single(Assert.Single(viewModel.Files).Versions);

        await viewModel.RestoreVersionCommand.ExecuteAsync(versionRow);
        await viewModel.OpenVersionPreviewCommand.ExecuteAsync(versionRow);

        Assert.Equal(["v1"], restored);
        Assert.Equal(["v1"], opened);
    }

    [Fact]
    public async Task Folder_version_snapshot_can_navigate_and_preview_child_file()
    {
        var opened = new List<string>();
        var child = Version("file-v1", @"D:\Work\Docs\a.txt", minutesAgo: 0, 20);
        var folder = Version("folder-v1", @"D:\Work\Docs", minutesAgo: 0, 20) with
        {
            EntryKind = RepositoryEntryKind.Folder,
            ChunkCount = 0,
            FolderEntries =
            [
                new FolderVersionEntry(
                    "a.txt",
                    Path.GetFullPath(@"D:\Work\Docs\a.txt"),
                    RepositoryEntryKind.File,
                    "file-v1",
                    IsDeleted: false,
                    LogicalLength: 20,
                    DateTimeOffset.UtcNow)
            ]
        };
        var viewModel = new VersionInventoryViewModel(
            Path.GetFullPath(@"D:\Work"),
            [folder, child],
            _ => Task.CompletedTask,
            version =>
            {
                opened.Add(version.VersionId);
                return Task.CompletedTask;
            });

        viewModel.SelectedVersion = viewModel.Versions.Single(version => version.VersionId == "folder-v1");
        viewModel.SelectedSnapshotEntry = Assert.Single(viewModel.SnapshotEntries);
        await viewModel.OpenSelectedSnapshotEntryCommand.ExecuteAsync(null);

        Assert.Equal(["file-v1"], opened);
    }

    [Fact]
    public async Task Folder_focus_left_list_shows_only_exact_folder_versions_and_snapshot_navigation_updates_focus()
    {
        var opened = new List<string>();
        var childFile = Version("file-v1", @"D:\Work\Docs\a.txt", minutesAgo: 0, 20);
        var grandchildFile = Version("nested-file-v1", @"D:\Work\Docs\Nested\b.txt", minutesAgo: 0, 30);
        var nestedFolder = Version("nested-folder-v1", @"D:\Work\Docs\Nested", minutesAgo: 0, 30) with
        {
            EntryKind = RepositoryEntryKind.Folder,
            ChunkCount = 0,
            FolderEntries =
            [
                new FolderVersionEntry(
                    "b.txt",
                    Path.GetFullPath(@"D:\Work\Docs\Nested\b.txt"),
                    RepositoryEntryKind.File,
                    "nested-file-v1",
                    IsDeleted: false,
                    LogicalLength: 30,
                    DateTimeOffset.UtcNow)
            ]
        };
        var folder = Version("folder-v1", @"D:\Work\Docs", minutesAgo: 0, 50) with
        {
            EntryKind = RepositoryEntryKind.Folder,
            ChunkCount = 0,
            FolderEntries =
            [
                new FolderVersionEntry(
                    "Nested",
                    Path.GetFullPath(@"D:\Work\Docs\Nested"),
                    RepositoryEntryKind.Folder,
                    "nested-folder-v1",
                    IsDeleted: false,
                    LogicalLength: 30,
                    DateTimeOffset.UtcNow),
                new FolderVersionEntry(
                    "a.txt",
                    Path.GetFullPath(@"D:\Work\Docs\a.txt"),
                    RepositoryEntryKind.File,
                    "file-v1",
                    IsDeleted: false,
                    LogicalLength: 20,
                    DateTimeOffset.UtcNow)
            ]
        };
        var viewModel = new VersionInventoryViewModel(
            Path.GetFullPath(@"D:\Work"),
            [folder, childFile, nestedFolder, grandchildFile],
            _ => Task.CompletedTask,
            version =>
            {
                opened.Add(version.VersionId);
                return Task.CompletedTask;
            },
            focusPath: Path.GetFullPath(@"D:\Work\Docs"));

        Assert.Equal(["folder-v1"], viewModel.Versions.Select(version => version.VersionId).ToArray());

        viewModel.SelectedSnapshotEntry = viewModel.SnapshotEntries.Single(entry => entry.EntryKind == RepositoryEntryKind.Folder);
        await viewModel.OpenSelectedSnapshotEntryCommand.ExecuteAsync(null);

        Assert.Equal(["nested-folder-v1"], viewModel.Versions.Select(version => version.VersionId).ToArray());
        viewModel.SelectedSnapshotEntry = Assert.Single(viewModel.SnapshotEntries);
        await viewModel.OpenSelectedSnapshotEntryCommand.ExecuteAsync(null);

        Assert.Equal(["nested-file-v1"], opened);
    }

    [Fact]
    public async Task Preview_busy_state_blocks_duplicate_file_preview_until_callback_finishes()
    {
        var previewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new VersionInventoryViewModel(
            Path.GetFullPath(@"D:\Work"),
            [Version("v1", @"D:\Work\brief.docx", minutesAgo: 0, 20)],
            _ => Task.CompletedTask,
            async version =>
            {
                previewStarted.SetResult();
                await releasePreview.Task.ConfigureAwait(true);
            });
        var versionRow = Assert.Single(Assert.Single(viewModel.Files).Versions);

        var previewTask = viewModel.OpenVersionPreviewCommand.ExecuteAsync(versionRow);
        await previewStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(viewModel.IsPreviewBusy);
        Assert.Equal("Preparing preview...", viewModel.PreviewStatus);
        Assert.False(viewModel.OpenVersionPreviewCommand.CanExecute(versionRow));

        releasePreview.SetResult();
        await previewTask;

        Assert.False(viewModel.IsPreviewBusy);
        Assert.Equal(string.Empty, viewModel.PreviewStatus);
        Assert.True(viewModel.OpenVersionPreviewCommand.CanExecute(versionRow));
    }

    private static RepositoryVersionSummary Version(string id, string path, int minutesAgo, long length)
    {
        return new RepositoryVersionSummary(
            id,
            Path.GetFullPath(path),
            DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            CaptureConsistency.BestEffort,
            length,
            ChunkCount: 1);
    }
}
