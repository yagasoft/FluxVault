using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.ChangeTracking;
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
    private string serviceStatusToolTip = "Service connection status is checking.";

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
    private string usnHealthToolTip = "Durable change tracking status is checking.";

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
                    SetServiceStatus($"Service connection: unavailable ({response.ErrorMessage ?? "no status returned"})");
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
            SetServiceUnavailable(ex);
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
        SetServiceStatus(response.Success
            ? "Service connection: configuration saved"
            : $"Service connection: save failed ({response.ErrorMessage})");
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
        SetServiceStatus($"Service connection: unavailable ({ex.Message})");
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
        SetServiceStatus(response.Backup is null
            ? $"Service connection: backup failed ({response.ErrorMessage})"
            : $"Service connection: {response.Backup.Message}");
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
        SetServiceStatus(response.Success
            ? $"Service connection: restored {SelectedVersion.VersionId}"
            : $"Service connection: restore failed ({response.ErrorMessage})");
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
            var visibleStatus = $"Service connection: running - {status.LastMessage.TrimEnd('.')}. Last refreshed {DateTime.Now:HH:mm:ss}";
            SetServiceStatus(visibleStatus, BuildServiceStatusToolTip(visibleStatus, status.DurableChange));
            ApplyDurableChangeHealth(status.DurableChange);
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

    private void ApplyDurableChangeHealth(DurableChangeRuntimeStatus? durableChange)
    {
        if (durableChange is null || string.IsNullOrWhiteSpace(durableChange.Status))
        {
            UsnHealth = "USN: reconciliation scan";
            UsnHealthToolTip = "The service has not reported durable USN tracking yet. FluxVault will use reconciliation scans as the safety net.";
            return;
        }

        UsnHealth = $"USN: {BuildDurableChangeSummary(durableChange)}";
        UsnHealthToolTip = BuildDurableChangeToolTip(durableChange);
    }

    private void SetServiceStatus(string text, string? toolTip = null)
    {
        ServiceStatus = text;
        ServiceStatusToolTip = string.IsNullOrWhiteSpace(toolTip) ? text : toolTip;
    }

    private static string BuildServiceStatusToolTip(string visibleStatus, DurableChangeRuntimeStatus? durableChange)
    {
        if (durableChange is null || string.IsNullOrWhiteSpace(durableChange.Status))
        {
            return visibleStatus;
        }

        return visibleStatus + Environment.NewLine + BuildDurableChangeToolTip(durableChange);
    }

    private static string BuildDurableChangeSummary(DurableChangeRuntimeStatus durableChange)
    {
        var status = FormatDurableStatus(durableChange.Status);
        var latestCheck = FormatLatestUsnCheck(durableChange.LastUsnCatchUpUtc);
        if (string.IsNullOrWhiteSpace(durableChange.FallbackReason))
        {
            return JoinDurableSummary(status, latestCheck);
        }

        var fallback = durableChange.FallbackReason;
        if (Contains(fallback, "Unable to open volume"))
        {
            return JoinDurableSummary(status, latestCheck, "Unable to open volume");
        }

        if (Contains(fallback, "Unsupported volume"))
        {
            return JoinDurableSummary(status, latestCheck, "unsupported volume");
        }

        if (Contains(fallback, "journal wrapped"))
        {
            return JoinDurableSummary(status, latestCheck, "journal wrapped");
        }

        if (Contains(fallback, "journal ID changed"))
        {
            return JoinDurableSummary(status, latestCheck, "journal ID changed");
        }

        if (Contains(fallback, "checkpoint missing"))
        {
            return JoinDurableSummary(status, latestCheck, "checkpoint missing");
        }

        if (Contains(fallback, "File-id path resolution failed"))
        {
            return JoinDurableSummary(status, latestCheck, "file path resolution failed");
        }

        if (Contains(fallback, "FSCTL_QUERY_USN_JOURNAL")
            || Contains(fallback, "NativeDeviceIoControl")
            || durableChange.Details.Any(detail => string.Equals(detail.Operation, "FSCTL_QUERY_USN_JOURNAL", StringComparison.OrdinalIgnoreCase)))
        {
            return JoinDurableSummary(status, latestCheck, "unable to query change journal");
        }

        if (Contains(fallback, "FSCTL_READ_USN_JOURNAL"))
        {
            return JoinDurableSummary(status, latestCheck, "unable to read change journal");
        }

        return JoinDurableSummary(status, latestCheck, TrimSummary(fallback));
    }

    private static string FormatDurableStatus(string status)
    {
        var trimmed = status.Trim().TrimEnd('.');
        var withoutPrefix = trimmed.StartsWith("USN ", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..]
            : trimmed;
        var parts = withoutPrefix.Split(". ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            return withoutPrefix;
        }

        return $"{parts[0]} - {LowercaseFirst(parts[1].TrimEnd('.'))}";
    }

    private static string? FormatLatestUsnCheck(DateTimeOffset? checkedAt)
    {
        return checkedAt is null ? null : $"last checked {checkedAt.Value.ToLocalTime():HH:mm:ss}";
    }

    private static string JoinDurableSummary(params string?[] parts)
    {
        return string.Join(" - ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string LowercaseFirst(string value)
    {
        return string.IsNullOrEmpty(value)
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static bool Contains(string value, string expected)
    {
        return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSummary(string value)
    {
        const int maxLength = 96;
        var singleLine = value.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..(maxLength - 1)] + "...";
    }

    private static string BuildDurableChangeToolTip(DurableChangeRuntimeStatus durableChange)
    {
        var lines = new List<string>();
        AddUniqueLine(lines, durableChange.Status.TrimEnd('.'));
        if (!string.IsNullOrWhiteSpace(durableChange.FallbackReason))
        {
            AddUniqueLine(lines, durableChange.FallbackReason);
        }

        foreach (var detail in durableChange.Details.Take(8))
        {
            var scope = string.IsNullOrWhiteSpace(detail.Path) ? string.Empty : $" ({detail.Path})";
            var error = detail.Win32ErrorCode is null ? string.Empty : $" [Win32 {detail.Win32ErrorCode}]";
            AddUniqueLine(lines, $"{detail.Operation}{scope}: {detail.Reason}{error}");
        }

        if (durableChange.Details.Count > 8)
        {
            lines.Add($"Plus {durableChange.Details.Count - 8} more detail(s) in diagnostics.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddUniqueLine(ICollection<string> lines, string value)
    {
        if (!lines.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(value);
        }
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
