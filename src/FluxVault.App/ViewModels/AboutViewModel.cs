using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FluxVault.App.ViewModels;

public interface IExternalLinkLauncher
{
    Task<bool> OpenAsync(string url, CancellationToken cancellationToken = default);
}

public sealed class ExternalLinkLauncher(Func<ProcessStartInfo, bool>? start = null) : IExternalLinkLauncher
{
    private readonly Func<ProcessStartInfo, bool> start = start ?? (info => Process.Start(info) is not null);

    public Task<bool> OpenAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(url)
        {
            UseShellExecute = true
        };

        return Task.FromResult(start(startInfo));
    }
}

public sealed partial class AboutViewModel : ObservableObject
{
    private readonly IExternalLinkLauncher linkLauncher;

    [ObservableProperty]
    private string statusText = "Ready.";

    public AboutViewModel(IExternalLinkLauncher linkLauncher)
    {
        this.linkLauncher = linkLauncher;
        OpenGitHubCommand = new AsyncRelayCommand(() => OpenAsync(GitHubUrl));
        OpenWebsiteCommand = new AsyncRelayCommand(() => OpenAsync(WebsiteUrl));
    }

    public string AppName { get; } = "FluxVault";

    public string VersionText { get; } = $"Version {GetVersion()}";

    public string CopyrightText { get; } = "© 2026 Yagasoft. Licensed under GPL-3.0.";

    public string GitHubUrl { get; } = "https://github.com/yagasoft/FluxVault";

    public string WebsiteUrl { get; } = "https://yagasoft.com/";

    public string Description { get; } =
        "FluxVault protects large local files with frequent versioned backups, open-file capture, chunked storage, retention, and optional cloud-folder mirroring.";

    public ObservableCollection<string> RoadmapMilestones { get; } =
    [
        "MVP: local Windows protection, WPF dashboard, tray activity, USN catch-up, retention, and restore.",
        "V1: production hardening, richer restore history, repository health checks, and installer polish.",
        "R2: distributed mirror fabric with capacity-aware placement, redundancy, drain, and repair.",
        "R3: multi-PC sync with mapping confirmation, chunk-level hydration, blocked files, and conflict handling.",
        "R4/R5 foundations: WinFsp performance workspace plus Cloud Files API and ProjFS shell integration.",
        "R6 foundation: direct cloud adapters for Azure Blob, S3-compatible storage, Dropbox, Google Drive, and OneDrive.",
        "R7: PostgreSQL whole-PC metadata runtime with local reliability validation next."
    ];

    public IAsyncRelayCommand OpenGitHubCommand { get; }

    public IAsyncRelayCommand OpenWebsiteCommand { get; }

    private async Task OpenAsync(string url)
    {
        try
        {
            var opened = await linkLauncher.OpenAsync(url).ConfigureAwait(true);
            StatusText = opened ? $"Opened {url}" : $"Could not open {url}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            StatusText = $"Could not open {url}: {ex.Message}";
        }
    }

    private static string GetVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
