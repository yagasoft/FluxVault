using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;
using WinForms = System.Windows.Forms;

namespace FluxVault.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IFluxVaultServiceClient client;
    private readonly IFluxVaultWindowsServiceController windowsServiceController;
    private readonly IRestoreDestinationPicker restoreDestinationPicker;
    private readonly IRestoreOverwriteConfirmation restoreOverwriteConfirmation;
    private readonly TimeSpan autoRefreshInterval;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CancellationTokenSource? autoRefreshCancellation;
    private Task? autoRefreshTask;
    private bool isApplyingStatus;
    private bool hasLocalConfigurationChanges;
    private RetentionPolicy currentRetentionPolicy = RetentionPolicy.CreateDefault();
    private CaptureCadencePolicy currentCaptureCadencePolicy = CaptureCadencePolicy.CreateDefault();
    private CodecPolicy currentCodecPolicy = CodecPolicy.CreateDefault();
    private IReadOnlyList<ProtectionExclusionRule> currentExclusionRules = [];
    private FluxVaultWindowsServiceStatus windowsServiceStatus = new(
        WindowsFluxVaultServiceController.DefaultServiceName,
        FluxVaultWindowsServiceState.Unknown,
        "FluxVault service status is checking.");

    [ObservableProperty]
    private string serviceStatus = "Service connection: checking...";

    [ObservableProperty]
    private string serviceStatusToolTip = "Service connection status is checking.";

    [ObservableProperty]
    private string serviceWarningText = "FluxVault service status is checking.";

    [ObservableProperty]
    private bool isServiceWarningVisible;

    [ObservableProperty]
    private string serviceControlActionLabel = "Start service";

    [ObservableProperty]
    private string serviceControlToolTip = "Start or stop the FluxVault Windows service.";

    [ObservableProperty]
    private bool isServiceControlActionEnabled;

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
    private string restoreHintPath = string.Empty;

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
        : this(
            new NamedPipeFluxVaultClient(),
            TimeSpan.FromSeconds(5),
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new WindowsFluxVaultServiceController(),
            new SaveFileRestoreDestinationPicker(),
            new MessageBoxRestoreOverwriteConfirmation())
    {
    }

    public MainWindowViewModel(IFluxVaultServiceClient client, TimeSpan autoRefreshInterval)
        : this(
            client,
            autoRefreshInterval,
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new AssumedRunningWindowsServiceController(),
            new SaveFileRestoreDestinationPicker(),
            new MessageBoxRestoreOverwriteConfirmation())
    {
    }

    public MainWindowViewModel(
        IFluxVaultServiceClient client,
        TimeSpan autoRefreshInterval,
        IRestoreDestinationPicker restoreDestinationPicker,
        IRestoreOverwriteConfirmation restoreOverwriteConfirmation)
        : this(
            client,
            autoRefreshInterval,
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new AssumedRunningWindowsServiceController(),
            restoreDestinationPicker,
            restoreOverwriteConfirmation)
    {
    }

    public MainWindowViewModel(
        IFluxVaultServiceClient client,
        TimeSpan autoRefreshInterval,
        FileBrowserViewModel fileBrowser)
        : this(
            client,
            autoRefreshInterval,
            fileBrowser,
            new WindowsFluxVaultServiceController(),
            new SaveFileRestoreDestinationPicker(),
            new MessageBoxRestoreOverwriteConfirmation())
    {
    }

    public MainWindowViewModel(
        IFluxVaultServiceClient client,
        TimeSpan autoRefreshInterval,
        FileBrowserViewModel fileBrowser,
        IFluxVaultWindowsServiceController windowsServiceController)
        : this(
            client,
            autoRefreshInterval,
            fileBrowser,
            windowsServiceController,
            new SaveFileRestoreDestinationPicker(),
            new MessageBoxRestoreOverwriteConfirmation())
    {
    }

    public MainWindowViewModel(
        IFluxVaultServiceClient client,
        TimeSpan autoRefreshInterval,
        FileBrowserViewModel fileBrowser,
        IFluxVaultWindowsServiceController windowsServiceController,
        IRestoreDestinationPicker restoreDestinationPicker,
        IRestoreOverwriteConfirmation restoreOverwriteConfirmation)
    {
        this.client = client;
        this.windowsServiceController = windowsServiceController;
        this.restoreDestinationPicker = restoreDestinationPicker;
        this.restoreOverwriteConfirmation = restoreOverwriteConfirmation;
        this.autoRefreshInterval = autoRefreshInterval;
        FileBrowser = fileBrowser;
        FileBrowser.SelectionRulesChanged += (_, _) => MarkConfigurationDirty();
    }

    public string CaptureStrategy { get; } =
        "The service watches configured folders, debounces rapid edits, periodically reconciles missed changes, " +
        "uses normal reads where possible, and falls back to writer-aware VSS for locked files.";

    public string RepositorySummary { get; } =
        "Versions are stored as immutable BLAKE3-addressed chunks plus manifests. Optional cloud-folder mirroring uses atomic writes.";

    public ObservableCollection<WatchedFolderRow> WatchedFolders { get; } = [];

    public ObservableCollection<VersionRow> RecentVersions { get; } = [];

    public ObservableCollection<CaptureStatusRow> CaptureStatuses { get; } = [];

    public FileBrowserViewModel FileBrowser { get; }

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

    public void ApplyRestorePathRequest(string restorePath)
    {
        if (string.IsNullOrWhiteSpace(restorePath))
        {
            return;
        }

        RestoreHintPath = restorePath;
        SelectedVersion = FindRestoreHintVersion() ?? SelectedVersion;
        SetServiceStatus($"Service connection: restore request received for {restorePath}");
    }

    public async Task ApplyStartupRequestAsync(AppStartupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return;
        }

        switch (request.Action)
        {
            case AppStartupRequestAction.ShowVersions:
                ApplyRestorePathRequest(request.Path);
                break;
            case AppStartupRequestAction.AddToFluxVault:
                await ApplyExplorerSelectionRequestAsync(request.Path, add: true).ConfigureAwait(true);
                break;
            case AppStartupRequestAction.RemoveFromFluxVault:
                await ApplyExplorerSelectionRequestAsync(request.Path, add: false).ConfigureAwait(true);
                break;
        }
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

    private async Task RefreshAsync(
        bool isAutomatic,
        CancellationToken cancellationToken = default,
        bool forceConfigurationReload = false)
    {
        try
        {
            if (!await refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
            {
                return;
            }

            try
            {
                var serviceStatusSnapshot = await RefreshWindowsServiceStatusAsync(cancellationToken).ConfigureAwait(true);
                if (serviceStatusSnapshot.State is FluxVaultWindowsServiceState.Stopped
                    or FluxVaultWindowsServiceState.NotInstalled
                    or FluxVaultWindowsServiceState.StartPending
                    or FluxVaultWindowsServiceState.StopPending)
                {
                    SetServiceStatus($"Service connection: unavailable ({serviceStatusSnapshot.Message})");
                    return;
                }

                var response = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), cancellationToken).ConfigureAwait(true);
                if (!response.Success || response.Status is null)
                {
                    SetServiceStatus($"Service connection: unavailable ({response.ErrorMessage ?? "no status returned"})");
                    SetServiceConnectionWarning(response.ErrorMessage ?? "The dashboard cannot connect to the FluxVault service.");
                    return;
                }

                ApplyStatus(response.Status, preserveLocalConfiguration: hasLocalConfigurationChanges && !forceConfigurationReload);
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
        catch (InvalidOperationException ex)
        {
            SetServiceUnavailable(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleWindowsService))]
    private async Task ToggleWindowsServiceAsync()
    {
        FluxVaultWindowsServiceActionResult result;
        if (windowsServiceStatus.State == FluxVaultWindowsServiceState.Running)
        {
            result = await windowsServiceController.StopAsync().ConfigureAwait(true);
        }
        else if (windowsServiceStatus.State == FluxVaultWindowsServiceState.Stopped)
        {
            result = await windowsServiceController.StartAsync().ConfigureAwait(true);
        }
        else
        {
            await RefreshWindowsServiceStatusAsync().ConfigureAwait(true);
            return;
        }

        ApplyWindowsServiceStatus(result.Status);
        if (!result.Success)
        {
            SetServiceConnectionWarning(result.Message);
            SetServiceStatus($"Service connection: unavailable ({result.Message})");
            return;
        }

        if (result.Status.State == FluxVaultWindowsServiceState.Running)
        {
            IsServiceWarningVisible = false;
            ServiceWarningText = result.Message;
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        SetServiceConnectionWarning(result.Message);
        SetServiceStatus($"Service connection: unavailable ({result.Message})");
    }

    private bool CanToggleWindowsService()
    {
        return IsServiceControlActionEnabled;
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

    private async Task SaveConfigurationCoreAsync(bool refreshAfterSave = true, string? successStatus = null)
    {
        var response = await client.SendAsync(FluxVaultIpcRequest.SaveConfiguration(BuildConfiguration())).ConfigureAwait(true);
        SetServiceStatus(response.Success
            ? successStatus ?? "Service connection: configuration saved"
            : $"Service connection: save failed ({response.ErrorMessage})");
        if (response.Success)
        {
            hasLocalConfigurationChanges = false;
            if (refreshAfterSave)
            {
                await RefreshAsync().ConfigureAwait(true);
            }
        }
    }

    [RelayCommand]
    private async Task DiscardConfigurationChangesAsync()
    {
        hasLocalConfigurationChanges = false;
        await RefreshAsync(isAutomatic: false, forceConfigurationReload: true).ConfigureAwait(true);
    }

    private void MarkStatusApplied()
    {
        hasLocalConfigurationChanges = false;
    }

    private void SetServiceUnavailable(Exception ex)
    {
        SetServiceStatus($"Service connection: unavailable ({ex.Message})");
        SetServiceConnectionWarning($"The dashboard cannot connect to the FluxVault service: {ex.Message}");
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

    private async Task ApplyExplorerSelectionRequestAsync(string path, bool add)
    {
        var isDirectory = IsDirectoryLike(path);
        if (add)
        {
            FileBrowser.AddPathSelection(path, isDirectory);
            await SaveConfigurationCoreAsync(
                refreshAfterSave: false,
                successStatus: $"Service connection: added {path} to FluxVault.").ConfigureAwait(true);
            return;
        }

        var removedOrExcluded = FileBrowser.RemovePathSelection(path, isDirectory);
        if (!removedOrExcluded)
        {
            SetServiceStatus($"Service connection: {path} is not directly or inheritably protected.");
            return;
        }

        await SaveConfigurationCoreAsync(
            refreshAfterSave: false,
            successStatus: $"Service connection: removed or excluded {path} from FluxVault.").ConfigureAwait(true);
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
        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
        {
            return;
        }

        var destination = restoreDestinationPicker.PickDestination(selectedVersion);
        if (string.IsNullOrWhiteSpace(destination))
        {
            SetServiceStatus("Service connection: restore cancelled.");
            return;
        }

        if (File.Exists(destination) && !restoreOverwriteConfirmation.ConfirmOverwrite(destination))
        {
            SetServiceStatus("Service connection: restore overwrite denied.");
            return;
        }

        try
        {
            var response = await client.SendAsync(FluxVaultIpcRequest.RestoreVersion(selectedVersion.VersionId, destination))
                .ConfigureAwait(true);
            SetServiceStatus(response.Success
                ? $"Service connection: restored {selectedVersion.VersionId} to {destination}"
                : $"Service connection: restore failed ({response.ErrorMessage})");
            if (response.Success)
            {
                await RefreshAsync().ConfigureAwait(true);
                SetServiceStatus($"Service connection: restored {selectedVersion.VersionId} to {destination}");
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetServiceStatus($"Service connection: restore failed ({ex.Message})");
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

    private void ApplyStatus(FluxVaultServiceStatus status, bool preserveLocalConfiguration)
    {
        var selectedVersionId = SelectedVersion?.VersionId;
        isApplyingStatus = true;
        try
        {
            if (!preserveLocalConfiguration)
            {
                RepositoryPath = status.Configuration.RepositoryPath;
                MirrorPath = status.Configuration.MirrorPath ?? string.Empty;
                currentRetentionPolicy = status.Configuration.RetentionPolicy;
                currentCaptureCadencePolicy = status.Configuration.CaptureCadencePolicy;
                currentCodecPolicy = status.Configuration.CodecPolicy;
                currentExclusionRules = status.Configuration.ExclusionRules ?? [];
                var selectionRules = status.Configuration.SelectionRules;
                FileBrowser.LoadSelectionRules(selectionRules is { Count: > 0 }
                    ? selectionRules
                    : DeriveSelectionRules(status.Configuration.WatchedFolders));
                if (FileBrowser.Roots.Count == 0)
                {
                    FileBrowser.LoadRoots();
                }
            }

            var visibleStatus = $"Service connection: running - {status.LastMessage.TrimEnd('.')}. Last refreshed {DateTime.Now:HH:mm:ss}";
            if (!string.IsNullOrWhiteSpace(RestoreHintPath))
            {
                visibleStatus += $". Restore request: {RestoreHintPath}";
            }

            SetServiceStatus(visibleStatus, BuildServiceStatusToolTip(visibleStatus, status.DurableChange));
            if (windowsServiceStatus.State == FluxVaultWindowsServiceState.Running)
            {
                IsServiceWarningVisible = false;
                ServiceWarningText = windowsServiceStatus.Message;
            }

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
                    version.ChunkCount,
                    FormatLineage(version)));
            }

            SelectedVersion = selectedVersionId is null
                ? FindRestoreHintVersion()
                : RecentVersions.SingleOrDefault(version => version.VersionId == selectedVersionId) ?? FindRestoreHintVersion();
            CaptureStatuses.Clear();
            foreach (var captureStatus in status.CaptureStatuses ?? [])
            {
                CaptureStatuses.Add(new CaptureStatusRow(
                    captureStatus.SourcePath,
                    captureStatus.State,
                    captureStatus.LastEventUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                    captureStatus.NextForcedCaptureUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                    captureStatus.BlockedReason
                        ?? captureStatus.DelayReason
                        ?? captureStatus.ConsistencyDetail
                        ?? captureStatus.Consistency?.ToString()
                        ?? string.Empty));
            }

            if (!preserveLocalConfiguration)
            {
                MarkStatusApplied();
            }
        }
        finally
        {
            isApplyingStatus = false;
        }
    }

    private FluxVaultConfiguration BuildConfiguration()
    {
        var selectionRules = FileBrowser.GetSelectionRules();
        var watchedFolders = ProtectionSelectionCompiler.Compile(selectionRules);
        return new FluxVaultConfiguration(
            RepositoryPath: RepositoryPath,
            MirrorPath: string.IsNullOrWhiteSpace(MirrorPath) ? null : MirrorPath,
            IsEnabled: true,
            WatchedFolders: watchedFolders,
            RetentionPolicy: currentRetentionPolicy,
            CaptureCadencePolicy: currentCaptureCadencePolicy,
            CodecPolicy: currentCodecPolicy,
            SelectionRules: selectionRules,
            ExclusionRules: currentExclusionRules);
    }

    private VersionRow? FindRestoreHintVersion()
    {
        if (string.IsNullOrWhiteSpace(RestoreHintPath))
        {
            return null;
        }

        return RecentVersions.FirstOrDefault(version => SourcePathMatchesRestoreHint(version.SourcePath, RestoreHintPath));
    }

    private static bool SourcePathMatchesRestoreHint(string sourcePath, string restoreHintPath)
    {
        try
        {
            var source = Path.GetFullPath(sourcePath);
            var hint = Path.GetFullPath(restoreHintPath);
            if (string.Equals(source, hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var hintRoot = hint.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return source.StartsWith(hintRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(sourcePath, restoreHintPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FormatLineage(RepositoryVersionSummary version)
    {
        return version.OperationType switch
        {
            VersionOperationType.Restore when !string.IsNullOrWhiteSpace(version.RestoredFromVersionId)
                => $"Restored from {version.RestoredFromVersionId}",
            VersionOperationType.InheritedCopy when !string.IsNullOrWhiteSpace(version.InheritedFromVersionId)
                => $"Inherited from {version.InheritedFromVersionId}",
            VersionOperationType.Capture when version.ParentVersionIds is { Count: > 0 }
                => $"Parent {version.ParentVersionIds[0]}",
            _ => "Capture"
        };
    }

    private static bool IsDirectoryLike(string path)
    {
        if (Directory.Exists(path))
        {
            return true;
        }

        if (File.Exists(path))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(Path.GetExtension(path));
    }

    private static IReadOnlyList<ProtectionSelectionRule> DeriveSelectionRules(IReadOnlyList<WatchedFolderConfiguration> watchedFolders)
    {
        var rules = new List<ProtectionSelectionRule>();
        foreach (var folder in watchedFolders)
        {
            if (folder.IncludePatterns.Count == 1 && folder.IncludePatterns[0] == "*")
            {
                rules.Add(new ProtectionSelectionRule(
                    folder.Id,
                    folder.Path,
                    folder.Recursive ? ProtectionSelectionMode.RecursiveFolder : ProtectionSelectionMode.ImmediateFiles,
                    folder.Compression,
                    folder.ResourceProfile,
                    folder.IsEnabled));
                continue;
            }

            foreach (var pattern in folder.IncludePatterns)
            {
                if (pattern.Contains('*') || pattern.Contains('?'))
                {
                    continue;
                }

                var filePath = Path.Combine(folder.Path, pattern);
                rules.Add(new ProtectionSelectionRule(
                    $"{folder.Id}-{pattern}",
                    filePath,
                    ProtectionSelectionMode.File,
                    folder.Compression,
                    folder.ResourceProfile,
                    folder.IsEnabled));
            }
        }

        return rules;
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

    private async Task<FluxVaultWindowsServiceStatus> RefreshWindowsServiceStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await windowsServiceController.GetStatusAsync(cancellationToken).ConfigureAwait(true);
        ApplyWindowsServiceStatus(status);
        return status;
    }

    private void ApplyWindowsServiceStatus(FluxVaultWindowsServiceStatus status)
    {
        windowsServiceStatus = status;
        ServiceControlActionLabel = status.ActionLabel;
        ServiceControlToolTip = status.State switch
        {
            FluxVaultWindowsServiceState.Running => "Stop the FluxVault Windows service.",
            FluxVaultWindowsServiceState.Stopped => "Start the FluxVault Windows service.",
            _ => status.Message
        };
        IsServiceControlActionEnabled = status.CanToggle;
        ToggleWindowsServiceCommand.NotifyCanExecuteChanged();

        if (status.IsWarning)
        {
            SetServiceConnectionWarning(status.Message);
        }
    }

    private void SetServiceConnectionWarning(string message)
    {
        ServiceWarningText = message;
        IsServiceWarningVisible = true;
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

    private sealed class AssumedRunningWindowsServiceController : IFluxVaultWindowsServiceController
    {
        private static readonly FluxVaultWindowsServiceStatus RunningStatus = new(
            WindowsFluxVaultServiceController.DefaultServiceName,
            FluxVaultWindowsServiceState.Running,
            "FluxVault service is running.");

        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(RunningStatus);
        }

        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new FluxVaultWindowsServiceActionResult(true, RunningStatus, "FluxVault service is already running."));
        }

        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default)
        {
            var stopped = new FluxVaultWindowsServiceStatus(
                WindowsFluxVaultServiceController.DefaultServiceName,
                FluxVaultWindowsServiceState.Stopped,
                "FluxVault service stopped.");
            return Task.FromResult(new FluxVaultWindowsServiceActionResult(true, stopped, stopped.Message));
        }
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
    int ChunkCount,
    string Lineage = "Capture");

public sealed record CaptureStatusRow(
    string SourcePath,
    CaptureRuntimeState State,
    string LastEvent,
    string NextForcedCapture,
    string Detail);
