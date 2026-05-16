using System.Diagnostics;
using FluxVault.App.ViewModels;

namespace FluxVault.App.Tests;

public sealed class AboutViewModelTests
{
    [Fact]
    public void About_view_model_exposes_brand_links_description_and_roadmap()
    {
        var viewModel = new AboutViewModel(new RecordingExternalLinkLauncher());

        Assert.Equal("FluxVault", viewModel.AppName);
        Assert.StartsWith("Version ", viewModel.VersionText, StringComparison.Ordinal);
        Assert.Equal("https://github.com/yagasoft/FluxVault", viewModel.GitHubUrl);
        Assert.Equal("https://yagasoft.com/", viewModel.WebsiteUrl);
        Assert.Contains("Yagasoft", viewModel.CopyrightText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GPL-3.0", viewModel.CopyrightText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("large local files", viewModel.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(viewModel.RoadmapMilestones, milestone => milestone.Contains("MVP", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.RoadmapMilestones, milestone => milestone.Contains("R3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.RoadmapMilestones, milestone => milestone.Contains("foundation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.RoadmapMilestones, milestone => milestone.Contains("R7", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.RoadmapMilestones, milestone => milestone.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.RoadmapMilestones, milestone => milestone.Contains("direct cloud", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.RoadmapMilestones, milestone => milestone.Contains("Dropbox", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.RoadmapMilestones, milestone => milestone.Contains("Google Drive", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.RoadmapMilestones, milestone => milestone.Contains("OneDrive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Link_commands_use_injected_launcher()
    {
        var launcher = new RecordingExternalLinkLauncher();
        var viewModel = new AboutViewModel(launcher);

        await viewModel.OpenGitHubCommand.ExecuteAsync(null);
        await viewModel.OpenWebsiteCommand.ExecuteAsync(null);

        Assert.Equal(["https://github.com/yagasoft/FluxVault", "https://yagasoft.com/"], launcher.Urls);
        Assert.Contains("Opened", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Link_failure_sets_status_without_throwing()
    {
        var viewModel = new AboutViewModel(new RecordingExternalLinkLauncher(false));

        await viewModel.OpenGitHubCommand.ExecuteAsync(null);

        Assert.Contains("Could not open", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task External_link_launcher_uses_shell_execute()
    {
        ProcessStartInfo? captured = null;
        var launcher = new ExternalLinkLauncher(info =>
        {
            captured = info;
            return true;
        });

        var opened = await launcher.OpenAsync("https://yagasoft.com/");

        Assert.True(opened);
        Assert.NotNull(captured);
        Assert.Equal("https://yagasoft.com/", captured.FileName);
        Assert.True(captured.UseShellExecute);
    }

    private sealed class RecordingExternalLinkLauncher(bool result = true) : IExternalLinkLauncher
    {
        public List<string> Urls { get; } = [];

        public Task<bool> OpenAsync(string url, CancellationToken cancellationToken = default)
        {
            Urls.Add(url);
            return Task.FromResult(result);
        }
    }
}
