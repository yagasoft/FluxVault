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
