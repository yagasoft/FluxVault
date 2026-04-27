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
    private readonly IFluxVaultServiceClient client;
    private readonly TimeSpan autoRefreshInterval;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CancellationTokenSource? autoRefreshCancellation;
    private Task? autoRefreshTask;
    private bool isApplyingStatus;
    private bool hasLocalConfigurationChanges;
    private RetentionPolicy currentRetentionPolicy = RetentionPolicy.CreateDefault();
    private CaptureCadencePolicy currentCaptureCadencePolicy = CaptureCadencePolicy.CreateDefault();

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

    [ObservableProperty]
    private string usnHealth = "USN: checking";

    [ObservableProperty]
    private string retentionHealth = "Retention: checking";

    [ObservableProperty]
    private string mirrorHealth = "Mirror: local only";

    [ObservableProperty]
    private string captureHealth = "Capture: idle";

    public MainWindowViewModel()
        : this(new NamedPipeFluxVaultClient(), TimeSpan.FromSeconds(5))
    {
    }

    public MainWindowViewModel(IFluxVaultServiceClient client, TimeSpan autoRefreshInterval)
    {
        this.client = client;
        this.autoRefreshInterval = autoRefreshInterval;
    }

    public string CaptureStrategy { get; } =
        "The service watches configured folders, debounces rapid edits, periodically reconciles missed changes, " +
        "uses normal reads where possible, and falls back to VSS for locked files.";

    public string RepositorySummary { get; } =
        "Versions are stored as immutable BLAKE3-addressed chunks plus manifests. Optional cloud-folder mirroring uses atomic writes.";

    public ObservableCollection<WatchedFolderRow> WatchedFolders { get; } = [];

    public ObservableCollection<VersionRow> RecentVersions { get; } = [];

    public ObservableCollection<CaptureStatusRow> CaptureStatuses { get; } = [];

    public IFluxVaultServiceClient ServiceClient => client;

    public IReadOnlyList<string> ResourceProfiles { get; } =
    [
        "Fast: 2 second debounce for critical folders.",
        "Balanced: 8 second debounce for normal work.",
        "Quiet: 30 second debounce for low background pressure."
    ];

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await RefreshAsync(isAutomatic: false).ConfigureAwait(true);
    }

    public void StartAutoRefresh()
    {
        if (autoRefreshTask is { IsCompleted: false })
        {
            return;
        }

        autoRefreshCancellation = new CancellationTokenSource();
        autoRefreshTask = AutoRefreshAsync(autoRefreshCancellation.Token);
    }

    public void StopAutoRefresh()
    {
        autoRefreshCancellation?.Cancel();
        autoRefreshCancellation?.Dispose();
        autoRefreshCancellation = null;
        autoRefreshTask = null;
    }

    private async Task AutoRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(autoRefreshInterval, cancellationToken).ConfigureAwait(true);
                await RefreshAsync(isAutomatic: true, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAsync(bool isAutomatic, CancellationToken cancellationToken = default)
    {
        try
        {
            if (isAutomatic && hasLocalConfigurationChanges)
            {
                return;
            }

            if (!await refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
            {
                return;
            }

            try
            {
                var response = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), cancellationToken).ConfigureAwait(true);
                if (!response.Success || response.Status is null)
                {
                    ServiceStatus = $"Service connection: unavailable ({response.ErrorMessage ?? "no status returned"})";
                    return;
                }

                ApplyStatus(response.Status);
            }
            finally
            {
                refreshGate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            ServiceStatus = $"Service connection: unavailable ({ex.Message})";
        }
    }

    partial void OnRepositoryPathChanged(string value)
    {
        MarkConfigurationDirty();
    }

    partial void OnMirrorPathChanged(string value)
    {
        MarkConfigurationDirty();
    }

    partial void OnNewWatchedFolderPathChanged(string value)
    {
        MarkConfigurationDirty();
    }

    private void MarkConfigurationDirty()
    {
        if (!isApplyingStatus)
        {
            hasLocalConfigurationChanges = true;
        }
    }

    private async Task SaveConfigurationCoreAsync()
    {
        var response = await client.SendAsync(FluxVaultIpcRequest.SaveConfiguration(BuildConfiguration())).ConfigureAwait(true);
        ServiceStatus = response.Success
            ? "Service connection: configuration saved"
            : $"Service connection: save failed ({response.ErrorMessage})";
        if (response.Success)
        {
            hasLocalConfigurationChanges = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private void MarkStatusApplied()
    {
        hasLocalConfigurationChanges = false;
    }

    private void SetServiceUnavailable(Exception ex)
    {
        ServiceStatus = $"Service connection: unavailable ({ex.Message})";
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
        hasLocalConfigurationChanges = true;
    }

    [RelayCommand]
    private void RemoveWatchedFolder()
    {
        if (SelectedWatchedFolder is not null)
        {
            WatchedFolders.Remove(SelectedWatchedFolder);
            hasLocalConfigurationChanges = true;
        }
    }

    [RelayCommand]
    private async Task SaveConfigurationAsync()
    {
        await SaveConfigurationCoreAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunBackupNowAsync()
    {
        await SaveConfigurationCoreAsync().ConfigureAwait(true);
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
        if (response.Success)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
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
        var selectedVersionId = SelectedVersion?.VersionId;
        isApplyingStatus = true;
        try
        {
            RepositoryPath = status.Configuration.RepositoryPath;
            MirrorPath = status.Configuration.MirrorPath ?? string.Empty;
            currentRetentionPolicy = status.Configuration.RetentionPolicy;
            currentCaptureCadencePolicy = status.Configuration.CaptureCadencePolicy;
            var durableStatus = string.IsNullOrWhiteSpace(status.DurableChange?.Status)
                ? string.Empty
                : $" - {status.DurableChange.Status.TrimEnd('.')}";
            ServiceStatus = $"Service connection: running - {status.LastMessage.TrimEnd('.')}{durableStatus}. Last refreshed {DateTime.Now:HH:mm:ss}";
            UsnHealth = string.IsNullOrWhiteSpace(status.DurableChange?.Status)
                ? "USN: reconciliation scan"
                : $"USN: {status.DurableChange.Status.TrimEnd('.')}";
            RetentionHealth = status.LastRetention is null
                ? "Retention: waiting"
                : $"Retention: kept {status.LastRetention.KeptVersionCount}, pruned {status.LastRetention.PrunedVersionCount}";
            MirrorHealth = string.IsNullOrWhiteSpace(status.Configuration.MirrorPath)
                ? "Mirror: local only"
                : $"Mirror: {status.Configuration.MirrorPath}";
            CaptureHealth = BuildCaptureHealth(status.CaptureStatuses ?? []);
            WatchedFolders.Clear();
            foreach (var folder in status.Configuration.WatchedFolders)
            {
                var runtime = status.WatchedFolders.SingleOrDefault(value => value.Id == folder.Id);
                var runtimeStatus = runtime is null
                    ? "Ready"
                    : $"{runtime.Status} - {runtime.DurableChangeStatus}";
                WatchedFolders.Add(new WatchedFolderRow(
                    folder.Id,
                    folder.Path,
                    folder.ResourceProfile,
                    folder.Compression,
                    runtimeStatus));
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

            SelectedVersion = selectedVersionId is null
                ? null
                : RecentVersions.SingleOrDefault(version => version.VersionId == selectedVersionId);
            CaptureStatuses.Clear();
            foreach (var captureStatus in status.CaptureStatuses ?? [])
            {
                CaptureStatuses.Add(new CaptureStatusRow(
                    captureStatus.SourcePath,
                    captureStatus.State,
                    captureStatus.LastEventUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                    captureStatus.NextForcedCaptureUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                    captureStatus.BlockedReason ?? captureStatus.DelayReason ?? captureStatus.Consistency?.ToString() ?? string.Empty));
            }

            MarkStatusApplied();
        }
        finally
        {
            isApplyingStatus = false;
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
                .ToArray(),
            RetentionPolicy: currentRetentionPolicy,
            CaptureCadencePolicy: currentCaptureCadencePolicy);
    }

    private static string BuildCaptureHealth(IReadOnlyList<CaptureRuntimeStatus> statuses)
    {
        var blocked = statuses.Count(status => status.State == CaptureRuntimeState.Blocked);
        if (blocked > 0)
        {
            return $"Capture: Blocked files {blocked}";
        }

        var capturing = statuses.Count(status => status.State == CaptureRuntimeState.Capturing);
        if (capturing > 0)
        {
            return $"Capture: capturing {capturing}";
        }

        var pending = statuses.Count(status => status.State is CaptureRuntimeState.WaitingForQuietWindow or CaptureRuntimeState.ForcedHotFileSnapshot);
        return pending > 0 ? $"Capture: pending {pending}" : "Capture: idle";
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

public sealed record CaptureStatusRow(
    string SourcePath,
    CaptureRuntimeState State,
    string LastEvent,
    string NextForcedCapture,
    string Detail);
