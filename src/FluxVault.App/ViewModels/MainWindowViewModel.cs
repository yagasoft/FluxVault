using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private const int MirrorsWorkspaceIndex = 3;
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
    private WorkloadPolicyConfiguration currentWorkloadPolicy = WorkloadPolicyConfiguration.CreateDefault();
    private IReadOnlyList<ProtectionExclusionRule> currentExclusionRules = [];
    private RepositoryScrubReport? currentScrubReport;
    private RestoreRehearsalReport? currentRestoreRehearsalReport;
    private MirrorRepairReport? currentMirrorRepairReport;
    private MirrorRebalancePreviewReport? currentMirrorRebalanceReport;
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
    private int selectedWorkspaceIndex;

    [ObservableProperty]
    private string mirrorSummary = "Mirrors: local only";

    [ObservableProperty]
    private MirrorPlacementProfile mirrorPlacementProfile = MirrorPlacementProfile.FullCopy;

    [ObservableProperty]
    private int minimumMirrorCopies = 1;

    [ObservableProperty]
    private MirrorNodeRow? selectedMirrorNode;

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
    private string repositoryHealthStatus = "Repository health: waiting";

    [ObservableProperty]
    private string deviceIdentityStatus = "Device: waiting";

    [ObservableProperty]
    private string trustedDeviceSummary = "Trusted devices: waiting";

    [ObservableProperty]
    private string syncPeerSummary = "Sync: waiting";

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

    public ObservableCollection<RepositoryHealthRow> RepositoryHealthRows { get; } = [];

    public ObservableCollection<MirrorNodeRow> MirrorNodes { get; } = [];

    public FileBrowserViewModel FileBrowser { get; }

    public IReadOnlyList<MirrorPlacementProfile> MirrorPlacementProfiles { get; } =
    [
        MirrorPlacementProfile.FullCopy,
        MirrorPlacementProfile.CapacityBalanced,
        MirrorPlacementProfile.Redundant
    ];

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

    private void ReplaceMirrorNodes(IReadOnlyList<MirrorNodeConfiguration> nodes)
    {
        foreach (var row in MirrorNodes)
        {
            row.PropertyChanged -= MirrorNode_PropertyChanged;
        }

        MirrorNodes.Clear();
        foreach (var node in nodes)
        {
            AddMirrorRow(new MirrorNodeRow(node.Id, node.Label, node.Path, node.IsEnabled, node.CapacityBudgetBytes, node.Priority));
        }

        SelectedMirrorNode = MirrorNodes.FirstOrDefault();
        UpdateMirrorSummary();
        ApplyMirrorRepairToNodes(currentMirrorRepairReport);
        ApplyMirrorPlacementToNodes(currentMirrorRebalanceReport);
    }

    private void AddMirrorRow(MirrorNodeRow row)
    {
        row.PropertyChanged += MirrorNode_PropertyChanged;
        MirrorNodes.Add(row);
    }

    private void MirrorNode_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkConfigurationDirty();
        UpdateMirrorSummary();
    }

    private void UpdateMirrorSummary()
    {
        var total = MirrorNodes.Count;
        if (total == 0)
        {
            MirrorSummary = "Mirrors: local only";
            return;
        }

        var enabled = MirrorNodes.Count(node => node.IsEnabled);
        MirrorSummary = $"Mirrors: {enabled} of {total} enabled, {MirrorPlacementProfile} placement";
    }

    partial void OnMirrorPlacementProfileChanged(MirrorPlacementProfile value)
    {
        if (!isApplyingStatus)
        {
            MarkConfigurationDirty();
        }

        UpdateMirrorSummary();
    }

    partial void OnMinimumMirrorCopiesChanged(int value)
    {
        if (!isApplyingStatus)
        {
            MarkConfigurationDirty();
        }
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
    private void OpenMirrorsWorkspace()
    {
        SelectedWorkspaceIndex = MirrorsWorkspaceIndex;
    }

    [RelayCommand]
    private void AddMirror()
    {
        var row = new MirrorNodeRow(
            Guid.NewGuid().ToString("N"),
            "New mirror",
            string.Empty,
            isEnabled: false);
        AddMirrorRow(row);
        SelectedMirrorNode = row;
        MarkConfigurationDirty();
        UpdateMirrorSummary();
    }

    [RelayCommand]
    private void RemoveMirror()
    {
        if (SelectedMirrorNode is null)
        {
            return;
        }

        SelectedMirrorNode.PropertyChanged -= MirrorNode_PropertyChanged;
        MirrorNodes.Remove(SelectedMirrorNode);
        SelectedMirrorNode = MirrorNodes.FirstOrDefault();
        MarkConfigurationDirty();
        UpdateMirrorSummary();
    }

    [RelayCommand]
    private void BrowseSelectedMirror()
    {
        if (SelectedMirrorNode is null)
        {
            return;
        }

        SelectedMirrorNode.Path = BrowseFolder(SelectedMirrorNode.Path);
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

    [RelayCommand]
    private async Task RunRepositoryScrubAsync()
    {
        try
        {
            var response = await client.SendAsync(FluxVaultIpcRequest.RunRepositoryScrub()).ConfigureAwait(true);
            if (!response.Success || response.RepositoryScrub is null)
            {
                RepositoryHealthStatus = $"Repository scrub failed: {response.ErrorMessage ?? "no scrub report returned"}";
                return;
            }

            currentScrubReport = response.RepositoryScrub;
            ApplyRepositoryHealth(new RepositoryHealthSnapshot(
                DateTimeOffset.UtcNow,
                CombineHealth(currentScrubReport.HealthState, currentRestoreRehearsalReport?.HealthState, currentMirrorRepairReport?.HealthState, currentMirrorRebalanceReport?.HealthState),
                "Repository scrub completed.",
                currentScrubReport,
                currentRestoreRehearsalReport,
                currentMirrorRepairReport,
                currentMirrorRebalanceReport));
            RepositoryHealthStatus = $"Repository scrub completed - {response.RepositoryScrub.HealthState}";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            RepositoryHealthStatus = $"Repository scrub failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunRestoreRehearsalAsync()
    {
        try
        {
            var response = await client.SendAsync(FluxVaultIpcRequest.RunRestoreRehearsal()).ConfigureAwait(true);
            if (!response.Success || response.RestoreRehearsal is null)
            {
                RepositoryHealthStatus = $"Restore rehearsal failed: {response.ErrorMessage ?? "no rehearsal report returned"}";
                return;
            }

            currentRestoreRehearsalReport = response.RestoreRehearsal;
            ApplyRepositoryHealth(new RepositoryHealthSnapshot(
                DateTimeOffset.UtcNow,
                CombineHealth(currentScrubReport?.HealthState, currentRestoreRehearsalReport.HealthState, currentMirrorRepairReport?.HealthState, currentMirrorRebalanceReport?.HealthState),
                "Restore rehearsal completed.",
                currentScrubReport,
                currentRestoreRehearsalReport,
                currentMirrorRepairReport,
                currentMirrorRebalanceReport));
            RepositoryHealthStatus = $"Restore rehearsal completed - {response.RestoreRehearsal.HealthState}";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            RepositoryHealthStatus = $"Restore rehearsal failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PreviewMirrorRepairAsync()
    {
        await RunMirrorRepairCoreAsync(isPreview: true, mirrorNodeId: null).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunMirrorRepairAsync()
    {
        await RunMirrorRepairCoreAsync(isPreview: false, mirrorNodeId: null).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PreviewSelectedMirrorRepairAsync()
    {
        if (SelectedMirrorNode is null)
        {
            RepositoryHealthStatus = "Mirror repair preview failed: no mirror is selected.";
            return;
        }

        await RunMirrorRepairCoreAsync(isPreview: true, SelectedMirrorNode.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunSelectedMirrorRepairAsync()
    {
        if (SelectedMirrorNode is null)
        {
            RepositoryHealthStatus = "Mirror repair failed: no mirror is selected.";
            return;
        }

        await RunMirrorRepairCoreAsync(isPreview: false, SelectedMirrorNode.Id).ConfigureAwait(true);
    }

    private async Task RunMirrorRepairCoreAsync(bool isPreview, string? mirrorNodeId)
    {
        try
        {
            var request = isPreview
                ? FluxVaultIpcRequest.PreviewMirrorRepair(mirrorNodeId)
                : FluxVaultIpcRequest.RunMirrorRepair(mirrorNodeId);
            var response = await client.SendAsync(request).ConfigureAwait(true);
            if (!response.Success || response.MirrorRepair is null)
            {
                RepositoryHealthStatus = $"{(isPreview ? "Mirror repair preview" : "Mirror repair")} failed: {response.ErrorMessage ?? "no mirror repair report returned"}";
                return;
            }

            currentMirrorRepairReport = response.MirrorRepair;
            ApplyRepositoryHealth(new RepositoryHealthSnapshot(
                DateTimeOffset.UtcNow,
                CombineHealth(currentScrubReport?.HealthState, currentRestoreRehearsalReport?.HealthState, currentMirrorRepairReport.HealthState, currentMirrorRebalanceReport?.HealthState),
                isPreview ? "Mirror repair preview completed." : "Mirror repair completed.",
                currentScrubReport,
                currentRestoreRehearsalReport,
                currentMirrorRepairReport,
                currentMirrorRebalanceReport));
            RepositoryHealthStatus = $"{(isPreview ? "Mirror repair preview" : "Mirror repair")} completed - {response.MirrorRepair.HealthState}";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            RepositoryHealthStatus = $"{(isPreview ? "Mirror repair preview" : "Mirror repair")} failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PreviewSelectedMirrorDrainAsync()
    {
        if (SelectedMirrorNode is null)
        {
            RepositoryHealthStatus = "Mirror drain preview failed: no mirror is selected.";
            return;
        }

        await RunMirrorDrainCoreAsync(isPreview: true, SelectedMirrorNode.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunSelectedMirrorDrainAsync()
    {
        if (SelectedMirrorNode is null)
        {
            RepositoryHealthStatus = "Mirror drain failed: no mirror is selected.";
            return;
        }

        await RunMirrorDrainCoreAsync(isPreview: false, SelectedMirrorNode.Id).ConfigureAwait(true);
    }

    private async Task RunMirrorDrainCoreAsync(bool isPreview, string mirrorNodeId)
    {
        try
        {
            var request = isPreview
                ? FluxVaultIpcRequest.PreviewMirrorDrain(mirrorNodeId)
                : FluxVaultIpcRequest.RunMirrorDrain(mirrorNodeId);
            var response = await client.SendAsync(request).ConfigureAwait(true);
            if (!response.Success || response.MirrorRebalance is null)
            {
                RepositoryHealthStatus = $"{(isPreview ? "Mirror drain preview" : "Mirror drain")} failed: {response.ErrorMessage ?? "no mirror drain report returned"}";
                return;
            }

            currentMirrorRebalanceReport = response.MirrorRebalance;
            ApplyRepositoryHealth(new RepositoryHealthSnapshot(
                DateTimeOffset.UtcNow,
                CombineHealth(currentScrubReport?.HealthState, currentRestoreRehearsalReport?.HealthState, currentMirrorRepairReport?.HealthState, currentMirrorRebalanceReport.HealthState),
                isPreview ? "Mirror drain preview completed." : "Mirror drain completed.",
                currentScrubReport,
                currentRestoreRehearsalReport,
                currentMirrorRepairReport,
                currentMirrorRebalanceReport));
            RepositoryHealthStatus = $"{(isPreview ? "Mirror drain preview" : "Mirror drain")} completed - {response.MirrorRebalance.HealthState}";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            RepositoryHealthStatus = $"{(isPreview ? "Mirror drain preview" : "Mirror drain")} failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PreviewMirrorRebalanceAsync()
    {
        try
        {
            var response = await client.SendAsync(new FluxVaultIpcRequest(
                FluxVaultIpcCommand.PreviewMirrorRebalance,
                null,
                null,
                null,
                null)).ConfigureAwait(true);
            if (!response.Success || response.MirrorRebalance is null)
            {
                RepositoryHealthStatus = $"Mirror placement preview failed: {response.ErrorMessage ?? "no placement preview returned"}";
                return;
            }

            currentMirrorRebalanceReport = response.MirrorRebalance;
            ApplyRepositoryHealth(new RepositoryHealthSnapshot(
                DateTimeOffset.UtcNow,
                CombineHealth(currentScrubReport?.HealthState, currentRestoreRehearsalReport?.HealthState, currentMirrorRepairReport?.HealthState, currentMirrorRebalanceReport.HealthState),
                "Mirror placement preview completed.",
                currentScrubReport,
                currentRestoreRehearsalReport,
                currentMirrorRepairReport,
                currentMirrorRebalanceReport));
            RepositoryHealthStatus = $"Mirror placement preview completed - {response.MirrorRebalance.HealthState}";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            RepositoryHealthStatus = $"Mirror placement preview failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunMirrorRebalanceAsync()
    {
        try
        {
            var response = await client.SendAsync(new FluxVaultIpcRequest(
                FluxVaultIpcCommand.RunMirrorRebalance,
                null,
                null,
                null,
                null)).ConfigureAwait(true);
            if (!response.Success || response.MirrorRebalance is null)
            {
                RepositoryHealthStatus = $"Mirror placement apply failed: {response.ErrorMessage ?? "no placement report returned"}";
                return;
            }

            currentMirrorRebalanceReport = response.MirrorRebalance;
            ApplyRepositoryHealth(new RepositoryHealthSnapshot(
                DateTimeOffset.UtcNow,
                CombineHealth(currentScrubReport?.HealthState, currentRestoreRehearsalReport?.HealthState, currentMirrorRepairReport?.HealthState, currentMirrorRebalanceReport.HealthState),
                "Mirror placement apply completed.",
                currentScrubReport,
                currentRestoreRehearsalReport,
                currentMirrorRepairReport,
                currentMirrorRebalanceReport));
            RepositoryHealthStatus = $"Mirror placement apply completed - {response.MirrorRebalance.HealthState}";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            RepositoryHealthStatus = $"Mirror placement apply failed: {ex.Message}";
        }
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
                var mirrorSet = (status.Configuration.MirrorSet
                    ?? MirrorSetConfiguration.FromLegacyPath(status.Configuration.MirrorPath)).Normalise();
                MirrorPlacementProfile = mirrorSet.PlacementPolicy.Profile;
                MinimumMirrorCopies = mirrorSet.PlacementPolicy.MinimumMirrorCopies;
                ReplaceMirrorNodes(mirrorSet.Nodes);
                currentRetentionPolicy = status.Configuration.RetentionPolicy;
                currentCaptureCadencePolicy = status.Configuration.CaptureCadencePolicy;
                currentCodecPolicy = status.Configuration.CodecPolicy;
                currentWorkloadPolicy = status.Configuration.WorkloadPolicy ?? WorkloadPolicyConfiguration.CreateDefault();
                currentExclusionRules = status.Configuration.ExclusionRules ?? [];
                FileBrowser.DefaultWorkloadPreset = currentWorkloadPolicy.DefaultPreset;
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
            MirrorHealth = BuildMirrorHealth(status.MirrorWarnings ?? [], status.Configuration);
            CaptureHealth = BuildCaptureHealth(status.CaptureStatuses ?? []);
            ApplyDeviceIdentity(status);
            ApplySyncStatus(status.Sync);
            ApplyRepositoryHealth(status.RepositoryHealth);
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
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: watchedFolders,
            RetentionPolicy: currentRetentionPolicy,
            CaptureCadencePolicy: currentCaptureCadencePolicy,
            CodecPolicy: currentCodecPolicy,
            SelectionRules: selectionRules,
            ExclusionRules: currentExclusionRules,
            WorkloadPolicy: currentWorkloadPolicy,
            MirrorSet: new MirrorSetConfiguration(MirrorNodes
                .Select(node => new MirrorNodeConfiguration(
                    node.Id,
                    node.Label,
                    node.Path,
                    node.IsEnabled,
                    node.CapacityBudgetBytes,
                    node.Priority))
                .ToArray(),
                new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile, MinimumMirrorCopies)));
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
                    folder.IsEnabled,
                    WorkloadPreset: WorkloadPolicyPresetId.GeneralPurpose));
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
                    folder.IsEnabled,
                    WorkloadPreset: WorkloadPolicyPresetId.GeneralPurpose));
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

    private void ApplyDeviceIdentity(FluxVaultServiceStatus status)
    {
        var identity = status.DeviceIdentity ?? BuildDeviceIdentityStatus(status.Configuration.Sync);
        DeviceIdentityStatus = $"Device: {identity.DisplayName} ({identity.DeviceId})";
        var trusted = identity.TrustedDevices.Count(device =>
            device.TrustState is DeviceTrustState.Local or DeviceTrustState.Trusted);
        var blocked = identity.TrustedDevices.Count(device => device.TrustState == DeviceTrustState.Blocked);
        TrustedDeviceSummary = $"Trusted devices: {trusted} trusted, {blocked} blocked";
    }

    private static DeviceIdentityRuntimeStatus BuildDeviceIdentityStatus(SyncConfiguration sync)
    {
        var normalised = (sync ?? SyncConfiguration.CreateDefault(AppContext.BaseDirectory))
            .Normalise(AppContext.BaseDirectory);
        return new DeviceIdentityRuntimeStatus(
            normalised.LocalDevice.DeviceId,
            normalised.LocalDevice.DisplayName,
            normalised.TrustedDevices
                .Select(device => new TrustedDeviceRuntimeStatus(
                    device.DeviceId,
                    device.DisplayName,
                    device.TrustState,
                    device.TrustedAtUtc,
                    device.LastSeenAtUtc))
                .ToArray());
    }

    private void ApplySyncStatus(SyncRuntimeStatus? status)
    {
        SyncPeerSummary = status is null
            ? "Sync: waiting"
            : $"Sync: {status.PeerHeads.Count} peer head(s), {status.Cursors.Count} cursor(s)";
    }

    private static string BuildMirrorHealth(IReadOnlyList<string> warnings, FluxVaultConfiguration configuration)
    {
        if (warnings.Count > 0)
        {
            return $"Mirror: {warnings.Count} warning(s)";
        }

        var mirrorSet = configuration.MirrorSet ?? MirrorSetConfiguration.FromLegacyPath(configuration.MirrorPath);
        var total = mirrorSet.Nodes.Count;
        if (total == 0)
        {
            return "Mirror: local only";
        }

        var enabled = mirrorSet.Nodes.Count(node => node.IsEnabled);
        return $"Mirror: {enabled} of {total} enabled";
    }

    private void ApplyRepositoryHealth(RepositoryHealthSnapshot? health)
    {
        if (health is null)
        {
            RepositoryHealthStatus = "Repository health: waiting";
            RepositoryHealthRows.Clear();
            RepositoryHealthRows.Add(new RepositoryHealthRow("Repository integrity", "Waiting for scrub", "No scrub report has been recorded yet."));
            RepositoryHealthRows.Add(new RepositoryHealthRow("Mirror placement", "Waiting for placement preview", "Run mirror placement preview to see required mirror copy/delete actions."));
            RepositoryHealthRows.Add(new RepositoryHealthRow("Mirror repair", "Waiting for repair preview", "Run a mirror repair preview or repair action to get per-node mirror health."));
            RepositoryHealthRows.Add(new RepositoryHealthRow("Restore rehearsal", "Waiting for rehearsal", "No restore rehearsal has been recorded yet."));
            RepositoryHealthRows.Add(new RepositoryHealthRow("USN state", UsnHealth, UsnHealthToolTip));
            RepositoryHealthRows.Add(new RepositoryHealthRow("Blocked files", CaptureHealth, "Blocked and pending capture state is shown in Activity."));
            ApplyMirrorRepairToNodes(null);
            ApplyMirrorPlacementToNodes(null);
            return;
        }

        currentScrubReport = health.LastScrub;
        currentRestoreRehearsalReport = health.LastRestoreRehearsal;
        currentMirrorRepairReport = health.LastMirrorRepair;
        currentMirrorRebalanceReport = health.LastMirrorRebalance;
        RepositoryHealthStatus = $"Repository health: {health.OverallState} - {health.Summary}";
        RepositoryHealthRows.Clear();
        RepositoryHealthRows.Add(BuildScrubRow(health.LastScrub));
        RepositoryHealthRows.Add(BuildMirrorRow(health.LastScrub));
        RepositoryHealthRows.Add(BuildMirrorRebalanceRow(health.LastMirrorRebalance));
        RepositoryHealthRows.Add(BuildMirrorRepairRow(health.LastMirrorRepair));
        RepositoryHealthRows.Add(BuildRehearsalRow(health.LastRestoreRehearsal));
        RepositoryHealthRows.Add(new RepositoryHealthRow("USN state", UsnHealth, UsnHealthToolTip));
        RepositoryHealthRows.Add(new RepositoryHealthRow("Blocked files", CaptureHealth, "Blocked and pending capture state is shown in Activity."));
        ApplyMirrorRepairToNodes(health.LastMirrorRepair);
        ApplyMirrorPlacementToNodes(health.LastMirrorRebalance);
    }

    private static RepositoryHealthRow BuildScrubRow(RepositoryScrubReport? report)
    {
        if (report is null)
        {
            return new RepositoryHealthRow("Repository integrity", "Waiting for scrub", "No scrub report has been recorded yet.");
        }

        var status = report.RepairedIssueCount > 0
            ? $"{report.HealthState} - repaired {report.RepairedIssueCount} issue(s)"
            : $"{report.HealthState} - {report.IssueCount} issue(s)";
        var detail = $"Checked {report.CheckedChunkCount} chunk(s) across {report.ManifestCount} manifest(s) at {report.CompletedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}.";
        return new RepositoryHealthRow("Repository integrity", status, detail);
    }

    private static RepositoryHealthRow BuildMirrorRow(RepositoryScrubReport? report)
    {
        if (report is null)
        {
            return new RepositoryHealthRow("Mirror state", "Waiting for scrub", "Mirror drift is checked during repository scrub.");
        }

        var mirrorIssues = report.Issues.Count(issue => issue.Kind == RepositoryScrubIssueKind.MirrorDrift);
        var repairedMirror = report.Issues.Count(issue => issue.RepairAction == RepositoryRepairAction.RepairedMirrorFromPrimary);
        var status = mirrorIssues == 0
            ? "Healthy"
            : $"Repaired {repairedMirror} mirror issue(s)";
        return new RepositoryHealthRow("Mirror state", status, "Mirror artefacts are compared against referenced primary repository artefacts.");
    }

    private static RepositoryHealthRow BuildMirrorRepairRow(MirrorRepairReport? report)
    {
        if (report is null)
        {
            return new RepositoryHealthRow("Mirror repair", "Waiting for repair preview", "Run a mirror repair preview or repair action to get per-node mirror health.");
        }

        var status = report.RepairedIssueCount > 0
            ? $"{report.HealthState} - repaired {report.RepairedIssueCount} issue(s)"
            : $"{report.HealthState} - {report.IssueCount} issue(s)";
        var nodeSummary = string.Join("; ", report.Nodes.Select(node => $"{node.Label}: {node.HealthState}, {node.IssueCount} issue(s)"));
        var detail = $"{(report.IsPreview ? "Preview" : "Repair")} completed at {report.CompletedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}. {nodeSummary}";
        return new RepositoryHealthRow("Mirror repair", status, detail);
    }

    private static RepositoryHealthRow BuildMirrorRebalanceRow(MirrorRebalancePreviewReport? report)
    {
        if (report is null)
        {
            return new RepositoryHealthRow("Mirror placement", "Waiting for placement preview", "Run mirror placement preview to see required mirror copy/delete actions.");
        }

        var status = $"{report.HealthState} - {report.ActionCount} action(s)";
        var detail = $"Checked {report.CheckedChunkCount} chunk(s) at {report.CompletedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}. Copy {FormatBytes(report.EstimatedCopyBytes)}, delete {FormatBytes(report.EstimatedDeleteBytes)}.";
        var name = report.Operation == MirrorRebalanceOperation.Drain ? "Mirror drain" : "Mirror placement";
        return new RepositoryHealthRow(name, status, detail);
    }

    private void ApplyMirrorRepairToNodes(MirrorRepairReport? report)
    {
        foreach (var node in MirrorNodes)
        {
            var nodeReport = report?.Nodes.FirstOrDefault(reportNode =>
                string.Equals(reportNode.NodeId, node.Id, StringComparison.OrdinalIgnoreCase));
            if (nodeReport is null)
            {
                node.RepairStatus = "No repair preview";
                node.RepairDetail = "Run mirror repair preview to check this node.";
                continue;
            }

            node.RepairStatus = nodeReport.RepairedIssueCount > 0
                ? $"{nodeReport.HealthState} - repaired {nodeReport.RepairedIssueCount}"
                : $"{nodeReport.HealthState} - {nodeReport.IssueCount} issue(s)";
            node.RepairDetail = nodeReport.IssueCount == 0
                ? "No mirror repair issues reported."
                : $"{nodeReport.Label} reported {nodeReport.IssueCount} issue(s), repaired {nodeReport.RepairedIssueCount}.";
        }
    }

    private void ApplyMirrorPlacementToNodes(MirrorRebalancePreviewReport? report)
    {
        var isDrain = report?.Operation == MirrorRebalanceOperation.Drain;
        foreach (var node in MirrorNodes)
        {
            var nodeReport = report?.Nodes.FirstOrDefault(reportNode =>
                string.Equals(reportNode.NodeId, node.Id, StringComparison.OrdinalIgnoreCase));
            if (nodeReport is null)
            {
                node.PlacementStatus = isDrain ? "No drain preview" : "No placement preview";
                node.PlacementDetail = isDrain
                    ? "Run mirror drain preview to check this node."
                    : "Run mirror placement preview to check this node.";
                continue;
            }

            node.PlacementStatus = $"{nodeReport.HealthState} - {nodeReport.ActionCount} action(s)";
            node.PlacementDetail = nodeReport.ActionCount == 0
                ? isDrain ? "No mirror drain movement is required." : "No mirror placement movement is required."
                : $"{nodeReport.Label}: copy {FormatBytes(nodeReport.EstimatedCopyBytes)}, delete {FormatBytes(nodeReport.EstimatedDeleteBytes)}.";
        }
    }

    private static RepositoryHealthRow BuildRehearsalRow(RestoreRehearsalReport? report)
    {
        if (report is null)
        {
            return new RepositoryHealthRow("Restore rehearsal", "Waiting for rehearsal", "No restore rehearsal has been recorded yet.");
        }

        var status = report.FailedVersionCount == 0
            ? $"{report.HealthState} - passed {report.RehearsedVersionCount} version(s)"
            : $"{report.HealthState} - failed {report.FailedVersionCount} version(s)";
        var detail = $"Requested newest {report.RequestedVersionCount} version(s); completed at {report.CompletedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}.";
        return new RepositoryHealthRow("Restore rehearsal", status, detail);
    }

    private static RepositoryHealthState CombineHealth(params RepositoryHealthState?[] states)
    {
        var actual = states.Where(state => state is not null).Select(state => state!.Value).ToArray();
        if (actual.Length == 0)
        {
            return RepositoryHealthState.Warning;
        }

        if (actual.Contains(RepositoryHealthState.Critical))
        {
            return RepositoryHealthState.Critical;
        }

        return actual.Contains(RepositoryHealthState.Warning)
            ? RepositoryHealthState.Warning
            : RepositoryHealthState.Healthy;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
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

public sealed partial class MirrorNodeRow : ObservableObject
{
    [ObservableProperty]
    private string id;

    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private string path;

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private long? capacityBudgetBytes;

    [ObservableProperty]
    private int priority = 100;

    [ObservableProperty]
    private string repairStatus = "No repair preview";

    [ObservableProperty]
    private string repairDetail = "Run mirror repair preview to check this node.";

    [ObservableProperty]
    private string placementStatus = "No placement preview";

    [ObservableProperty]
    private string placementDetail = "Run mirror placement preview to check this node.";

    public MirrorNodeRow(
        string id,
        string label,
        string path,
        bool isEnabled,
        long? capacityBudgetBytes = null,
        int priority = 100)
    {
        this.id = id;
        this.label = label;
        this.path = path;
        this.isEnabled = isEnabled;
        this.capacityBudgetBytes = capacityBudgetBytes;
        this.priority = priority;
    }

    public string Status => IsEnabled ? "Enabled" : "Disabled";

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(Status));
    }

    partial void OnPriorityChanged(int value)
    {
        if (value < 1)
        {
            Priority = 1;
        }
    }
}

public sealed record RepositoryHealthRow(
    string Name,
    string Status,
    string Detail);
