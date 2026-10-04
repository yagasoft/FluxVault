using System.IO;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Sync;
using FluxVault.Abstractions.Security;
using FluxVault.App.Services;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;
using FluxVault.Core.Policies;
using FluxVault.Core.Security;
using WinForms = System.Windows.Forms;

namespace FluxVault.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const int MirrorsWorkspaceIndex = 3;
    private const int PerformanceWorkspaceIndex = 5;
    private static readonly JsonSerializerOptions ConfigurationFingerprintJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IFluxVaultServiceClient client;
    private readonly IBackupOperationStore backupOperationStore;
    private readonly IProtectionSaveOperationStore saveOperationStore;
    private readonly FluxVault.Windows.Security.WindowsUserPreviewCache previewCache;
    private readonly IFluxVaultWindowsServiceController windowsServiceController;
    private readonly IRestoreDestinationPicker restoreDestinationPicker;
    private readonly IRestoreOverwriteConfirmation restoreOverwriteConfirmation;
    private readonly IProtectionRemovalConfirmation protectionRemovalConfirmation;
    private readonly IVersionPreviewLauncher versionPreviewLauncher;
    private readonly IMirrorNodeDialogService mirrorNodeDialogService;
    private readonly TimeSpan autoRefreshInterval;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CancellationTokenSource? autoRefreshCancellation;
    private Task? autoRefreshTask;
    private bool isApplyingStatus;
    private bool hasLocalConfigurationChanges;
    private FluxVaultConfiguration? acceptedConfiguration;
    private VaultId? acceptedVaultId;
    private long? acceptedConfigurationRevision;
    private long configurationEditGeneration;
    private bool requiresPurgeReconciliation;
    private bool requiresSaveStatusCheck;
    private FluxVaultConfiguration? lastDispatchedSaveConfiguration;
    private PendingBackupOperation? unconfirmedBackup;

    public Guid? UnconfirmedBackupOperationId => unconfirmedBackup?.OperationId;
    public bool HasUnconfirmedBackup => unconfirmedBackup is not null;
    private PendingProtectionSave? unconfirmedProtectionSave;
    private bool saveRecordBlocked;
    private bool saveConfirmedForReview;
    public bool HasUnconfirmedProtectionSave => unconfirmedProtectionSave is not null || saveRecordBlocked;

    [ObservableProperty]
    private string backupOutcomeMessage = string.Empty;
    private bool isOptionsEditing;
    private bool requiresOptionsReconciliation;
    private string? lastAppliedConfigurationFingerprint;
    private (VaultId? RepositoryId, string RepositoryPath, MetadataStoreConfiguration MetadataStore)? lastAppliedVersionInventoryIdentity;
    private readonly Dictionary<string, MirrorMigrationState> pendingMirrorMigrations = new(StringComparer.OrdinalIgnoreCase);
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
    private ProtectionSaveState protectionSaveState;

    [ObservableProperty]
    private string protectionSaveMessage = string.Empty;

    [ObservableProperty]
    private bool isProtectionSaveBusy;

    public bool CanSaveProtection => !IsProtectionSaveBusy && !isOptionsEditing && !requiresOptionsReconciliation && !HasUnconfirmedProtectionSave;

    public bool CanDiscardConfigurationChanges => !IsProtectionSaveBusy && !isOptionsEditing && (!HasUnconfirmedProtectionSave || saveConfirmedForReview);

    public bool CanOpenOptions => CanSaveProtection && !hasLocalConfigurationChanges && acceptedConfiguration is not null;

    public string OptionsEntryToolTip => CanOpenOptions
        ? "Open retention, maintenance, capture, compression, and Explorer settings."
        : "Load configuration, then save or discard pending protection changes before opening Options.";

    private void NotifyOptionsEntryChanged()
    {
        OnPropertyChanged(nameof(CanOpenOptions));
        OnPropertyChanged(nameof(OptionsEntryToolTip));
    }

    internal bool TryBeginOptionsEditing()
    {
        if (!TryLoadPendingProtectionSave()) return false;
        if (!CanOpenOptions) return false;
        isOptionsEditing = true;
        NotifyConfigurationCommandAvailability();
        return true;
    }

    internal async Task EndOptionsEditingAsync()
    {
        requiresOptionsReconciliation = true;
        try
        {
            var applied = await RefreshAsync(isAutomatic: false, forceConfigurationReload: true,
                expectedEditGeneration: configurationEditGeneration).ConfigureAwait(true);
            if (!applied)
            {
                ProtectionSaveMessage = "Options could not be reloaded. Protection saving is unavailable until saved settings are reloaded. Your pending edits are kept; refresh, or review them and use Discard changes to reload.";
                SetServiceStatus($"Service connection: {ProtectionSaveMessage}");
            }
        }
        finally
        {
            isOptionsEditing = false;
            NotifyConfigurationCommandAvailability();
        }
    }

    public bool HasProtectionSaveMessage => !string.IsNullOrWhiteSpace(ProtectionSaveMessage);

    partial void OnProtectionSaveMessageChanged(string value) => OnPropertyChanged(nameof(HasProtectionSaveMessage));

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
    private bool isPreviewBusy;

    [ObservableProperty]
    private string previewStatus = string.Empty;

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
    private string performanceWorkspaceStatus = "Performance workspace: waiting";

    [ObservableProperty]
    private string shellIntegrationStatus = "Shell integration: waiting";

    [ObservableProperty]
    private string directCloudStatus = "Direct cloud: waiting";

    [ObservableProperty]
    private string securityPostureStatus = "Security: waiting";

    [ObservableProperty]
    private string fleetPolicyStatus = "Fleet: waiting";

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

    [ObservableProperty]
    private string performanceStatusText = "Performance: waiting";

    public MainWindowViewModel()
        : this(
            new NamedPipeFluxVaultClient(FluxVault.Windows.Security.WindowsFluxVaultPipeClientFactory.ForService()),
            TimeSpan.FromSeconds(5),
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new WindowsFluxVaultServiceController(),
            new SaveFileRestoreDestinationPicker(),
            new MessageBoxRestoreOverwriteConfirmation(),
            new WpfMirrorNodeDialogService(),
            backupOperationStore: new FileBackupOperationStore(), saveOperationStore: new FileProtectionSaveOperationStore())
    {
    }

    public MainWindowViewModel(IFluxVaultServiceClient client, TimeSpan autoRefreshInterval)
        : this(
            client,
            autoRefreshInterval,
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new AssumedRunningWindowsServiceController(),
            new SaveFileRestoreDestinationPicker(),
            new MessageBoxRestoreOverwriteConfirmation(),
            new WpfMirrorNodeDialogService())
    {
    }

    public MainWindowViewModel(
        IFluxVaultServiceClient client,
        TimeSpan autoRefreshInterval,
        IMirrorNodeDialogService mirrorNodeDialogService)
        : this(
            client,
            autoRefreshInterval,
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new AssumedRunningWindowsServiceController(),
            new SaveFileRestoreDestinationPicker(),
            new MessageBoxRestoreOverwriteConfirmation(),
            mirrorNodeDialogService)
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
            restoreOverwriteConfirmation,
            new WpfMirrorNodeDialogService())
    {
    }

    public MainWindowViewModel(
        IFluxVaultServiceClient client,
        TimeSpan autoRefreshInterval,
        IRestoreDestinationPicker restoreDestinationPicker,
        IRestoreOverwriteConfirmation restoreOverwriteConfirmation,
        IVersionPreviewLauncher versionPreviewLauncher)
        : this(
            client,
            autoRefreshInterval,
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new AssumedRunningWindowsServiceController(),
            restoreDestinationPicker,
            restoreOverwriteConfirmation,
            new WpfMirrorNodeDialogService(),
            versionPreviewLauncher)
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
            new MessageBoxRestoreOverwriteConfirmation(),
            new WpfMirrorNodeDialogService())
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
            new MessageBoxRestoreOverwriteConfirmation(),
            new WpfMirrorNodeDialogService())
    {
    }

    public MainWindowViewModel(
        IFluxVaultServiceClient client,
        TimeSpan autoRefreshInterval,
        FileBrowserViewModel fileBrowser,
        IFluxVaultWindowsServiceController windowsServiceController,
        IRestoreDestinationPicker restoreDestinationPicker,
        IRestoreOverwriteConfirmation restoreOverwriteConfirmation,
        IMirrorNodeDialogService? mirrorNodeDialogService = null,
        IVersionPreviewLauncher? versionPreviewLauncher = null,
        IProtectionRemovalConfirmation? protectionRemovalConfirmation = null,
        IBackupOperationStore? backupOperationStore = null,
        FluxVault.Windows.Security.WindowsUserPreviewCache? previewCache = null,
        IProtectionSaveOperationStore? saveOperationStore = null)
    {
        this.client = client;
        this.backupOperationStore = backupOperationStore ?? new MemoryBackupOperationStore();
        this.saveOperationStore = saveOperationStore ?? new MemoryProtectionSaveOperationStore();
        this.previewCache = previewCache ?? new();
        this.windowsServiceController = windowsServiceController;
        this.restoreDestinationPicker = restoreDestinationPicker;
        this.restoreOverwriteConfirmation = restoreOverwriteConfirmation;
        this.protectionRemovalConfirmation = protectionRemovalConfirmation ?? new MessageBoxProtectionRemovalConfirmation();
        this.versionPreviewLauncher = versionPreviewLauncher ?? new ShellVersionPreviewLauncher();
        this.mirrorNodeDialogService = mirrorNodeDialogService ?? new WpfMirrorNodeDialogService();
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

    public ObservableCollection<PerformanceMetricRow> PerformanceMetricRows { get; } = [];

    public ObservableCollection<PerformanceLoopRow> PerformanceLoopRows { get; } = [];

    public ObservableCollection<PerformanceBackgroundRow> PerformanceBackgroundRows { get; } = [];

    public ObservableCollection<PerformanceSampleRow> PerformanceSampleRows { get; } = [];

    public FileBrowserViewModel FileBrowser { get; }

    public event EventHandler<VersionInventoryRequestedEventArgs>? VersionInventoryRequested;

    public IReadOnlyList<MirrorPlacementProfile> MirrorPlacementProfiles { get; } =
    [
        MirrorPlacementProfile.FullCopy,
        MirrorPlacementProfile.CapacityBalanced,
        MirrorPlacementProfile.Redundant
    ];

    public IReadOnlyList<MirrorPlacementProfileOption> MirrorPlacementProfileOptions { get; } =
    [
        new(MirrorPlacementProfile.FullCopy, "Full copy"),
        new(MirrorPlacementProfile.CapacityBalanced, "Capacity balanced"),
        new(MirrorPlacementProfile.Redundant, "Redundant")
    ];

    public bool HasSelectedMirror => SelectedMirrorNode is not null;

    public bool HasSelectedVersion => SelectedVersion is not null;

    public bool CanEnableSelectedMirror => SelectedMirrorNode is { IsEnabled: false };

    public bool CanDisableSelectedMirror => SelectedMirrorNode is { IsEnabled: true };

    public bool CanShowSelectedMirrorRepairActions => SelectedMirrorNode is { IsEnabled: true };

    public bool CanShowSelectedMirrorDrainActions => SelectedMirrorNode is { IsEnabled: true } && EnabledMirrorCount >= 2;

    public bool CanShowGlobalMirrorRepairActions => EnabledMirrorCount > 0;

    public bool CanShowMirrorPlacementActions => EnabledMirrorCount > 0 && HasRequiredMirrorCountForPlacement;

    public string MirrorActionStatus
    {
        get
        {
            if (MirrorNodes.Count == 0)
            {
                return "Add a mirror to enable placement, repair, and drain actions.";
            }

            if (!HasRequiredMirrorCountForPlacement)
            {
                return $"Enable at least {MinimumMirrorCopies} mirrors to satisfy the redundant placement policy.";
            }

            if (SelectedMirrorNode is null)
            {
                return "Select a mirror to edit, enable, disable, repair, or drain it.";
            }

            if (!SelectedMirrorNode.IsEnabled)
            {
                return "Enable the selected mirror before repair or drain actions are available.";
            }

            if (!CanShowSelectedMirrorDrainActions)
            {
                return "Drain needs the selected mirror plus at least one other enabled mirror.";
            }

            return "Selected mirror actions are available.";
        }
    }

    private int EnabledMirrorCount => MirrorNodes.Count(node => node.IsEnabled);

    private bool HasRequiredMirrorCountForPlacement =>
        MirrorPlacementProfile != MirrorPlacementProfile.Redundant || EnabledMirrorCount >= MinimumMirrorCopies;

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
                await RefreshAsync(isAutomatic: false).ConfigureAwait(true);
                await ShowVersionsForPathAsync(request.Path).ConfigureAwait(true);
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
                try
                {
                    await RefreshAsync(isAutomatic: true, cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException
                                             or TimeoutException
                                             or UnauthorizedAccessException
                                             or InvalidOperationException
                                             or ArgumentException)
                {
                    SetServiceUnavailable(ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> RefreshAsync(
        bool isAutomatic,
        CancellationToken cancellationToken = default,
        bool forceConfigurationReload = false,
        long? expectedEditGeneration = null,
        bool validateDiscardReview = false,
        PendingProtectionSave? discardReview = null)
    {
        var reconcileOptions = requiresOptionsReconciliation;
        try
        {
            if (!await refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
            {
                return false;
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
                    return false;
                }

                var response = await SendBoundAsync(
                        FluxVaultIpcRequest.GetStatus(
                            statusDetailLevel: isAutomatic
                                ? FluxVaultStatusDetailLevel.Fast
                                : FluxVaultStatusDetailLevel.Full),
                        cancellationToken)
                    .ConfigureAwait(true);
                if (isAutomatic && response.Success && response.Status is { HasVersionInventory: false })
                {
                    response = await SendBoundAsync(
                            FluxVaultIpcRequest.GetStatus(
                                statusDetailLevel: FluxVaultStatusDetailLevel.Full),
                            cancellationToken)
                        .ConfigureAwait(true);
                }
                if (!response.Success || response.Status is null)
                {
                    SetServiceStatus($"Service connection: unavailable ({response.ErrorMessage ?? "no status returned"})");
                    SetServiceConnectionWarning(response.ErrorMessage ?? "The dashboard cannot connect to the FluxVault service.");
                    return false;
                }

                if (!response.Status.HasVersionInventory)
                {
                    SetServiceStatus("Service connection: running - repository inventory unavailable; previous versions retained");
                    SetServiceConnectionWarning("The service has not supplied a repository version inventory.");
                    return false;
                }

                var discardGenerationMatches = expectedEditGeneration is null || expectedEditGeneration == configurationEditGeneration;
                if (validateDiscardReview && !ValidateDiscardReview(discardReview)) return false;
                var appliedConfiguration = ApplyStatus(
                    response.Status,
                    preserveLocalConfiguration: hasLocalConfigurationChanges && (!forceConfigurationReload || !discardGenerationMatches),
                    isAutomatic,
                    forceConfigurationReload,
                    reconcileOptions, response.VaultId, response.VaultRevision);
                TryLoadPendingBackupRecord();
                if (!validateDiscardReview) TryLoadPendingProtectionSave();
                if (SelectedWorkspaceIndex == PerformanceWorkspaceIndex && !validateDiscardReview)
                {
                    await RefreshPerformanceAsync(cancellationToken).ConfigureAwait(true);
                }
                return appliedConfiguration;
            }
            finally
            {
                refreshGate.Release();
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            SetServiceUnavailable(ex);
            return false;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            SetServiceUnavailable(ex);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            SetServiceUnavailable(ex);
            return false;
        }
    }

    private async Task RefreshPerformanceAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendBoundAsync(FluxVaultIpcRequest.GetPerformance(), cancellationToken)
            .ConfigureAwait(true);
        if (!response.Success || response.Performance is null)
        {
            PerformanceStatusText = $"Performance: unavailable ({response.ErrorMessage ?? "no telemetry returned"})";
            return;
        }

        ApplyPerformance(response.Performance);
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

    partial void OnSelectedMirrorNodeChanged(MirrorNodeRow? value)
    {
        RefreshMirrorActionState();
    }

    partial void OnSelectedVersionChanged(VersionRow? value)
    {
        RestoreSelectedCommand.NotifyCanExecuteChanged();
        OpenSelectedVersionPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedWorkspaceIndexChanged(int value)
    {
        if (value == PerformanceWorkspaceIndex)
        {
            _ = RefreshAsync(isAutomatic: true);
        }
    }

    partial void OnIsPreviewBusyChanged(bool value)
    {
        OpenSelectedVersionPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsProtectionSaveBusyChanged(bool value)
        => NotifyConfigurationCommandAvailability();

    private void NotifyConfigurationCommandAvailability()
    {
        NotifyOptionsEntryChanged();
        OnPropertyChanged(nameof(CanSaveProtection));
        OnPropertyChanged(nameof(CanDiscardConfigurationChanges));
        SaveConfigurationCommand.NotifyCanExecuteChanged();
        RunBackupNowCommand.NotifyCanExecuteChanged();
        DiscardConfigurationChangesCommand.NotifyCanExecuteChanged();
    }

    private void MarkConfigurationDirty()
    {
        if (!isApplyingStatus)
        {
            hasLocalConfigurationChanges = true;
            configurationEditGeneration++;
            NotifyOptionsEntryChanged();
        }
    }

    private async Task<ProtectionSaveOutcome> SaveConfigurationCoreAsync(bool refreshAfterSave = true, string? successStatus = null)
    {
        var vaultId = acceptedVaultId;
        var generation = configurationEditGeneration;
        IReadOnlyList<RepositoryPurgeScope> dispatchedRemovedSelections = [];
        if (!TryLoadPendingProtectionSave() || HasUnconfirmedProtectionSave)
            return new ProtectionSaveOutcome(ProtectionSaveState.Unknown, vaultId, generation, acceptedConfigurationRevision);
        if (!CanSaveProtection)
        {
            return new ProtectionSaveOutcome(ProtectionSaveState.Failed, vaultId, generation, acceptedConfigurationRevision);
        }
        IsProtectionSaveBusy = true;
        try
        {
            if (acceptedConfiguration is null || vaultId is null || acceptedConfigurationRevision is not > 0)
            {
                return CompleteProtectionSave(ProtectionSaveState.Failed, vaultId, generation,
                    "Save failed: load the vault's verified configuration first. Your changes are kept. Backup has not started for this action.");
            }
            if (requiresPurgeReconciliation)
            {
                return CompleteProtectionSave(ProtectionSaveState.Failed, vaultId, generation,
                    "Save stopped: the previous purge needs review. Your changes are kept. Reload saved settings using Discard changes before saving again. Backup has not started for this action.");
            }
            if (requiresSaveStatusCheck)
            {
                return CompleteProtectionSave(ProtectionSaveState.Unknown, vaultId, generation,
                    "Save could not be confirmed. Refresh to check the saved settings before retrying. Your changes are kept. Backup has not started for this action.");
            }
            var removedSelections = FileBrowser.GetRemovedSelectionPurgeScopes();
            if (removedSelections.Count > 0 && !protectionRemovalConfirmation.ConfirmPurge(removedSelections))
            {
                return CompleteProtectionSave(ProtectionSaveState.Cancelled, vaultId, generation,
                    "Save cancelled; removed selections were not purged. Your changes are kept. Backup has not started for this action.");
            }

            var pending = new PendingProtectionSave(vaultId.Value.Value, Guid.NewGuid(), acceptedConfigurationRevision.Value,
                BuildConfiguration(), removedSelections.Count > 0, removedSelections, FileBrowser.GetProtectedSelectionPurgeScopes()).Freeze();
            saveOperationStore.Reserve(pending);
            SetPendingProtectionSave(pending);
            var configuration = pending.Configuration;
            lastDispatchedSaveConfiguration = configuration;
            dispatchedRemovedSelections = removedSelections;
            ProtectionSaveState = ProtectionSaveState.Saving;
            ProtectionSaveMessage = "Saving protection changes…";
            var response = await SendBoundAsync(pending.Request)
                .ConfigureAwait(true);
            if (!response.Success)
            {
                if (response.ErrorCode is not (FluxVaultIpcErrorCode.Denied or FluxVaultIpcErrorCode.InvalidRequest or FluxVaultIpcErrorCode.StaleRevision))
                {
                    requiresSaveStatusCheck = true;
                    return CompleteProtectionSave(ProtectionSaveState.Unknown, vaultId, generation,
                        $"Save could not be confirmed ({response.ErrorMessage}). Your changes are kept. Backup has not started for this action.");
                }
                if (!TryClearPendingProtectionSave(pending))
                    return new ProtectionSaveOutcome(ProtectionSaveState.Unknown, vaultId, generation, acceptedConfigurationRevision);
                return CompleteProtectionSave(ProtectionSaveState.Failed, vaultId, generation,
                    $"Save failed ({response.ErrorMessage ?? "no acknowledgement returned"}). Your changes are kept. Backup has not started for this action.");
            }

            acceptedConfiguration = configuration;
            acceptedConfigurationRevision = response.VaultRevision;
            if (response.Purge is { Success: false })
            {
                requiresPurgeReconciliation = true;
                saveConfirmedForReview = true;
                return CompleteProtectionSave(ProtectionSaveState.Failed, vaultId, generation,
                    $"Configuration saved; purge failed ({response.Purge.ErrorMessage ?? "unknown error"}). Backup history may remain. Your pending changes are kept for review. Backup has not started for this action.");
            }
            if (!TryClearPendingProtectionSave(pending))
                return new ProtectionSaveOutcome(ProtectionSaveState.Unknown, vaultId, generation, acceptedConfigurationRevision);
            if (generation != configurationEditGeneration || acceptedVaultId != vaultId)
            {
                FileBrowser.AcknowledgeSelectionRules(configuration.SelectionRules);
                return CompleteProtectionSave(ProtectionSaveState.Saved, vaultId, generation,
                    "Earlier changes saved. Newer edits are kept; save them before backing up. Backup has not started for this action.");
            }

            isApplyingStatus = true;
            try
            {
                FileBrowser.LoadSelectionRules(configuration.SelectionRules);
                hasLocalConfigurationChanges = false;
                NotifyOptionsEntryChanged();
            }
            finally
            {
                isApplyingStatus = false;
            }
            var purgeStatus = response.Purge is null
                ? string.Empty
                : $" Purged {response.Purge.PurgedVersionCount} version(s), {response.Purge.DeletedChunkCount} chunk(s).";
            var outcome = CompleteProtectionSave(ProtectionSaveState.Saved, vaultId, generation,
                successStatus ?? $"Configuration saved.{purgeStatus}");
            if (refreshAfterSave)
            {
                await RefreshAsync().ConfigureAwait(true);
            }
            FileBrowser.RefreshBrowser();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            requiresSaveStatusCheck = true;
            requiresPurgeReconciliation = dispatchedRemovedSelections.Count > 0;
            return CompleteProtectionSave(ProtectionSaveState.Cancelled, vaultId, generation,
                "Save cancelled; any service-side outcome must be checked before retrying. Your changes are kept. Backup has not started for this action.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            requiresSaveStatusCheck = true;
            requiresPurgeReconciliation = dispatchedRemovedSelections.Count > 0;
            return CompleteProtectionSave(ProtectionSaveState.Unknown, vaultId, generation,
                $"Save could not be confirmed; service acknowledgement unavailable ({ex.Message}). Check the original save outcome before retrying. Your changes are kept. Backup has not started for this action.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                     or InvalidDataException
                                     or InvalidOperationException
                                     or ArgumentException)
        {
            if (unconfirmedProtectionSave is not null)
                return CompleteProtectionSave(ProtectionSaveState.Unknown, vaultId, generation,
                    $"Save could not be confirmed ({ex.Message}). Its submitted snapshot and your changes are kept. Check the original save outcome before retrying. Backup has not started for this action.");
            return CompleteProtectionSave(ProtectionSaveState.Failed, vaultId, generation,
                $"Save failed ({ex.Message}). Your changes are kept. Backup has not started for this action.");
        }
        finally
        {
            IsProtectionSaveBusy = false;
        }
    }

    private ProtectionSaveOutcome CompleteProtectionSave(ProtectionSaveState state, VaultId? vaultId, long generation, string message)
    {
        ProtectionSaveState = state;
        ProtectionSaveMessage = message;
        SetServiceStatus($"Service connection: {message}");
        return new ProtectionSaveOutcome(state, vaultId, generation, acceptedConfigurationRevision);
    }

    private sealed record ProtectionSaveOutcome(ProtectionSaveState Kind, VaultId? VaultId, long EditGeneration, long? Revision);

    private void SetPendingProtectionSave(PendingProtectionSave? pending)
    {
        unconfirmedProtectionSave = pending;
        saveRecordBlocked = false;
        saveConfirmedForReview = false;
        OnPropertyChanged(nameof(HasUnconfirmedProtectionSave));
        CheckProtectionSaveOutcomeCommand.NotifyCanExecuteChanged();
        NotifyConfigurationCommandAvailability();
    }

    private bool TryLoadPendingProtectionSave()
    {
        try
        {
            var pending = saveOperationStore.Read();
            var changed = pending is not null && (unconfirmedProtectionSave is null ||
                !FileProtectionSaveOperationStore.Encode(pending).AsSpan().SequenceEqual(FileProtectionSaveOperationStore.Encode(unconfirmedProtectionSave)));
            if (pending is null) { if (unconfirmedProtectionSave is not null || saveRecordBlocked) SetPendingProtectionSave(null); return true; }
            if (changed || saveRecordBlocked) SetPendingProtectionSave(pending);
            requiresSaveStatusCheck = true;
            if (acceptedVaultId?.Value != pending.RepositoryId)
            {
                saveConfirmedForReview = false;
                CompleteProtectionSave(ProtectionSaveState.Unknown, acceptedVaultId, configurationEditGeneration,
                    "The pending save does not match this installation's repository binding. Its record and your edits are kept; no save, receipt check or backup will be sent.");
                return false;
            }
            if (changed && !hasLocalConfigurationChanges)
            {
                RestoreProtectionDraft(pending.Configuration);
                lastDispatchedSaveConfiguration = pending.Configuration;
            }
            if (!saveConfirmedForReview)
                CompleteProtectionSave(ProtectionSaveState.Unknown, acceptedVaultId, configurationEditGeneration,
                    pending.PurgeRemovedSelections
                        ? "The previous save and purge could not be confirmed. Its submitted snapshot and purge scopes are kept. Check the original save outcome before retrying; backup has not started for this action."
                        : "A previous protection save could not be confirmed. Its submitted snapshot is kept. Check the original save outcome before retrying; backup has not started for this action.");
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            saveRecordBlocked = true;
            saveConfirmedForReview = false;
            OnPropertyChanged(nameof(HasUnconfirmedProtectionSave));
            CheckProtectionSaveOutcomeCommand.NotifyCanExecuteChanged();
            NotifyConfigurationCommandAvailability();
            CompleteProtectionSave(ProtectionSaveState.Unknown, acceptedVaultId, configurationEditGeneration,
                $"The pending protection save could not be read. It and your edits are kept; no save or backup will be sent. {exception.Message}");
            return false;
        }
    }

    private void RestoreProtectionDraft(FluxVaultConfiguration draft)
    {
        isApplyingStatus = true;
        try
        {
            RepositoryPath = draft.RepositoryPath;
            var mirrors = (draft.MirrorSet ?? MirrorSetConfiguration.FromLegacyPath(draft.MirrorPath)).Normalise();
            MirrorPlacementProfile = mirrors.PlacementPolicy.Profile;
            MinimumMirrorCopies = mirrors.PlacementPolicy.MinimumMirrorCopies;
            ReplaceMirrorNodes(mirrors.Nodes);
            FileBrowser.LoadSelectionRules(draft.SelectionRules ?? []);
            FileBrowser.AcknowledgeSelectionRules(acceptedConfiguration!.SelectionRules ?? []);
            hasLocalConfigurationChanges = true;
        }
        finally { isApplyingStatus = false; }
    }

    private bool ValidateDiscardReview(PendingProtectionSave? reviewed)
    {
        try
        {
            var current = saveOperationStore.Read();
            if (reviewed is null && current is null || reviewed is not null && current is not null && saveConfirmedForReview &&
                FileProtectionSaveOperationStore.Encode(reviewed).AsSpan().SequenceEqual(FileProtectionSaveOperationStore.Encode(current))) return true;
            if (current is not null) SetPendingProtectionSave(current);
            saveConfirmedForReview = false;
            CompleteProtectionSave(ProtectionSaveState.Unknown, acceptedVaultId, configurationEditGeneration,
                "Discard stopped because the pending save changed. Its record and your edits are kept. Check the original save outcome before continuing.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            saveConfirmedForReview = false;
            TryLoadPendingProtectionSave();
        }
        return false;
    }

    private bool TryClearPendingProtectionSave(PendingProtectionSave pending)
    {
        try
        {
            saveOperationStore.Clear(pending);
            SetPendingProtectionSave(null);
            requiresSaveStatusCheck = false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            CompleteProtectionSave(ProtectionSaveState.Unknown, acceptedVaultId, configurationEditGeneration,
                $"The save outcome was returned, but its pending record could not be cleared. Your edits are kept; check again before retrying. {exception.Message}");
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasUnconfirmedProtectionSave))]
    private async Task CheckProtectionSaveOutcomeAsync()
    {
        if (!TryLoadPendingProtectionSave() || unconfirmedProtectionSave is not { } pending) return;
        try
        {
            var response = await SendBoundAsync(new FluxVaultIpcRequest(FluxVaultIpcCommand.GetOperationStatus,
                null, null, null, null, VaultId: new VaultId(pending.RepositoryId), OperationId: pending.OperationId)).ConfigureAwait(true);
            if (!response.Success || response.ErrorCode is not null || response.VaultId?.Value != pending.RepositoryId ||
                response.OperationId != pending.OperationId || response.VaultRevision != pending.Revision + 1)
            {
                CompleteProtectionSave(ProtectionSaveState.Unknown, acceptedVaultId, configurationEditGeneration,
                    "The original save outcome could not be confirmed. Its snapshot and your edits are kept; no backup has started. Check again later.");
                return;
            }
            var current = await SendBoundAsync(FluxVaultIpcRequest.GetStatus()).ConfigureAwait(true);
            if (current.Success && current.Status is not null)
            {
                ApplyStatus(current.Status, preserveLocalConfiguration: true, repositoryId: current.VaultId, revision: current.VaultRevision);
                acceptedConfiguration = current.Status.Configuration;
                acceptedConfigurationRevision = current.VaultRevision;
            }
            if (response.Purge is { Success: false } || !current.Success || current.Status is null ||
                current.VaultRevision != pending.Revision + 1 ||
                ComputeConfigurationFingerprint(current.Status.Configuration) != ComputeConfigurationFingerprint(pending.Configuration))
            {
                saveConfirmedForReview = true;
                requiresPurgeReconciliation = response.Purge is { Success: false };
                CompleteProtectionSave(ProtectionSaveState.Failed, acceptedVaultId, configurationEditGeneration,
                    response.Purge is { Success: false }
                        ? "Configuration saved, but purge failed. The durable record and your edits are kept. Review the result, then use Discard changes to reload saved settings before continuing. No backup has started."
                        : "The historical save is confirmed, but the current settings differ or could not be verified. The durable record and your edits are kept. Review, then use Discard changes to reload current settings before continuing. No backup has started.");
                NotifyConfigurationCommandAvailability();
                return;
            }
            if (!TryClearPendingProtectionSave(pending)) return;
            FileBrowser.AcknowledgeSelectionRules(pending.Configuration.SelectionRules ?? []);
            hasLocalConfigurationChanges = ComputeConfigurationFingerprint(BuildConfiguration()) != ComputeConfigurationFingerprint(current.Status.Configuration);
            NotifyConfigurationCommandAvailability();
            CompleteProtectionSave(ProtectionSaveState.Saved, acceptedVaultId, configurationEditGeneration,
                "The previous protection save is confirmed. Any newer edits are kept. No backup has started for this check.");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            CompleteProtectionSave(ProtectionSaveState.Unknown, acceptedVaultId, configurationEditGeneration,
                "The original save outcome could not be confirmed. Its snapshot and your edits are kept; no backup has started. Check again later.");
        }
    }

    private async Task<FluxVaultIpcResponse> SendBoundAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
    {
        var mutation = PostgreSqlVaultCatalogue.IsMutation(request.Command);
        var target = request.VaultId ?? acceptedVaultId;
        if (target is null && request.Command != FluxVaultIpcCommand.GetStatus)
            return FluxVaultIpcResponse.Failure("Load the vault's verified configuration before running this command.") with { ErrorCode = FluxVaultIpcErrorCode.Unavailable };
        request = request with { VaultId = target,
            ExpectedVaultRevision = request.ExpectedVaultRevision ?? (mutation ? acceptedConfigurationRevision : null),
            OperationId = request.OperationId ?? (mutation ? Guid.NewGuid() : null) };
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(true);
        if (!response.Success) return response;
        var expectedAcknowledgedRevision = request.ExpectedVaultRevision +
            (request.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.SetVaultAccess ? 1 : 0);
        if (response.VaultId is not { IsValid: true } returnedId || response.VaultRevision is not > 0 ||
            target is not null && returnedId != target || mutation && response.OperationId != request.OperationId ||
            mutation && response.VaultRevision != expectedAcknowledgedRevision ||
            request.Command == FluxVaultIpcCommand.GetStatus && acceptedConfigurationRevision is { } current && response.VaultRevision < current)
            return FluxVaultIpcResponse.Failure(mutation
                ? "The operation acknowledgement did not match this vault. Check its status before retrying."
                : "The service response did not confirm this vault's current binding and revision.") with
            { ErrorCode = mutation ? FluxVaultIpcErrorCode.OutcomeUnknown : FluxVaultIpcErrorCode.Unavailable,
                VaultId = target, OperationId = request.OperationId };
        return response;
    }

    [RelayCommand(CanExecute = nameof(CanDiscardConfigurationChanges))]
    private async Task DiscardConfigurationChangesAsync()
    {
        if (!TryLoadPendingProtectionSave()) return;
        if (!CanDiscardConfigurationChanges) return;
        var reviewed = saveConfirmedForReview ? unconfirmedProtectionSave : null;
        var draft = acceptedConfiguration is null ? null : BuildConfiguration();
        IsProtectionSaveBusy = true;
        try
        {
            var applied = await RefreshAsync(isAutomatic: false, forceConfigurationReload: true,
                expectedEditGeneration: configurationEditGeneration, validateDiscardReview: true, discardReview: reviewed).ConfigureAwait(true);
            if (!applied)
            {
                ProtectionSaveMessage = "Discard did not complete: saved settings could not be reloaded or newer edits arrived. Your pending changes are kept. Refresh and try again.";
                SetServiceStatus($"Service connection: {ProtectionSaveMessage}");
                return;
            }
            if (reviewed is not null && !TryClearPendingProtectionSave(reviewed))
            {
                if (draft is not null) RestoreProtectionDraft(draft);
                return;
            }
            var previousMessage = ProtectionSaveMessage;
            ProtectionSaveState = ProtectionSaveState.Idle;
            ProtectionSaveMessage = string.Empty;
            if (!string.IsNullOrWhiteSpace(previousMessage))
            {
                SetServiceStatus(ServiceStatus.Replace(previousMessage, string.Empty, StringComparison.Ordinal).TrimEnd(' ', '.'));
            }
            FileBrowser.RefreshBrowser();
        }
        finally { IsProtectionSaveBusy = false; }
    }

    private void MarkStatusApplied(bool reconcileOptions)
    {
        hasLocalConfigurationChanges = false;
        requiresPurgeReconciliation = false;
        if (reconcileOptions && requiresOptionsReconciliation)
        {
            requiresOptionsReconciliation = false;
            var previousMessage = ProtectionSaveMessage;
            ProtectionSaveMessage = string.Empty;
            if (!string.IsNullOrWhiteSpace(previousMessage))
            {
                SetServiceStatus(ServiceStatus.Replace(previousMessage, string.Empty, StringComparison.Ordinal).TrimEnd(' ', '.'));
            }
        }
        NotifyConfigurationCommandAvailability();
    }

    private void ReplaceMirrorNodes(IReadOnlyList<MirrorNodeConfiguration> nodes)
    {
        var selectedMirrorNodeId = SelectedMirrorNode?.Id;
        foreach (var row in MirrorNodes)
        {
            row.PropertyChanged -= MirrorNode_PropertyChanged;
        }

        MirrorNodes.Clear();
        foreach (var node in nodes)
        {
            var row = new MirrorNodeRow(node.Id, node.Label, node.Path, node.IsEnabled, node.CapacityBudgetBytes, node.Priority);
            ApplyPendingMirrorMigration(row);
            AddMirrorRow(row);
        }

        SelectedMirrorNode = selectedMirrorNodeId is null
            ? MirrorNodes.FirstOrDefault()
            : MirrorNodes.FirstOrDefault(node => string.Equals(node.Id, selectedMirrorNodeId, StringComparison.OrdinalIgnoreCase))
              ?? MirrorNodes.FirstOrDefault();
        UpdateMirrorSummary();
        ApplyMirrorRepairToNodes(currentMirrorRepairReport);
        ApplyMirrorPlacementToNodes(currentMirrorRebalanceReport);
        RefreshMirrorActionState();
    }

    private void AddMirrorRow(MirrorNodeRow row)
    {
        row.PropertyChanged += MirrorNode_PropertyChanged;
        MirrorNodes.Add(row);
    }

    private void MirrorNode_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MirrorNodeRow.Label)
            or nameof(MirrorNodeRow.Path)
            or nameof(MirrorNodeRow.IsEnabled)
            or nameof(MirrorNodeRow.CapacityBudgetBytes)
            or nameof(MirrorNodeRow.Priority))
        {
            MarkConfigurationDirty();
        }

        UpdateMirrorSummary();
        RefreshMirrorActionState();
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
        MirrorSummary = $"Mirrors: {enabled} of {total} enabled, {FormatMirrorPlacementProfile(MirrorPlacementProfile)} placement";
    }

    partial void OnMirrorPlacementProfileChanged(MirrorPlacementProfile value)
    {
        if (!isApplyingStatus)
        {
            MarkConfigurationDirty();
        }

        UpdateMirrorSummary();
        RefreshMirrorActionState();
    }

    partial void OnMinimumMirrorCopiesChanged(int value)
    {
        if (!isApplyingStatus)
        {
            MarkConfigurationDirty();
        }

        RefreshMirrorActionState();
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
            "General purpose",
            "None",
            CompressionPreference.Zstd,
            "Pending save"));
        NewWatchedFolderPath = string.Empty;
        MarkConfigurationDirty();
    }

    [RelayCommand]
    private void RemoveWatchedFolder()
    {
        if (SelectedWatchedFolder is not null)
        {
            WatchedFolders.Remove(SelectedWatchedFolder);
            MarkConfigurationDirty();
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
        var draft = mirrorNodeDialogService.ShowAddMirrorDialog(new MirrorNodeDraft(
            "New mirror",
            string.Empty,
            IsEnabled: true,
            CapacityBudgetBytes: null,
            Priority: 100));
        if (draft is null)
        {
            return;
        }

        var row = new MirrorNodeRow(
            Guid.NewGuid().ToString("N"),
            draft.Label,
            draft.Path,
            draft.IsEnabled,
            draft.CapacityBudgetBytes,
            draft.Priority)
        {
            MigrationStatus = "Pending save",
            MigrationDetail = "Save mirrors to add this mirror to the service configuration."
        };
        AddMirrorRow(row);
        SelectedMirrorNode = row;
        MarkConfigurationDirty();
        UpdateMirrorSummary();
        RefreshMirrorActionState();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMirror))]
    private void EditMirror()
    {
        if (SelectedMirrorNode is null)
        {
            return;
        }

        var original = ToDraft(SelectedMirrorNode);
        var draft = mirrorNodeDialogService.ShowEditMirrorDialog(original);
        if (draft is null)
        {
            return;
        }

        var migrationChanges = GetMigrationChanges(original, draft).ToArray();
        SelectedMirrorNode.Label = draft.Label;
        SelectedMirrorNode.Path = draft.Path;
        SelectedMirrorNode.IsEnabled = draft.IsEnabled;
        SelectedMirrorNode.CapacityBudgetBytes = draft.CapacityBudgetBytes;
        SelectedMirrorNode.Priority = draft.Priority;
        if (migrationChanges.Length > 0)
        {
            MarkMirrorMigrationPending(SelectedMirrorNode, migrationChanges);
        }

        MarkConfigurationDirty();
        UpdateMirrorSummary();
        RefreshMirrorActionState();
    }

    [RelayCommand(CanExecute = nameof(CanEnableSelectedMirror))]
    private void EnableSelectedMirror()
    {
        if (SelectedMirrorNode is null)
        {
            return;
        }

        SelectedMirrorNode.IsEnabled = true;
        MarkMirrorMigrationPending(SelectedMirrorNode, "enablement");
        MarkConfigurationDirty();
        RefreshMirrorActionState();
    }

    [RelayCommand(CanExecute = nameof(CanDisableSelectedMirror))]
    private void DisableSelectedMirror()
    {
        if (SelectedMirrorNode is null)
        {
            return;
        }

        SelectedMirrorNode.IsEnabled = false;
        MarkMirrorMigrationPending(SelectedMirrorNode, "enablement");
        MarkConfigurationDirty();
        RefreshMirrorActionState();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMirror))]
    private void RemoveMirror()
    {
        if (SelectedMirrorNode is null)
        {
            return;
        }

        pendingMirrorMigrations.Remove(SelectedMirrorNode.Id);
        SelectedMirrorNode.PropertyChanged -= MirrorNode_PropertyChanged;
        MirrorNodes.Remove(SelectedMirrorNode);
        SelectedMirrorNode = MirrorNodes.FirstOrDefault();
        MarkConfigurationDirty();
        UpdateMirrorSummary();
        RefreshMirrorActionState();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMirror))]
    private void BrowseSelectedMirror()
    {
        if (SelectedMirrorNode is null)
        {
            return;
        }

        var newPath = mirrorNodeDialogService.BrowseMirrorPath(SelectedMirrorNode.Path);
        if (!string.Equals(newPath, SelectedMirrorNode.Path, StringComparison.OrdinalIgnoreCase))
        {
            SelectedMirrorNode.Path = newPath;
            MarkMirrorMigrationPending(SelectedMirrorNode, "path");
            MarkConfigurationDirty();
        }
    }

    private void RefreshMirrorActionState()
    {
        OnPropertyChanged(nameof(HasSelectedMirror));
        OnPropertyChanged(nameof(CanEnableSelectedMirror));
        OnPropertyChanged(nameof(CanDisableSelectedMirror));
        OnPropertyChanged(nameof(CanShowSelectedMirrorRepairActions));
        OnPropertyChanged(nameof(CanShowSelectedMirrorDrainActions));
        OnPropertyChanged(nameof(CanShowGlobalMirrorRepairActions));
        OnPropertyChanged(nameof(CanShowMirrorPlacementActions));
        OnPropertyChanged(nameof(MirrorActionStatus));

        EditMirrorCommand.NotifyCanExecuteChanged();
        EnableSelectedMirrorCommand.NotifyCanExecuteChanged();
        DisableSelectedMirrorCommand.NotifyCanExecuteChanged();
        RemoveMirrorCommand.NotifyCanExecuteChanged();
        BrowseSelectedMirrorCommand.NotifyCanExecuteChanged();
        PreviewSelectedMirrorRepairCommand.NotifyCanExecuteChanged();
        RunSelectedMirrorRepairCommand.NotifyCanExecuteChanged();
        PreviewSelectedMirrorDrainCommand.NotifyCanExecuteChanged();
        RunSelectedMirrorDrainCommand.NotifyCanExecuteChanged();
        PreviewMirrorRepairCommand.NotifyCanExecuteChanged();
        RunMirrorRepairCommand.NotifyCanExecuteChanged();
        PreviewMirrorRebalanceCommand.NotifyCanExecuteChanged();
        RunMirrorRebalanceCommand.NotifyCanExecuteChanged();
    }

    private static MirrorNodeDraft ToDraft(MirrorNodeRow row)
    {
        return new MirrorNodeDraft(row.Label, row.Path, row.IsEnabled, row.CapacityBudgetBytes, row.Priority);
    }

    private static IEnumerable<string> GetMigrationChanges(MirrorNodeDraft original, MirrorNodeDraft updated)
    {
        if (!string.Equals(original.Path, updated.Path, StringComparison.OrdinalIgnoreCase))
        {
            yield return "path";
        }

        if (original.IsEnabled != updated.IsEnabled)
        {
            yield return "enablement";
        }

        if (original.CapacityBudgetBytes != updated.CapacityBudgetBytes)
        {
            yield return "capacity";
        }

        if (original.Priority != updated.Priority)
        {
            yield return "priority";
        }
    }

    private void MarkMirrorMigrationPending(MirrorNodeRow row, params string[] changes)
    {
        var changeSummary = string.Join(", ", changes.Distinct(StringComparer.OrdinalIgnoreCase));
        var state = new MirrorMigrationState(
            "Migration pending",
            $"Changed {changeSummary}. Save mirrors, then use preview/apply placement, repair, or drain to reconcile stored mirror data.");
        pendingMirrorMigrations[row.Id] = state;
        ApplyPendingMirrorMigration(row);
    }

    private void ApplyPendingMirrorMigration(MirrorNodeRow row)
    {
        if (pendingMirrorMigrations.TryGetValue(row.Id, out var state))
        {
            row.MigrationStatus = state.Status;
            row.MigrationDetail = state.Detail;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveProtection))]
    private async Task SaveConfigurationAsync()
    {
        await SaveConfigurationCoreAsync().ConfigureAwait(true);
    }

    private async Task ApplyExplorerSelectionRequestAsync(string path, bool add)
    {
        if (isOptionsEditing || requiresOptionsReconciliation)
        {
            SetServiceStatus("Finish the Options dialogue before changing protection from Explorer. This request was not applied; retry it after closing Options.");
            return;
        }
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

    [RelayCommand(CanExecute = nameof(CanSaveProtection))]
    private async Task RunBackupNowAsync()
    {
        if (!TryLoadPendingBackupRecord()) return;
        if (HasUnconfirmedBackup)
        {
            SetServiceStatus(BackupOutcomeMessage);
            return;
        }
        var outcome = await SaveConfigurationCoreAsync().ConfigureAwait(true);
        if (outcome.Kind != ProtectionSaveState.Saved
            || outcome.EditGeneration != configurationEditGeneration
            || outcome.VaultId != acceptedVaultId
            || outcome.Revision != acceptedConfigurationRevision)
        {
            return;
        }
        var pending = new PendingBackupOperation(outcome.VaultId!.Value.Value, Guid.NewGuid(), outcome.Revision!.Value);
        try
        {
            backupOperationStore.Reserve(pending);
            SetUnconfirmedBackup(pending);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryLoadPendingBackupRecord();
            BackupOutcomeMessage = $"The backup record could not be saved. No backup was sent. Check the record before retrying. {exception.Message}";
            SetServiceStatus(BackupOutcomeMessage);
            return;
        }
        FluxVaultIpcResponse response;
        try
        {
            response = await SendBoundAsync(FluxVaultIpcRequest.RunBackupNow() with
            { VaultId = outcome.VaultId, ExpectedVaultRevision = outcome.Revision, OperationId = pending.OperationId }).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            response = FluxVaultIpcResponse.Failure(exception.Message) with { ErrorCode = FluxVaultIpcErrorCode.OutcomeUnknown };
        }
        if (!ConfirmsBackupOutcome(response, pending))
        {
            BackupOutcomeMessage = "The backup outcome could not be confirmed. It may have run. Check its outcome before retrying.";
            SetServiceStatus(BackupOutcomeMessage);
            return;
        }
        if (!TryClearPendingBackup(pending)) return;
        BackupOutcomeMessage = response.Success
            ? response.Backup?.Message ?? "Backup request completed."
            : $"Backup failed ({response.ErrorMessage}).";
        SetServiceStatus(response.Backup is null
            ? $"Service connection: backup failed ({response.ErrorMessage})"
            : $"Service connection: {response.Backup.Message}");
        await RefreshAsync().ConfigureAwait(true);
        FileBrowser.RefreshBrowser();
    }

    private void SetUnconfirmedBackup(PendingBackupOperation? operation)
    {
        unconfirmedBackup = operation;
        OnPropertyChanged(nameof(UnconfirmedBackupOperationId));
        OnPropertyChanged(nameof(HasUnconfirmedBackup));
        CheckBackupOutcomeCommand.NotifyCanExecuteChanged();
    }

    private bool TryLoadPendingBackupRecord()
    {
        try
        {
            var pending = backupOperationStore.Read();
            var changed = pending != unconfirmedBackup;
            SetUnconfirmedBackup(pending);
            if (pending is null) return true;
            if (acceptedVaultId?.Value != pending.RepositoryId)
            {
                BackupOutcomeMessage = "The pending backup record does not match this installation's repository binding. It has been preserved; no backup or receipt check will be sent.";
                SetServiceStatus(BackupOutcomeMessage);
                return false;
            }
            if (changed)
                BackupOutcomeMessage = "A previous backup outcome is pending. It may have run. Check its original operation before requesting another backup.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            BackupOutcomeMessage = $"The pending backup record could not be read. It has been preserved; no backup will be sent. {exception.Message}";
            SetServiceStatus(BackupOutcomeMessage);
            return false;
        }
    }

    private static bool ConfirmsBackupOutcome(FluxVaultIpcResponse response, PendingBackupOperation pending) =>
        (response.ErrorCode is null || response is { Success: false, ErrorCode: FluxVaultIpcErrorCode.Unavailable, Backup.Success: false }) &&
        response.VaultId?.Value == pending.RepositoryId &&
        response.OperationId == pending.OperationId && response.VaultRevision == pending.Revision;

    private bool TryClearPendingBackup(PendingBackupOperation pending)
    {
        try
        {
            backupOperationStore.Clear(pending);
            SetUnconfirmedBackup(null);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            BackupOutcomeMessage = $"The backup outcome was returned, but its pending record could not be cleared. Check again before retrying. {exception.Message}";
            SetServiceStatus(BackupOutcomeMessage);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasUnconfirmedBackup))]
    private async Task CheckBackupOutcomeAsync()
    {
        if (!TryLoadPendingBackupRecord() || unconfirmedBackup is not { } pending) return;
        try
        {
            var response = await SendBoundAsync(new FluxVaultIpcRequest(
                FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
                VaultId: new VaultId(pending.RepositoryId), OperationId: pending.OperationId)).ConfigureAwait(true);
            if (!ConfirmsBackupOutcome(response, pending))
            {
                BackupOutcomeMessage = "The backup outcome could not be confirmed. Keep this operation pending and check again when the service is available.";
            }
            else
            {
                if (!TryClearPendingBackup(pending)) return;
                BackupOutcomeMessage = response.Success
                    ? response.Backup?.Message ?? "The previous backup completed."
                    : $"The previous backup failed ({response.ErrorMessage}).";
                await RefreshAsync().ConfigureAwait(true);
            }
            SetServiceStatus(BackupOutcomeMessage);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            BackupOutcomeMessage = "The backup outcome could not be confirmed. The service acknowledgement is unavailable; check again later.";
            SetServiceStatus(BackupOutcomeMessage);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedVersion))]
    private async Task RestoreSelectedAsync()
    {
        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
        {
            SetServiceStatus("Service connection: select a repository version before restoring.");
            return;
        }

        await RestoreVersionAsync(selectedVersion).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RestoreSelectedBrowserItemElsewhereAsync()
    {
        var selection = GetSelectedBrowserRestoreSelection();
        if (selection is null)
        {
            SetServiceStatus("Service connection: select a file or folder in the browser before restoring.");
            return;
        }

        var destination = selection.Value.IsDirectory
            ? restoreDestinationPicker.PickFolderDestination(selection.Value.Path)
            : restoreDestinationPicker.PickDestination(new VersionRow(
                "latest",
                selection.Value.Path,
                string.Empty,
                CaptureConsistency.BestEffort,
                0));
        if (string.IsNullOrWhiteSpace(destination))
        {
            SetServiceStatus("Service connection: restore cancelled.");
            return;
        }

        if (!selection.Value.IsDirectory
            && File.Exists(destination)
            && !restoreOverwriteConfirmation.ConfirmOverwrite(destination))
        {
            SetServiceStatus("Service connection: restore overwrite denied.");
            return;
        }

        await RestoreBrowserSelectionAsync(
            selection.Value.Path,
            selection.Value.IsDirectory,
            RestoreSelectionDestinationMode.Elsewhere,
            destination,
            overwriteConfirmed: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RestoreSelectedBrowserItemToOriginalAsync()
    {
        var selection = GetSelectedBrowserRestoreSelection();
        if (selection is null)
        {
            SetServiceStatus("Service connection: select a file or folder in the browser before restoring.");
            return;
        }

        if (!restoreOverwriteConfirmation.ConfirmOverwrite(selection.Value.Path))
        {
            SetServiceStatus("Service connection: restore overwrite denied.");
            return;
        }

        await RestoreBrowserSelectionAsync(
            selection.Value.Path,
            selection.Value.IsDirectory,
            RestoreSelectionDestinationMode.Original,
            selection.Value.Path,
            overwriteConfirmed: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ShowSelectedBrowserItemVersionsAsync()
    {
        var selection = GetSelectedBrowserRestoreSelection();
        if (selection is null)
        {
            SetServiceStatus("Service connection: select a file or folder in the browser before showing versions.");
            return;
        }

        await ShowVersionsForPathAsync(selection.Value.Path).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanPreviewSelectedVersion))]
    private async Task OpenSelectedVersionPreviewAsync()
    {
        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
        {
            return;
        }

        await OpenVersionPreviewAsync(selectedVersion).ConfigureAwait(true);
    }

    private bool CanPreviewSelectedVersion()
    {
        return !IsPreviewBusy && SelectedVersion is { EntryKind: RepositoryEntryKind.File };
    }

    internal async Task RestoreVersionAsync(VersionRow selectedVersion)
    {
        var destination = selectedVersion.EntryKind == RepositoryEntryKind.Folder
            ? restoreDestinationPicker.PickFolderDestination(selectedVersion.SourcePath)
            : restoreDestinationPicker.PickDestination(selectedVersion);
        if (string.IsNullOrWhiteSpace(destination))
        {
            SetServiceStatus("Service connection: restore cancelled.");
            return;
        }

        if (selectedVersion.EntryKind == RepositoryEntryKind.Folder && (File.Exists(destination) || Directory.Exists(destination)))
        {
            SetServiceStatus("Choose a new recovery folder. Existing folders cannot be merged.");
            return;
        }
        if (File.Exists(destination) && !restoreOverwriteConfirmation.ConfirmOverwrite(destination))
        {
            SetServiceStatus("Service connection: restore overwrite denied.");
            return;
        }

        try
        {
            var response = await SendBoundAsync(FluxVaultIpcRequest.RestoreVersion(selectedVersion.VersionId, destination))
                .ConfigureAwait(true);
            if (!response.Success)
            {
                SetServiceStatus($"Service connection: restore failed ({response.ErrorMessage})");
                return;
            }
            if (response.RestoreResult is not { } result)
            {
                SetServiceStatus("The restore response did not include verification results. Check the destination before retrying.");
                return;
            }
            var message = $"Restored and verified {result.RestoredFileCount} file(s) to {destination}. {string.Join(" ", result.Warnings)}".TrimEnd();
            try { await RefreshAsync().ConfigureAwait(true); }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
            { message += $" Status refresh failed: {exception.Message}"; }
            SetServiceStatus(message);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetServiceStatus($"Service connection: restore failed ({ex.Message})");
        }
    }

    private async Task RestoreBrowserSelectionAsync(
        string sourcePath,
        bool isDirectory,
        RestoreSelectionDestinationMode destinationMode,
        string? destinationPath,
        bool overwriteConfirmed)
    {
        try
        {
            var response = await SendBoundAsync(FluxVaultIpcRequest.RunRestoreSelection(
                    sourcePath,
                    isDirectory,
                    destinationMode,
                    destinationPath,
                    overwriteConfirmed))
                .ConfigureAwait(true);
            if (!response.Success)
            {
                SetServiceStatus($"Service connection: restore failed ({response.ErrorMessage})");
                return;
            }

            var summary = response.RestoreSelection;
            if (summary is null)
            {
                SetServiceStatus("The restore response did not include results. Check the destination before retrying.");
                return;
            }
            var message = $"Restored and verified {summary.RestoredCount} of {summary.FileCount} file(s).";
            if (summary.FailedPaths.Count > 0) message += " Failed: " + string.Join("; ", summary.FailedPaths);
            if (summary.Warnings is { Count: > 0 }) message += " " + string.Join(" ", summary.Warnings);
            try { await RefreshAsync().ConfigureAwait(true); FileBrowser.RefreshBrowser(); }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
            { message += $" Status refresh failed: {exception.Message}"; }
            SetServiceStatus(message);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetServiceStatus($"Service connection: restore failed ({ex.Message})");
        }
    }

    private (string Path, bool IsDirectory)? GetSelectedBrowserRestoreSelection()
    {
        if (FileBrowser.SelectedFile is not null)
        {
            return (FileBrowser.SelectedFile.Path, false);
        }

        if (FileBrowser.SelectedFolder is not null)
        {
            return (FileBrowser.SelectedFolder.Path, true);
        }

        return null;
    }

    internal async Task OpenVersionPreviewAsync(VersionRow selectedVersion)
    {
        if (selectedVersion.EntryKind == RepositoryEntryKind.Folder)
        {
            SetServiceStatus("Service connection: select a file version to open a preview.");
            return;
        }

        if (IsPreviewBusy)
        {
            return;
        }

        IsPreviewBusy = true;
        PreviewStatus = "Preparing preview...";
        var copyCreated = false;
        var publicationWarnings = string.Empty;
        try
        {
            var path = previewCache.Allocate(selectedVersion.SourcePath, acceptedConfiguration?.VersionPreview ?? new());
            var response = await SendBoundAsync(FluxVaultIpcRequest.RestoreVersionPreview(selectedVersion.VersionId) with { OutputPath = path })
                .ConfigureAwait(true);
            if (!response.Success)
            {
                SetServiceStatus(response.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown
                    ? "Preview could not be confirmed; a copy may have been created. Nothing was opened."
                    : $"Service connection: version preview failed ({response.ErrorMessage ?? "no preview returned"})");
                return;
            }
            if (response.RestoreResult is not { RestoredFileCount: 1, VerifiedLogicalBytes: >= 0 } result ||
                !string.Equals(response.OutputPath, path, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.OutputPath, path, StringComparison.OrdinalIgnoreCase))
            {
                SetServiceStatus("The preview acknowledgement did not match its verified destination. Nothing was opened.");
                return;
            }
            copyCreated = true;
            publicationWarnings = string.Join(" ", result.Warnings);
            previewCache.MakeReadOnly(path);
            versionPreviewLauncher.OpenFile(path);
            SetServiceStatus($"Service connection: opened preview for {selectedVersion.VersionId}. {publicationWarnings}".TrimEnd());
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception or OperationCanceledException)
        {
            SetServiceStatus(copyCreated
                ? $"The verified preview copy was created, but opening failed ({ex.Message}). {publicationWarnings}".TrimEnd()
                : $"Version preview could not be prepared or confirmed ({ex.Message}); nothing was opened.");
        }
        finally
        {
            PreviewStatus = string.Empty;
            IsPreviewBusy = false;
        }
    }

    internal async Task<VersionInventoryViewModel> CreateVersionInventoryAsync(WatchedFolderRow folder)
    {
        try
        {
            var response = await SendBoundAsync(FluxVaultIpcRequest.ListVersions()).ConfigureAwait(true);
            if (!response.Success || response.Versions is null)
            {
                SetServiceStatus($"Service connection: backup inventory failed ({response.ErrorMessage ?? "no versions returned"})");
                return EmptyVersionInventory(folder.Path);
            }

            return new VersionInventoryViewModel(
                folder.Path,
                response.Versions,
                version => RestoreVersionAsync(ToVersionRow(version)),
                version => OpenVersionPreviewAsync(ToVersionRow(version)));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetServiceStatus($"Service connection: backup inventory failed ({ex.Message})");
            return EmptyVersionInventory(folder.Path);
        }
    }

    internal async Task<VersionInventoryViewModel> CreateVersionInventoryForPathAsync(string path)
    {
        try
        {
            var response = await SendBoundAsync(FluxVaultIpcRequest.ListVersions()).ConfigureAwait(true);
            if (!response.Success || response.Versions is null)
            {
                SetServiceStatus($"Service connection: backup inventory failed ({response.ErrorMessage ?? "no versions returned"})");
                return EmptyVersionInventory(path);
            }

            var fullPath = Path.GetFullPath(path);
            var exactMatch = response.Versions.Any(version => IsSamePath(version.SourcePath, fullPath));
            var folderPath = exactMatch
                ? Path.GetDirectoryName(fullPath) ?? fullPath
                : fullPath;
            return new VersionInventoryViewModel(
                folderPath,
                response.Versions,
                version => RestoreVersionAsync(ToVersionRow(version)),
                version => OpenVersionPreviewAsync(ToVersionRow(version)),
                fullPath);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetServiceStatus($"Service connection: backup inventory failed ({ex.Message})");
            return EmptyVersionInventory(path);
        }
    }

    private async Task ShowVersionsForPathAsync(string path)
    {
        var inventory = await CreateVersionInventoryForPathAsync(path).ConfigureAwait(true);
        VersionInventoryRequested?.Invoke(this, new VersionInventoryRequestedEventArgs(inventory));
        SetServiceStatus(inventory.Versions.Count == 0
            ? $"Service connection: no restorable versions found for {path}"
            : $"Service connection: showing versions for {path}");
    }

    private VersionInventoryViewModel EmptyVersionInventory(string folderPath)
    {
        return new VersionInventoryViewModel(
            folderPath,
            [],
            version => RestoreVersionAsync(ToVersionRow(version)),
            version => OpenVersionPreviewAsync(ToVersionRow(version)));
    }

    private static VersionRow ToVersionRow(VersionInventoryVersionRow version)
    {
        return new VersionRow(
            version.VersionId,
            version.Path,
            version.CapturedAt,
            Enum.TryParse<CaptureConsistency>(version.Consistency.Replace(" ", string.Empty), ignoreCase: true, out var consistency)
                ? consistency
                : CaptureConsistency.BestEffort,
            version.ChunkCount,
            version.Lineage,
            version.EntryKind,
            version.IsDeleted);
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var path = BrowseFolder(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var response = await SendBoundAsync(FluxVaultIpcRequest.ExportDiagnostics(path)).ConfigureAwait(true);
        DiagnosticsText = response.Success
            ? $"Diagnostics exported to {response.OutputPath}"
            : $"Diagnostics export failed: {response.ErrorMessage}";
    }

    [RelayCommand]
    private async Task RunRepositoryScrubAsync()
    {
        try
        {
            var response = await SendBoundAsync(FluxVaultIpcRequest.RunRepositoryScrub()).ConfigureAwait(true);
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
            var response = await SendBoundAsync(FluxVaultIpcRequest.RunRestoreRehearsal()).ConfigureAwait(true);
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

    [RelayCommand(CanExecute = nameof(CanShowGlobalMirrorRepairActions))]
    private async Task PreviewMirrorRepairAsync()
    {
        await RunMirrorRepairCoreAsync(isPreview: true, mirrorNodeId: null).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanShowGlobalMirrorRepairActions))]
    private async Task RunMirrorRepairAsync()
    {
        await RunMirrorRepairCoreAsync(isPreview: false, mirrorNodeId: null).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanShowSelectedMirrorRepairActions))]
    private async Task PreviewSelectedMirrorRepairAsync()
    {
        if (!CanShowSelectedMirrorRepairActions || SelectedMirrorNode is null)
        {
            RepositoryHealthStatus = "Mirror repair preview failed: select an enabled mirror.";
            return;
        }

        await RunMirrorRepairCoreAsync(isPreview: true, SelectedMirrorNode.Id).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanShowSelectedMirrorRepairActions))]
    private async Task RunSelectedMirrorRepairAsync()
    {
        if (!CanShowSelectedMirrorRepairActions || SelectedMirrorNode is null)
        {
            RepositoryHealthStatus = "Mirror repair failed: select an enabled mirror.";
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
            var response = await SendBoundAsync(request).ConfigureAwait(true);
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

    [RelayCommand(CanExecute = nameof(CanShowSelectedMirrorDrainActions))]
    private async Task PreviewSelectedMirrorDrainAsync()
    {
        if (!CanShowSelectedMirrorDrainActions || SelectedMirrorNode is null)
        {
            RepositoryHealthStatus = "Mirror drain preview failed: select an enabled mirror with at least one other enabled mirror.";
            return;
        }

        await RunMirrorDrainCoreAsync(isPreview: true, SelectedMirrorNode.Id).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanShowSelectedMirrorDrainActions))]
    private async Task RunSelectedMirrorDrainAsync()
    {
        if (!CanShowSelectedMirrorDrainActions || SelectedMirrorNode is null)
        {
            RepositoryHealthStatus = "Mirror drain failed: select an enabled mirror with at least one other enabled mirror.";
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
            var response = await SendBoundAsync(request).ConfigureAwait(true);
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

    [RelayCommand(CanExecute = nameof(CanShowMirrorPlacementActions))]
    private async Task PreviewMirrorRebalanceAsync()
    {
        try
        {
            var response = await SendBoundAsync(new FluxVaultIpcRequest(
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

    [RelayCommand(CanExecute = nameof(CanShowMirrorPlacementActions))]
    private async Task RunMirrorRebalanceAsync()
    {
        try
        {
            var response = await SendBoundAsync(new FluxVaultIpcRequest(
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

    private bool ApplyStatus(
        FluxVaultServiceStatus status,
        bool preserveLocalConfiguration,
        bool isAutomatic = false,
        bool forceConfigurationReload = false,
        bool reconcileOptions = false,
        VaultId? repositoryId = null,
        long? revision = null)
    {
        var inventoryIdentity = (
            repositoryId,
            status.Configuration.RepositoryPath,
            status.Configuration.MetadataStore);
        if ((hasLocalConfigurationChanges || IsProtectionSaveBusy || isOptionsEditing || requiresOptionsReconciliation) && acceptedVaultId is not null
            && inventoryIdentity.Item1 != acceptedVaultId)
        {
            SetServiceStatus("Service connection: repository binding could not be confirmed; your pending changes are kept. Backup has not started for this action.");
            return false;
        }
        if (requiresSaveStatusCheck && preserveLocalConfiguration)
        {
            // Rebase only an observed pre-save or dispatched snapshot. An unrelated
            // configuration change needs explicit reload rather than a blind overwrite.
            var observed = ComputeConfigurationFingerprint(status.Configuration);
            if (acceptedConfiguration is not null && observed == ComputeConfigurationFingerprint(acceptedConfiguration) ||
                lastDispatchedSaveConfiguration is not null && observed == ComputeConfigurationFingerprint(lastDispatchedSaveConfiguration))
            {
                acceptedConfiguration = status.Configuration;
                acceptedConfigurationRevision = revision;
                requiresSaveStatusCheck = HasUnconfirmedProtectionSave;
            }
        }
        else requiresSaveStatusCheck = HasUnconfirmedProtectionSave;
        var sameInventoryIdentity = lastAppliedVersionInventoryIdentity == inventoryIdentity;
        var selectedVersionId = sameInventoryIdentity ? SelectedVersion?.VersionId : null;
        var configurationFingerprint = ComputeConfigurationFingerprint(status.Configuration);
        var shouldApplyConfiguration = !preserveLocalConfiguration
                                       && (!isAutomatic
                                           || forceConfigurationReload
                                           || lastAppliedConfigurationFingerprint is null
                                           || !string.Equals(lastAppliedConfigurationFingerprint, configurationFingerprint, StringComparison.Ordinal));
        isApplyingStatus = true;
        try
        {
            if (shouldApplyConfiguration)
            {
                acceptedConfiguration = status.Configuration;
                acceptedVaultId = inventoryIdentity.Item1;
                acceptedConfigurationRevision = revision;
                RepositoryPath = status.Configuration.RepositoryPath;
                var mirrorSet = (status.Configuration.MirrorSet
                    ?? MirrorSetConfiguration.FromLegacyPath(status.Configuration.MirrorPath)).Normalise();
                MirrorPlacementProfile = mirrorSet.PlacementPolicy.Profile;
                MinimumMirrorCopies = mirrorSet.PlacementPolicy.MinimumMirrorCopies;
                ReplaceMirrorNodes(mirrorSet.Nodes);
                FileBrowser.DefaultWorkloadPreset = (status.Configuration.WorkloadPolicy ?? WorkloadPolicyConfiguration.CreateDefault()).DefaultPreset;
                var selectionRules = status.Configuration.SelectionRules;
                FileBrowser.LoadSelectionRules(selectionRules is { Count: > 0 }
                    ? selectionRules
                    : DeriveSelectionRules(status.Configuration.WatchedFolders));
                if (FileBrowser.Roots.Count == 0)
                {
                    FileBrowser.LoadRoots();
                }

                lastAppliedConfigurationFingerprint = configurationFingerprint;
            }

            if (status.HasVersionInventory && status.TrackedEntries is not null)
            {
                FileBrowser.LoadTrackedEntries(status.TrackedEntries);
            }
            else if (!sameInventoryIdentity)
            {
                FileBrowser.LoadTrackedEntries([]);
            }

            var visibleStatus = $"Service connection: running - {status.LastMessage.TrimEnd('.')}. Last refreshed {DateTime.Now:HH:mm:ss}";
            if (!string.IsNullOrWhiteSpace(RestoreHintPath))
            {
                visibleStatus += $". Restore request: {RestoreHintPath}";
            }

            if (!status.HasVersionInventory)
            {
                visibleStatus += sameInventoryIdentity
                    ? ". Repository inventory unavailable; previous versions retained"
                    : ". Repository inventory unavailable for this vault";
            }
            else if (status.RecentVersions.Count == 0)
            {
                visibleStatus += ". Select a repository version after a backup completes before restoring";
            }

            SetServiceStatus(visibleStatus, BuildServiceStatusToolTip(visibleStatus, status.DurableChange));
            if (!status.HasVersionInventory)
            {
                SetServiceConnectionWarning("The service has not supplied a repository version inventory.");
            }
            else if (windowsServiceStatus.State == FluxVaultWindowsServiceState.Running)
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
            ApplyPerformanceWorkspaceStatus(status.PerformanceWorkspace);
            ApplyShellIntegrationStatus(status.ShellIntegration);
            ApplyDirectCloudStatus(status.DirectCloud);
            ApplySecurityPostureStatus(status.SecurityPosture);
            ApplyFleetStatus(status.Fleet);
            ApplyRepositoryHealth(status.RepositoryHealth);
            ApplyMetadataStoreHealth(status.MetadataStore);
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
                    FormatWatchedFolderProfile(folder, status.Configuration.SelectionRules ?? []),
                    FormatRegexSummary(status.Configuration.SelectionRules ?? [], folder.Path),
                    folder.Compression,
                    runtimeStatus));
            }

            if (status.HasVersionInventory)
            {
                RecentVersions.Clear();
                foreach (var version in status.RecentVersions)
                {
                    RecentVersions.Add(new VersionRow(
                        version.VersionId,
                        version.SourcePath,
                        version.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        version.Consistency,
                        version.ChunkCount,
                        FormatLineage(version),
                        version.EntryKind,
                        version.IsDeleted));
                }

                lastAppliedVersionInventoryIdentity = inventoryIdentity;
                SelectedVersion = selectedVersionId is null
                    ? FindRestoreHintVersion()
                    : RecentVersions.SingleOrDefault(version => version.VersionId == selectedVersionId) ?? FindRestoreHintVersion();
            }
            else if (!sameInventoryIdentity)
            {
                RecentVersions.Clear();
                SelectedVersion = null;
                lastAppliedVersionInventoryIdentity = null;
            }
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

            if (shouldApplyConfiguration)
            {
                MarkStatusApplied(reconcileOptions);
            }
        }
        finally
        {
            isApplyingStatus = false;
        }
        return shouldApplyConfiguration;
    }

    private FluxVaultConfiguration BuildConfiguration()
    {
        var baseline = acceptedConfiguration ?? throw new InvalidOperationException("Load configuration before saving.");
        var selectionRules = FileBrowser.GetSelectionRules();
        var watchedFolders = ProtectionSelectionCompiler.Compile(selectionRules);
        var mirrorSet = new MirrorSetConfiguration(MirrorNodes
                .Select(node => new MirrorNodeConfiguration(
                    node.Id,
                    node.Label,
                    node.Path,
                    node.IsEnabled,
                    node.CapacityBudgetBytes,
                    node.Priority))
                .ToArray(),
                new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile, MinimumMirrorCopies));
        var sameMirrors = JsonSerializer.Serialize(mirrorSet, ConfigurationFingerprintJsonOptions)
            == JsonSerializer.Serialize((baseline.MirrorSet ?? MirrorSetConfiguration.FromLegacyPath(baseline.MirrorPath)).Normalise(), ConfigurationFingerprintJsonOptions);
        return baseline with
        {
            RepositoryPath = RepositoryPath,
            MirrorPath = sameMirrors ? baseline.MirrorPath : null,
            WatchedFolders = watchedFolders,
            SelectionRules = selectionRules,
            MirrorSet = sameMirrors ? baseline.MirrorSet ?? mirrorSet : mirrorSet
        };
    }

    private static string ComputeConfigurationFingerprint(FluxVaultConfiguration configuration)
    {
        return JsonSerializer.Serialize(configuration, ConfigurationFingerprintJsonOptions);
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

    private void ApplyPerformance(PerformanceTelemetryStatus telemetry)
    {
        PerformanceStatusText = $"Performance: sampled {telemetry.CollectedAtUtc.ToLocalTime():HH:mm:ss}";

        PerformanceMetricRows.Clear();
        PerformanceMetricRows.Add(new PerformanceMetricRow("CPU", $"{telemetry.Process.CpuPercent:0.#}% total / {telemetry.Process.CpuCoreEquivalent:0.0} core", $"Process CPU across {telemetry.Process.LogicalProcessorCount} logical processor(s)."));
        PerformanceMetricRows.Add(new PerformanceMetricRow("Working set", FormatBytes(telemetry.Process.WorkingSetBytes), "Resident process memory."));
        PerformanceMetricRows.Add(new PerformanceMetricRow("Private memory", FormatBytes(telemetry.Process.PrivateMemoryBytes), "Private process memory."));
        PerformanceMetricRows.Add(new PerformanceMetricRow("GC heap", FormatBytes(telemetry.Process.GcHeapBytes), $"Collections G0/G1/G2: {telemetry.Process.Gen0Collections}/{telemetry.Process.Gen1Collections}/{telemetry.Process.Gen2Collections}."));
        PerformanceMetricRows.Add(new PerformanceMetricRow("Threads", telemetry.Process.ThreadCount.ToString(), "Process thread count."));
        PerformanceMetricRows.Add(new PerformanceMetricRow("Handles", telemetry.Process.HandleCount.ToString(), "Process handle count."));
        PerformanceMetricRows.Add(new PerformanceMetricRow("ThreadPool", $"{telemetry.ThreadPool.AvailableWorkerThreads}/{telemetry.ThreadPool.MaxWorkerThreads} workers", $"{telemetry.ThreadPool.PendingWorkItemCount} pending work item(s)."));
        PerformanceMetricRows.Add(new PerformanceMetricRow("IPC", $"{telemetry.Ipc.TotalRequests} request(s)", $"{telemetry.Ipc.ActiveRequests} active, {telemetry.Ipc.FailedRequests} failed; last {telemetry.Ipc.LastCommand}."));
        PerformanceMetricRows.Add(new PerformanceMetricRow("Logs", telemetry.Logs.EffectiveLevel, $"{telemetry.Logs.RetainedFileCount} file(s), {telemetry.Logs.DroppedMessageCount} dropped message(s)."));

        PerformanceLoopRows.Clear();
        foreach (var loop in telemetry.Loops)
        {
            PerformanceLoopRows.Add(new PerformanceLoopRow(
                loop.Name,
                loop.State,
                loop.Detail,
                loop.IterationCount,
                FormatLocalTime(loop.LastStartedUtc),
                FormatLocalTime(loop.LastCompletedUtc)));
        }

        PerformanceBackgroundRows.Clear();
        foreach (var work in telemetry.BackgroundWork)
        {
            PerformanceBackgroundRows.Add(new PerformanceBackgroundRow(
                work.Name,
                work.State,
                work.ActiveCount,
                work.PendingCount,
                work.Detail));
        }

        PerformanceSampleRows.Clear();
        foreach (var sample in telemetry.Samples.Reverse().Take(500).Reverse())
        {
            PerformanceSampleRows.Add(new PerformanceSampleRow(
                sample.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                $"{sample.CpuPercent:0.#}%",
                FormatBytes(sample.WorkingSetBytes),
                FormatBytes(sample.GcHeapBytes),
                sample.ThreadCount,
                sample.HandleCount,
                sample.ActiveCaptureWorkers,
                sample.WatcherBacklogCount,
                sample.IpcTotalRequests,
                sample.DroppedLogMessages));
        }
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
            : BuildSyncSummary(status);
    }

    private static string BuildSyncSummary(SyncRuntimeStatus status)
    {
        var mappings = status.Mappings ?? [];
        var appliedRemoteVersions = status.AppliedRemoteVersions ?? [];
        var hydrations = status.Hydrations ?? [];
        var conflicts = status.Conflicts ?? [];
        var pendingMappings = mappings.Count(mapping => mapping.Status == SyncMappingStatus.PendingConfirmation);
        var blockedTargets = hydrations.Count(hydration => hydration.State == SyncHydrationState.Blocked);
        var openConflicts = conflicts.Count(conflict => conflict.Status == SyncConflictStatus.Open);
        var parts = new List<string>
        {
            $"Sync: {status.PeerHeads.Count} peer head(s)",
            $"{status.Cursors.Count} cursor(s)"
        };
        if (pendingMappings > 0)
        {
            parts.Add($"{pendingMappings} pending mapping(s)");
        }

        if (appliedRemoteVersions.Count > 0)
        {
            parts.Add($"{appliedRemoteVersions.Count} applied remote version(s)");
        }

        if (blockedTargets > 0)
        {
            parts.Add($"{blockedTargets} blocked target(s)");
        }

        if (openConflicts > 0)
        {
            parts.Add($"{openConflicts} conflict(s)");
        }

        return string.Join(", ", parts);
    }

    private void ApplyPerformanceWorkspaceStatus(PerformanceWorkspaceRuntimeStatus? status)
    {
        if (status is null)
        {
            PerformanceWorkspaceStatus = "Performance workspace: waiting";
            return;
        }

        var state = status.IsEnabled ? "prepared" : "disabled";
        var driver = status.IsDriverCheckDeferred ? "driver check deferred" : status.Status;
        PerformanceWorkspaceStatus = $"Performance workspace: {status.Mode} {state}, {driver}";
    }

    private void ApplyShellIntegrationStatus(ShellIntegrationRuntimeStatus? status)
    {
        if (status is null)
        {
            ShellIntegrationStatus = "Shell integration: waiting";
            return;
        }

        var state = status.IsEnabled ? "prepared" : "disabled";
        var registration = status.IsRegistrationDeferred ? "registration deferred" : status.Status;
        ShellIntegrationStatus = $"Shell integration: {status.Mode} {state}, {registration}";
    }

    private void ApplyDirectCloudStatus(DirectCloudRuntimeStatus? status)
    {
        if (status is null)
        {
            DirectCloudStatus = "Direct cloud: waiting";
            return;
        }

        var enabled = status.Adapters.Count(adapter => adapter.IsEnabled);
        var validation = status.IsLiveValidationDeferred ? "live validation deferred" : status.Status;
        DirectCloudStatus = $"Direct cloud: {enabled} enabled adapter(s), {validation}";
    }

    private void ApplySecurityPostureStatus(SecurityPostureRuntimeStatus? status)
    {
        if (status is null)
        {
            SecurityPostureStatus = "Security: waiting";
            return;
        }

        var state = status.IsClientSideEncryptionEnabled ? "encryption planned" : "encryption disabled";
        var execution = status.IsEncryptionExecutionDeferred ? "execution deferred" : status.Status;
        SecurityPostureStatus = $"Security: {state}, {status.KeyReferenceCount} key reference(s), {execution}";
    }

    private void ApplyFleetStatus(FleetRuntimeStatus? status)
    {
        if (status is null)
        {
            FleetPolicyStatus = "Fleet: waiting";
            return;
        }

        var state = status.IsEnabled ? "enabled" : "disabled";
        var management = status.IsRemoteManagementDeferred ? "remote management deferred" : status.Status;
        FleetPolicyStatus = $"Fleet: {status.Mode} {state}, {status.AssignmentCount} assignment(s), {management}";
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

    private void ApplyMetadataStoreHealth(MetadataStoreRuntimeStatus? metadataStore)
    {
        if (metadataStore is null)
        {
            return;
        }

        var status = metadataStore.LastError is not null
            ? "Unavailable"
            : metadataStore.IsExportLagExceeded
                ? "Export lag warning"
                : "Ready";
        var oldestAge = metadataStore.OldestUnexportedAge is null
            ? "none"
            : $"{(int)metadataStore.OldestUnexportedAge.Value.TotalMinutes} minute(s)";
        var detail = metadataStore.LastError is not null
            ? $"{metadataStore.Provider} at {metadataStore.Endpoint}: {metadataStore.LastError}"
            : $"{metadataStore.Provider} at {metadataStore.Endpoint}; schema initialized: {metadataStore.SchemaInitialized}; pending journal exports: {metadataStore.PendingOutboxCount}; oldest pending age: {oldestAge}.";

        RepositoryHealthRows.Add(new RepositoryHealthRow("Metadata database", status, detail));
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

    private static string FormatLocalTime(DateTimeOffset? timestamp)
    {
        return timestamp is null ? string.Empty : timestamp.Value.ToLocalTime().ToString("HH:mm:ss");
    }

    private static string FormatMirrorPlacementProfile(MirrorPlacementProfile profile)
    {
        return profile switch
        {
            MirrorPlacementProfile.FullCopy => "Full copy",
            MirrorPlacementProfile.CapacityBalanced => "Capacity balanced",
            MirrorPlacementProfile.Redundant => "Redundant",
            _ => profile.ToString()
        };
    }

    private static string FormatWatchedFolderProfile(
        WatchedFolderConfiguration folder,
        IReadOnlyList<ProtectionSelectionRule> selectionRules)
    {
        var rule = selectionRules
            .Where(rule => rule.Mode != ProtectionSelectionMode.RegexScope
                           && (string.Equals(TrimPath(rule.Path), TrimPath(folder.Path), StringComparison.OrdinalIgnoreCase)
                               || string.Equals(rule.Id, folder.Id, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(rule => TrimPath(rule.Path).Length)
            .FirstOrDefault();
        if (rule?.WorkloadPreset is { } preset)
        {
            return WorkloadPolicyPresetCatalog.Get(preset).DisplayName;
        }

        return folder.ResourceProfile switch
        {
            ResourceProfile.Quiet => "Quiet",
            ResourceProfile.Balanced => "Balanced",
            ResourceProfile.Fast => "Fast",
            _ => folder.ResourceProfile.ToString()
        };
    }

    private static string FormatRegexSummary(IReadOnlyList<ProtectionSelectionRule> rules, string path)
    {
        var applicable = rules
            .Where(rule => rule.IsEnabled && CoversRegexPath(rule, path))
            .OrderBy(rule => TrimPath(rule.Path).Length)
            .ToArray();
        var includes = applicable
            .SelectMany(rule => rule.IncludeRegexRules ?? [])
            .Where(rule => rule.IsEnabled)
            .Select(rule => rule.Pattern)
            .ToArray();
        var excludes = applicable
            .SelectMany(rule => rule.ExcludeRegexRules ?? [])
            .Where(rule => rule.IsEnabled)
            .Select(rule => rule.Pattern)
            .ToArray();
        if (includes.Length == 0 && excludes.Length == 0)
        {
            return "None";
        }

        var parts = new List<string>();
        if (includes.Length > 0)
        {
            parts.Add("Include: " + string.Join(", ", includes));
        }

        if (excludes.Length > 0)
        {
            parts.Add("Exclude: " + string.Join(", ", excludes));
        }

        return string.Join("; ", parts);
    }

    private static bool CoversRegexPath(ProtectionSelectionRule rule, string path)
    {
        return rule.Mode switch
        {
            ProtectionSelectionMode.RegexScope or ProtectionSelectionMode.RecursiveFolder => IsSamePath(path, rule.Path) || IsUnderPath(path, rule.Path),
            ProtectionSelectionMode.ImmediateFiles => IsSamePath(path, rule.Path),
            ProtectionSelectionMode.File => IsSamePath(path, rule.Path),
            _ => false
        };
    }

    private static bool IsUnderPath(string path, string root)
    {
        var trimmedRoot = TrimPath(root) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(TrimPath(left), TrimPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
        ServiceStatus = string.IsNullOrWhiteSpace(ProtectionSaveMessage) || text.Contains(ProtectionSaveMessage, StringComparison.Ordinal)
            ? text
            : $"{text}. {ProtectionSaveMessage}";
        if (!string.IsNullOrWhiteSpace(BackupOutcomeMessage) && !ServiceStatus.Contains(BackupOutcomeMessage, StringComparison.Ordinal))
            ServiceStatus += $" {BackupOutcomeMessage}";
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
    string Profile,
    string Regex,
    CompressionPreference Compression,
    string Status);

public sealed record VersionRow(
    string VersionId,
    string SourcePath,
    string CapturedAt,
    CaptureConsistency Consistency,
    int ChunkCount,
    string Lineage = "Capture",
    RepositoryEntryKind EntryKind = RepositoryEntryKind.File,
    bool IsDeleted = false);

public sealed class VersionInventoryRequestedEventArgs(VersionInventoryViewModel inventory) : EventArgs
{
    public VersionInventoryViewModel Inventory { get; } = inventory;
}

public sealed record CaptureStatusRow(
    string SourcePath,
    CaptureRuntimeState State,
    string LastEvent,
    string NextForcedCapture,
    string Detail);

public sealed record PerformanceMetricRow(
    string Name,
    string Value,
    string Detail);

public sealed record PerformanceLoopRow(
    string Name,
    string State,
    string Detail,
    long IterationCount,
    string LastStarted,
    string LastCompleted);

public sealed record PerformanceBackgroundRow(
    string Name,
    string State,
    int ActiveCount,
    int PendingCount,
    string Detail);

public sealed record PerformanceSampleRow(
    string Timestamp,
    string Cpu,
    string WorkingSet,
    string GcHeap,
    int ThreadCount,
    int HandleCount,
    int ActiveCaptureWorkers,
    int WatcherBacklogCount,
    long IpcTotalRequests,
    long DroppedLogMessages);

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

    [ObservableProperty]
    private string migrationStatus = "No migration pending";

    [ObservableProperty]
    private string migrationDetail = "No pending migration. Edits are reconciled only after explicit preview/apply, repair, or drain actions.";

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

public sealed record MirrorPlacementProfileOption(
    MirrorPlacementProfile Value,
    string DisplayName);

internal sealed record MirrorMigrationState(
    string Status,
    string Detail);

public sealed record RepositoryHealthRow(
    string Name,
    string Status,
    string Detail);
