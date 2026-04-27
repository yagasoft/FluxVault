using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Ipc;
using WinForms = System.Windows.Forms;

namespace FluxVault.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly NamedPipeFluxVaultClient client;

    [ObservableProperty]
    private string serviceStatus = "Service connection: checking...";

    [ObservableProperty]
    private string repositoryPath = string.Empty;

    [ObservableProperty]
    private string mirrorPath = string.Empty;

    [ObservableProperty]
    private string newWatchedFolderPath = string.Empty;

    [ObservableProperty]
    private WatchedFolderRow? selectedWatchedFolder;

    [ObservableProperty]
    private VersionRow? selectedVersion;

    [ObservableProperty]
    private string diagnosticsText = "Diagnostics are local-only. Use Export diagnostics to write a JSON bundle.";

    public MainWindowViewModel()
        : this(new NamedPipeFluxVaultClient())
    {
    }

    private MainWindowViewModel(NamedPipeFluxVaultClient client)
    {
        this.client = client;
    }

    public string CaptureStrategy { get; } =
        "The service watches configured folders, debounces rapid edits, periodically reconciles missed changes, " +
        "uses normal reads where possible, and falls back to VSS for locked files.";

    public string RepositorySummary { get; } =
        "Versions are stored as immutable BLAKE3-addressed chunks plus manifests. Optional cloud-folder mirroring uses atomic writes.";

    public ObservableCollection<WatchedFolderRow> WatchedFolders { get; } = [];

    public ObservableCollection<VersionRow> RecentVersions { get; } = [];

    public IReadOnlyList<string> ResourceProfiles { get; } =
    [
        "Fast: 2 second debounce for critical folders.",
        "Balanced: 8 second debounce for normal work.",
        "Quiet: 30 second debounce for low background pressure."
    ];

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var response = await client.SendAsync(FluxVaultIpcRequest.GetStatus()).ConfigureAwait(true);
            if (!response.Success || response.Status is null)
            {
                ServiceStatus = $"Service connection: unavailable ({response.ErrorMessage ?? "no status returned"})";
                return;
            }

            ApplyStatus(response.Status);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            ServiceStatus = $"Service connection: unavailable ({ex.Message})";
        }
    }

    [RelayCommand]
    private void BrowseRepository()
    {
        RepositoryPath = BrowseFolder(RepositoryPath);
    }

    [RelayCommand]
    private void BrowseMirror()
    {
        MirrorPath = BrowseFolder(MirrorPath);
    }

    [RelayCommand]
    private void BrowseWatchedFolder()
    {
        NewWatchedFolderPath = BrowseFolder(NewWatchedFolderPath);
    }

    [RelayCommand]
    private void AddWatchedFolder()
    {
        if (string.IsNullOrWhiteSpace(NewWatchedFolderPath))
        {
            return;
        }

        WatchedFolders.Add(new WatchedFolderRow(
            Guid.NewGuid().ToString("N"),
            NewWatchedFolderPath,
            ResourceProfile.Balanced,
            CompressionPreference.Zstd,
            "Pending save"));
        NewWatchedFolderPath = string.Empty;
    }

    [RelayCommand]
    private void RemoveWatchedFolder()
    {
        if (SelectedWatchedFolder is not null)
        {
            WatchedFolders.Remove(SelectedWatchedFolder);
        }
    }

    [RelayCommand]
    private async Task SaveConfigurationAsync()
    {
        var response = await client.SendAsync(FluxVaultIpcRequest.SaveConfiguration(BuildConfiguration())).ConfigureAwait(true);
        ServiceStatus = response.Success
            ? "Service connection: configuration saved"
            : $"Service connection: save failed ({response.ErrorMessage})";
        if (response.Success)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RunBackupNowAsync()
    {
        await SaveConfigurationAsync().ConfigureAwait(true);
        var response = await client.SendAsync(FluxVaultIpcRequest.RunBackupNow()).ConfigureAwait(true);
        ServiceStatus = response.Backup is null
            ? $"Service connection: backup failed ({response.ErrorMessage})"
            : $"Service connection: {response.Backup.Message}";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        if (SelectedVersion is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(SelectedVersion.SourcePath),
            Title = "Restore FluxVault version"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var response = await client.SendAsync(FluxVaultIpcRequest.RestoreVersion(SelectedVersion.VersionId, dialog.FileName))
            .ConfigureAwait(true);
        ServiceStatus = response.Success
            ? $"Service connection: restored {SelectedVersion.VersionId}"
            : $"Service connection: restore failed ({response.ErrorMessage})";
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var path = BrowseFolder(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var response = await client.SendAsync(FluxVaultIpcRequest.ExportDiagnostics(path)).ConfigureAwait(true);
        DiagnosticsText = response.Success
            ? $"Diagnostics exported to {response.OutputPath}"
            : $"Diagnostics export failed: {response.ErrorMessage}";
    }

    private void ApplyStatus(FluxVaultServiceStatus status)
    {
        RepositoryPath = status.Configuration.RepositoryPath;
        MirrorPath = status.Configuration.MirrorPath ?? string.Empty;
        ServiceStatus = $"Service connection: running - {status.LastMessage}";
        WatchedFolders.Clear();
        foreach (var folder in status.Configuration.WatchedFolders)
        {
            var runtime = status.WatchedFolders.SingleOrDefault(value => value.Id == folder.Id);
            WatchedFolders.Add(new WatchedFolderRow(
                folder.Id,
                folder.Path,
                folder.ResourceProfile,
                folder.Compression,
                runtime?.Status ?? "Ready"));
        }

        RecentVersions.Clear();
        foreach (var version in status.RecentVersions)
        {
            RecentVersions.Add(new VersionRow(
                version.VersionId,
                version.SourcePath,
                version.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                version.Consistency,
                version.ChunkCount));
        }
    }

    private FluxVaultConfiguration BuildConfiguration()
    {
        return new FluxVaultConfiguration(
            RepositoryPath: RepositoryPath,
            MirrorPath: string.IsNullOrWhiteSpace(MirrorPath) ? null : MirrorPath,
            IsEnabled: true,
            WatchedFolders: WatchedFolders
                .Select(folder => new WatchedFolderConfiguration(
                    folder.Id,
                    folder.Path,
                    Recursive: true,
                    IncludePatterns: ["*"],
                    ExcludePatterns: ["~$*"],
                    folder.Compression,
                    folder.ResourceProfile,
                    IsEnabled: true))
                .ToArray());
    }

    private static string BrowseFolder(string selectedPath)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(selectedPath) ? selectedPath : string.Empty,
            UseDescriptionForTitle = true,
            Description = "Select folder"
        };
        return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : selectedPath;
    }
}

public sealed record WatchedFolderRow(
    string Id,
    string Path,
    ResourceProfile ResourceProfile,
    CompressionPreference Compression,
    string Status);

public sealed record VersionRow(
    string VersionId,
    string SourcePath,
    string CapturedAt,
    CaptureConsistency Consistency,
    int ChunkCount);
