using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;
using FluxVault.Core.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;
using System.IO;

namespace FluxVault.App.ViewModels;

public sealed partial class OptionsViewModel : ObservableObject
{
    private readonly IFluxVaultServiceClient client;
    private readonly IExplorerContextMenuService explorerContextMenuService;
    private readonly IConfigurationSaveOperationStore saveOperationStore;
    private FluxVaultConfiguration? currentConfiguration;
    private VaultId? acceptedVaultId;
    private long? acceptedRevision;
    private PendingConfigurationSave? pendingSave;
    private bool saveRecordBlocked;
    private bool saveConfirmedForReview;
    private readonly SemaphoreSlim initialiseGate = new(1, 1);
    private readonly SemaphoreSlim saveGate = new(1, 1);

    public OptionsViewModel(IFluxVaultServiceClient client)
        : this(client, new WindowsExplorerContextMenuService())
    {
    }

    public OptionsViewModel(IFluxVaultServiceClient client, IConfigurationSaveOperationStore saveOperationStore)
        : this(client, new WindowsExplorerContextMenuService(), saveOperationStore) { }

    public OptionsViewModel(
        IFluxVaultServiceClient client,
        IExplorerContextMenuService explorerContextMenuService,
        IConfigurationSaveOperationStore? saveOperationStore = null)
    {
        this.client = client;
        this.explorerContextMenuService = explorerContextMenuService;
        this.saveOperationStore = saveOperationStore ?? new MemoryConfigurationSaveOperationStore();
        RefreshExplorerContextMenuStatus();
    }

    [ObservableProperty]
    private bool retentionEnabled;

    [ObservableProperty]
    private int keepAllHours;

    [ObservableProperty]
    private int keepHourlyDays;

    [ObservableProperty]
    private int keepDailyDays;

    [ObservableProperty]
    private int minimumVersionsPerFile;

    [ObservableProperty]
    private int watcherPollSeconds;

    [ObservableProperty]
    private int periodicReconciliationMinutes;

    [ObservableProperty]
    private int fastDebounceSeconds;

    [ObservableProperty]
    private int balancedDebounceSeconds;

    [ObservableProperty]
    private int quietDebounceSeconds;

    [ObservableProperty]
    private int fastMaxHotFileDelaySeconds;

    [ObservableProperty]
    private int balancedMaxHotFileDelayMinutes;

    [ObservableProperty]
    private int quietMaxHotFileDelayMinutes;

    [ObservableProperty]
    private int minimumSameFileCaptureIntervalSeconds;

    [ObservableProperty]
    private int maximumConcurrentCaptures;

    [ObservableProperty]
    private int watcherEventBacklogLimit;

    [ObservableProperty]
    private int usnFallbackFullScanCooldownMinutes;

    [ObservableProperty]
    private int sourceDeepVerificationIntervalHours;

    [ObservableProperty]
    private CodecProfile codecProfile;

    [ObservableProperty]
    private CompressionPreference defaultCodec;

    [ObservableProperty]
    private CompressionPreference hotFileCodec;

    [ObservableProperty]
    private int codecLevel;

    [ObservableProperty]
    private int codecMinimumKb;

    [ObservableProperty]
    private string codecSkipExtensionsText = string.Empty;

    [ObservableProperty]
    private WorkloadPolicyPresetId defaultWorkloadPreset = WorkloadPolicyPresetId.GeneralPurpose;

    [ObservableProperty]
    private bool maintenanceRunAutomatically;

    [ObservableProperty]
    private int maintenanceIntervalHours;

    [ObservableProperty]
    private bool maintenanceAutoRepairFromMirror;

    [ObservableProperty]
    private int restoreRehearsalVersionCount;

    [ObservableProperty]
    private MetadataStoreProvider metadataStoreProvider = MetadataStoreProvider.PostgreSql;

    [ObservableProperty]
    private string metadataStoreHost = string.Empty;

    [ObservableProperty]
    private int metadataStorePort;

    [ObservableProperty]
    private string metadataStoreDatabaseName = string.Empty;

    [ObservableProperty]
    private string metadataStoreUsername = string.Empty;

    [ObservableProperty]
    private string metadataStoreServiceName = string.Empty;

    [ObservableProperty]
    private string metadataStoreBackupDirectory = string.Empty;

    [ObservableProperty]
    private int metadataStoreBackupRetentionDays;

    [ObservableProperty]
    private int metadataStoreMaxCaptureWorkers;

    [ObservableProperty]
    private int metadataStoreMaxDbWriterConcurrency;

    [ObservableProperty]
    private int metadataStoreExportLagWarningMinutes;

    [ObservableProperty]
    private bool diagnosticsFileLoggingEnabled = true;

    [ObservableProperty]
    private DiagnosticLogLevel diagnosticsFileLogLevel = DiagnosticLogLevel.Warning;

    [ObservableProperty]
    private string diagnosticsLogDirectory = string.Empty;

    [ObservableProperty]
    private int diagnosticsMaxLogFileMegabytes;

    [ObservableProperty]
    private int diagnosticsRetainedLogFileCount;

    [ObservableProperty]
    private int telemetrySampleIntervalSeconds;

    [ObservableProperty]
    private int telemetryRetainedSampleCount;

    [ObservableProperty]
    private int previewRetentionDays = 2;

    [ObservableProperty]
    private int recoveryItemsPerPage = 100;

    [ObservableProperty]
    private string previewText = "Retention preview has not been run.";

    [ObservableProperty]
    private string statusText = "Ready";

    [ObservableProperty]
    private string explorerContextMenuStatus = "Explorer context menu status is checking.";

    [ObservableProperty]
    private bool isExplorerContextMenuRegistered;

    [ObservableProperty]
    private string newExclusionLabel = string.Empty;

    [ObservableProperty]
    private string newExclusionDescription = string.Empty;

    [ObservableProperty]
    private string newExclusionPattern = string.Empty;

    [ObservableProperty]
    private ProtectionExclusionTarget newExclusionTarget = ProtectionExclusionTarget.Both;

    [ObservableProperty]
    private ProtectionExclusionRule? selectedExclusionRule;

    public IReadOnlyList<CodecProfile> CodecProfiles { get; } = Enum.GetValues<CodecProfile>();

    public IReadOnlyList<CompressionPreference> Codecs { get; } = Enum.GetValues<CompressionPreference>();

    public IReadOnlyList<DiagnosticLogLevel> DiagnosticLogLevels { get; } = Enum.GetValues<DiagnosticLogLevel>();

    public IReadOnlyList<MetadataStoreProvider> MetadataStoreProviders { get; } = Enum.GetValues<MetadataStoreProvider>();

    public IReadOnlyList<WorkloadPolicyPresetOption> WorkloadPresets { get; } = WorkloadPolicyPresetCatalog.PresetOptions;

    public IReadOnlyList<ProtectionExclusionTarget> ExclusionTargets { get; } = Enum.GetValues<ProtectionExclusionTarget>();

    public ObservableCollection<ProtectionExclusionRule> ExclusionRules { get; } = [];

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        await initialiseGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (currentConfiguration is not null) return;
            RefreshExplorerContextMenuStatus();
            FluxVaultIpcResponse response;
            try { response = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), cancellationToken).ConfigureAwait(true); }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            { StatusText = "Could not load options; the service acknowledgement is unavailable. Your edits are kept."; return; }
            if (!response.Success || response.Status is null || response.VaultId is not { IsValid: true } || response.VaultRevision is not > 0)
            {
                StatusText = $"Could not load options: {response.ErrorMessage ?? "no status returned"}";
                return;
            }

            currentConfiguration = response.Status.Configuration;
            acceptedVaultId = response.VaultId;
            acceptedRevision = response.VaultRevision;
            ApplyEditableConfiguration(currentConfiguration);
            StatusText = "Options loaded.";
            TryReadPendingSave(restoreDraft: true);
        }
        finally { initialiseGate.Release(); }
    }

    [RelayCommand(CanExecute = nameof(CanSaveOptions))]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var entered = false;
        try
        {
            entered = await saveGate.WaitAsync(0, cancellationToken).ConfigureAwait(true);
            if (!entered) return;
            if (currentConfiguration is null || acceptedVaultId is null || acceptedRevision is not > 0)
            {
                StatusText = "Options save failed: load the verified configuration first. Your edits are kept.";
                return;
            }
            if (!TryReadPendingSave() || HasUnconfirmedSave)
            {
                if (!saveRecordBlocked && pendingSave?.Origin == ConfigurationSaveOrigin.Options)
                    StatusText = "Options save could not be confirmed. Check the previous save outcome before retrying; your edits are kept.";
                return;
            }

            var updated = BuildEditedConfiguration();
            var pending = new PendingConfigurationSave(acceptedVaultId.Value.Value, Guid.NewGuid(), acceptedRevision.Value,
                updated, false, [], [], ConfigurationSaveOrigin.Options).Freeze();
            saveOperationStore.Reserve(pending);
            SetPendingSave(pending);
            FluxVaultIpcResponse response;
            try
            {
                response = await SendBoundAsync(pending.Request, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                response = FluxVaultIpcResponse.Failure(exception.Message) with { ErrorCode = FluxVaultIpcErrorCode.OutcomeUnknown };
            }
            if (response.Success && response.ErrorCode is null)
            {
                if (!TryClearPendingSave(pending)) return;
                currentConfiguration = pending.Configuration;
                acceptedRevision = response.VaultRevision;
                StatusText = "Options saved.";
            }
            else
            {
                if (response.ErrorCode is FluxVaultIpcErrorCode.Denied or FluxVaultIpcErrorCode.InvalidRequest or FluxVaultIpcErrorCode.StaleRevision)
                    if (!TryClearPendingSave(pending)) return;
                StatusText = $"Options save failed: {response.ErrorMessage}. Your edits are kept.";
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            TryReadPendingSave();
            StatusText = $"Options save did not complete. Your edits and any pending record are kept; no backup has started. {exception.Message}";
        }
        finally { if (entered) saveGate.Release(); }
    }

    public bool HasUnconfirmedSave => pendingSave is not null || saveRecordBlocked;
    public bool CanSaveOptions => currentConfiguration is not null && acceptedVaultId is not null && acceptedRevision is > 0 && !HasUnconfirmedSave;
    public bool CanCheckSaveOutcome => pendingSave?.Origin == ConfigurationSaveOrigin.Options && !saveRecordBlocked;
    public bool CanReloadSavedOptions => CanCheckSaveOutcome && saveConfirmedForReview;

    [RelayCommand(CanExecute = nameof(CanCheckSaveOutcome))]
    public async Task CheckSaveOutcomeAsync(CancellationToken cancellationToken = default)
    {
        var entered = false;
        try
        {
            entered = await saveGate.WaitAsync(0, cancellationToken).ConfigureAwait(true);
            if (!entered || !TryReadPendingSave() || !CanCheckSaveOutcome || pendingSave is not { } pending) return;
            var response = await SendBoundAsync(new FluxVaultIpcRequest(FluxVaultIpcCommand.GetOperationStatus,
                null, null, null, null, OperationId: pending.OperationId), cancellationToken).ConfigureAwait(true);
            if (!response.Success || response.ErrorCode is not null || response.VaultId?.Value != pending.RepositoryId ||
                response.OperationId != pending.OperationId || response.VaultRevision != pending.Revision + 1)
            {
                StatusText = "The Options save outcome could not be confirmed. Your edits are kept; check again later.";
                return;
            }
            var current = await SendBoundAsync(FluxVaultIpcRequest.GetStatus(), cancellationToken).ConfigureAwait(true);
            if (!IsSamePendingSave(pending)) return;
            if (response.Purge is not null || !current.Success || current.Status is null || current.VaultRevision != pending.Revision + 1 ||
                !SameConfiguration(current.Status.Configuration, pending.Configuration))
            {
                saveConfirmedForReview = true;
                NotifySaveState();
                StatusText = "The historical Options save is confirmed, but current settings differ or could not be verified. The record and your edits are kept. Review, then use Reload saved options to discard these edits and load current settings. No backup has started.";
                return;
            }
            if (!TryClearPendingSave(pending)) return;
            currentConfiguration = current.Status.Configuration;
            acceptedRevision = current.VaultRevision;
            StatusText = "Previous Options save confirmed. Any newer edits are kept. No backup has started.";
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        { StatusText = "The Options save outcome could not be confirmed. Your edits are kept; check again later."; }
        finally { if (entered) saveGate.Release(); }
    }

    [RelayCommand(CanExecute = nameof(CanReloadSavedOptions))]
    public async Task ReloadSavedOptionsAsync(CancellationToken cancellationToken = default)
    {
        var entered = false;
        try
        {
            entered = await saveGate.WaitAsync(0, cancellationToken).ConfigureAwait(true);
            var reviewed = saveConfirmedForReview ? pendingSave : null;
            if (!entered || reviewed is null || !TryReadPendingSave() || !CanReloadSavedOptions || !IsSamePendingSave(reviewed)) return;
            var reviewedEdits = BuildEditedConfiguration();
            var current = await SendBoundAsync(FluxVaultIpcRequest.GetStatus(), cancellationToken).ConfigureAwait(true);
            if (!current.Success || current.Status is null || current.VaultRevision < acceptedRevision)
            { StatusText = "Reload did not complete: current saved settings could not be verified. Your edits and pending record are kept."; return; }
            if (!IsSamePendingSave(reviewed)) return;
            if (!SameConfiguration(reviewedEdits, BuildEditedConfiguration()))
            { StatusText = "Reload stopped because newer edits arrived. Your edits and pending record are kept; review them before reloading again."; return; }
            if (!TryClearPendingSave(reviewed)) return;
            currentConfiguration = current.Status.Configuration;
            acceptedRevision = current.VaultRevision;
            ApplyEditableConfiguration(currentConfiguration);
            StatusText = "Current saved options reloaded. The reviewed pending save is cleared. No backup has started.";
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        { StatusText = "Reload did not complete. Your edits and pending record are kept; check again later."; }
        finally { if (entered) saveGate.Release(); }
    }

    private static bool SameConfiguration(FluxVaultConfiguration left, FluxVaultConfiguration right) =>
        System.Text.Json.JsonSerializer.Serialize(left, FileConfigurationSaveOperationStore.JsonOptions) ==
        System.Text.Json.JsonSerializer.Serialize(right, FileConfigurationSaveOperationStore.JsonOptions);

    private FluxVaultConfiguration BuildEditedConfiguration() => currentConfiguration! with
    {
        RetentionPolicy = BuildPolicy(), CaptureCadencePolicy = BuildCadence(), CodecPolicy = BuildCodec(),
        RepositoryMaintenancePolicy = BuildMaintenance(), WorkloadPolicy = BuildWorkload(),
        MetadataStore = BuildMetadataStore(), DiagnosticsPolicy = BuildDiagnostics(),
        VersionPreview = new VersionPreviewPolicy(PreviewRetentionDays).Normalise(),
        RepositoryBrowse = new RepositoryBrowsePolicy(RecoveryItemsPerPage).Normalise(), ExclusionRules = []
    };

    private void ApplyEditableConfiguration(FluxVaultConfiguration configuration)
    {
        ApplyPolicy(configuration.RetentionPolicy);
        ApplyCadence(configuration.CaptureCadencePolicy);
        ApplyCodec(configuration.CodecPolicy);
        ApplyMaintenance(configuration.RepositoryMaintenancePolicy);
        ApplyWorkload(configuration.WorkloadPolicy);
        ApplyMetadataStore(configuration.MetadataStore);
        ApplyDiagnostics(configuration.DiagnosticsPolicy);
        PreviewRetentionDays = (configuration.VersionPreview ?? new()).Normalise().RetentionDays;
        RecoveryItemsPerPage = (configuration.RepositoryBrowse ?? new()).Normalise().ItemsPerPage;
        ApplyExclusions(configuration.ExclusionRules ?? []);
    }

    private void SetPendingSave(PendingConfigurationSave? pending)
    {
        pendingSave = pending;
        saveRecordBlocked = false;
        saveConfirmedForReview = false;
        NotifySaveState();
    }

    private void NotifySaveState()
    {
        OnPropertyChanged(nameof(HasUnconfirmedSave));
        OnPropertyChanged(nameof(CanSaveOptions));
        OnPropertyChanged(nameof(CanCheckSaveOutcome));
        OnPropertyChanged(nameof(CanReloadSavedOptions));
        CheckSaveOutcomeCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ReloadSavedOptionsCommand.NotifyCanExecuteChanged();
    }

    private bool TryReadPendingSave(bool restoreDraft = false)
    {
        try
        {
            var pending = saveOperationStore.Read();
            if (pending is null) { SetPendingSave(null); return true; }
            if (pendingSave is null || !FileConfigurationSaveOperationStore.Encode(pending).AsSpan().SequenceEqual(FileConfigurationSaveOperationStore.Encode(pendingSave)))
                SetPendingSave(pending);
            saveRecordBlocked = pending.Origin != ConfigurationSaveOrigin.Options || acceptedVaultId?.Value != pending.RepositoryId;
            NotifySaveState();
            if (saveRecordBlocked)
            {
                StatusText = pending.Origin == ConfigurationSaveOrigin.Protect
                    ? "A protection save is pending. Resolve it in Protect before saving Options. Its record and your edits are kept."
                    : "The pending Options save does not match this repository binding. Its record and your edits are kept; no save or receipt check will be sent.";
                return false;
            }
            if (restoreDraft) ApplyEditableConfiguration(pending.Configuration);
            StatusText = "A previous Options save could not be confirmed. Its submitted options are kept. Check the original save outcome before retrying; no backup has started.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            saveRecordBlocked = true;
            saveConfirmedForReview = false;
            NotifySaveState();
            StatusText = $"The pending save could not be read. Its record and your edits are kept; no save or backup will be sent. {exception.Message}";
            return false;
        }
    }

    private bool IsSamePendingSave(PendingConfigurationSave expected)
    {
        if (!TryReadPendingSave()) return false;
        if (pendingSave is not null && FileConfigurationSaveOperationStore.Encode(expected).AsSpan().SequenceEqual(FileConfigurationSaveOperationStore.Encode(pendingSave))) return true;
        StatusText = "The pending save changed in another session. Its record and your edits are kept. Check the original outcome again before continuing.";
        return false;
    }

    private bool TryClearPendingSave(PendingConfigurationSave pending)
    {
        try { saveOperationStore.Clear(pending); SetPendingSave(null); return true; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryReadPendingSave();
            StatusText = $"The pending save could not be cleared. Its record and your edits are kept; check again before retrying. {exception.Message}";
            return false;
        }
    }

    private async Task<FluxVaultIpcResponse> SendBoundAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken)
    {
        if (acceptedVaultId is null) return FluxVaultIpcResponse.Failure("Load the verified configuration first.");
        var mutation = PostgreSqlVaultCatalogue.IsMutation(request.Command);
        request = request with { VaultId = acceptedVaultId, ExpectedVaultRevision = request.ExpectedVaultRevision ?? (mutation ? acceptedRevision : null),
            OperationId = request.OperationId ?? (mutation ? Guid.NewGuid() : null) };
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(true);
        if (!response.Success) return response;
        var expectedRevision = request.ExpectedVaultRevision + (request.Command == FluxVaultIpcCommand.SaveConfiguration ? 1 : 0);
        if (response.VaultId != acceptedVaultId || response.VaultRevision is not > 0 ||
            mutation && (response.OperationId != request.OperationId || response.VaultRevision != expectedRevision))
            return FluxVaultIpcResponse.Failure("The acknowledgement did not match this Options snapshot. Check the outcome before retrying.")
                with { ErrorCode = mutation ? FluxVaultIpcErrorCode.OutcomeUnknown : FluxVaultIpcErrorCode.Unavailable };
        return response;
    }

    [RelayCommand]
    public async Task PreviewRetentionAsync(CancellationToken cancellationToken = default)
    {
        if (acceptedVaultId is null) await InitialiseAsync(cancellationToken).ConfigureAwait(true);
        var response = await SendBoundAsync(FluxVaultIpcRequest.PreviewRetention(), cancellationToken).ConfigureAwait(true);
        if (!response.Success || response.RetentionPreview is null)
        {
            PreviewText = $"Preview failed: {response.ErrorMessage ?? "no preview returned"}";
            return;
        }

        PreviewText = FormatPreview(response.RetentionPreview);
    }

    [RelayCommand]
    public async Task RunRetentionNowAsync(CancellationToken cancellationToken = default)
    {
        if (acceptedVaultId is null) await InitialiseAsync(cancellationToken).ConfigureAwait(true);
        var response = await SendBoundAsync(FluxVaultIpcRequest.RunRetentionNow(), cancellationToken).ConfigureAwait(true);
        if (!response.Success || response.RetentionResult is null)
        {
            StatusText = $"Retention failed: {response.ErrorMessage ?? "no retention result returned"}";
            return;
        }

        StatusText = FormatResult(response.RetentionResult);
    }

    [RelayCommand]
    public void RegisterExplorerContextMenu()
    {
        ApplyExplorerContextMenuStatus(explorerContextMenuService.Register());
    }

    [RelayCommand]
    public void UnregisterExplorerContextMenu()
    {
        ApplyExplorerContextMenuStatus(explorerContextMenuService.Unregister());
    }

    [RelayCommand]
    public void AddExclusionRule()
    {
        var rule = new ProtectionExclusionRule(
            Id: $"exclude-{Guid.NewGuid():N}",
            Pattern: NewExclusionPattern.Trim(),
            Target: NewExclusionTarget,
            IsEnabled: true,
            Label: string.IsNullOrWhiteSpace(NewExclusionLabel) ? null : NewExclusionLabel.Trim(),
            Description: string.IsNullOrWhiteSpace(NewExclusionDescription) ? null : NewExclusionDescription.Trim());
        var validation = ProtectionExclusionRuleValidator.Validate([rule]);
        if (!validation.IsValid)
        {
            StatusText = string.Join(" ", validation.Errors);
            return;
        }

        ExclusionRules.Add(rule);
        NewExclusionLabel = string.Empty;
        NewExclusionDescription = string.Empty;
        NewExclusionPattern = string.Empty;
        NewExclusionTarget = ProtectionExclusionTarget.Both;
        StatusText = "Exclusion rule added. Save options to apply it.";
    }

    [RelayCommand]
    public void RemoveSelectedExclusionRule()
    {
        if (SelectedExclusionRule is null)
        {
            return;
        }

        ExclusionRules.Remove(SelectedExclusionRule);
        SelectedExclusionRule = null;
        StatusText = "Exclusion rule removed. Save options to apply it.";
    }

    private void ApplyPolicy(RetentionPolicy policy)
    {
        RetentionEnabled = policy.IsEnabled;
        KeepAllHours = Math.Max(0, (int)Math.Round(policy.KeepAllFor.TotalHours));
        KeepHourlyDays = Math.Max(0, (int)Math.Round(policy.KeepHourlyFor.TotalDays));
        KeepDailyDays = Math.Max(0, (int)Math.Round(policy.KeepDailyFor.TotalDays));
        MinimumVersionsPerFile = Math.Max(1, policy.MinimumVersionsPerFile);
    }

    private RetentionPolicy BuildPolicy()
    {
        return new RetentionPolicy(
            IsEnabled: RetentionEnabled,
            KeepAllFor: TimeSpan.FromHours(Math.Max(0, KeepAllHours)),
            KeepHourlyFor: TimeSpan.FromDays(Math.Max(0, KeepHourlyDays)),
            KeepDailyFor: TimeSpan.FromDays(Math.Max(0, KeepDailyDays)),
            MinimumVersionsPerFile: Math.Max(1, MinimumVersionsPerFile));
    }

    private void ApplyCadence(CaptureCadencePolicy policy)
    {
        WatcherPollSeconds = Math.Max(1, (int)Math.Round(policy.WatcherPollInterval.TotalSeconds));
        PeriodicReconciliationMinutes = Math.Max(1, (int)Math.Round(policy.PeriodicReconciliationInterval.TotalMinutes));
        FastDebounceSeconds = Math.Max(0, (int)Math.Round(policy.FastDebounce.TotalSeconds));
        BalancedDebounceSeconds = Math.Max(0, (int)Math.Round(policy.BalancedDebounce.TotalSeconds));
        QuietDebounceSeconds = Math.Max(0, (int)Math.Round(policy.QuietDebounce.TotalSeconds));
        FastMaxHotFileDelaySeconds = Math.Max(1, (int)Math.Round(policy.FastMaxHotFileDelay.TotalSeconds));
        BalancedMaxHotFileDelayMinutes = Math.Max(1, (int)Math.Round(policy.BalancedMaxHotFileDelay.TotalMinutes));
        QuietMaxHotFileDelayMinutes = Math.Max(1, (int)Math.Round(policy.QuietMaxHotFileDelay.TotalMinutes));
        MinimumSameFileCaptureIntervalSeconds = Math.Max(0, (int)Math.Round(policy.MinimumSameFileCaptureInterval.TotalSeconds));
        MaximumConcurrentCaptures = Math.Max(1, policy.MaximumConcurrentCaptures);
        WatcherEventBacklogLimit = Math.Max(16, policy.WatcherEventBacklogLimit);
        UsnFallbackFullScanCooldownMinutes = Math.Max(1, (int)Math.Round(policy.UsnFallbackFullScanCooldown.TotalMinutes));
        SourceDeepVerificationIntervalHours = Math.Max(1, (int)Math.Round(policy.SourceDeepVerificationInterval.TotalHours));
    }

    private CaptureCadencePolicy BuildCadence()
    {
        return new CaptureCadencePolicy(
            WatcherPollInterval: TimeSpan.FromSeconds(Math.Max(1, WatcherPollSeconds)),
            PeriodicReconciliationInterval: TimeSpan.FromMinutes(Math.Max(1, PeriodicReconciliationMinutes)),
            FastDebounce: TimeSpan.FromSeconds(Math.Max(0, FastDebounceSeconds)),
            BalancedDebounce: TimeSpan.FromSeconds(Math.Max(0, BalancedDebounceSeconds)),
            QuietDebounce: TimeSpan.FromSeconds(Math.Max(0, QuietDebounceSeconds)),
            FastMaxHotFileDelay: TimeSpan.FromSeconds(Math.Max(1, FastMaxHotFileDelaySeconds)),
            BalancedMaxHotFileDelay: TimeSpan.FromMinutes(Math.Max(1, BalancedMaxHotFileDelayMinutes)),
            QuietMaxHotFileDelay: TimeSpan.FromMinutes(Math.Max(1, QuietMaxHotFileDelayMinutes)),
            MinimumSameFileCaptureInterval: TimeSpan.FromSeconds(Math.Max(0, MinimumSameFileCaptureIntervalSeconds)),
            MaximumConcurrentCaptures: Math.Max(1, MaximumConcurrentCaptures),
            WatcherEventBacklogLimit: Math.Max(16, WatcherEventBacklogLimit),
            UsnFallbackFullScanCooldown: TimeSpan.FromMinutes(Math.Max(1, UsnFallbackFullScanCooldownMinutes)),
            SourceDeepVerificationInterval: TimeSpan.FromHours(Math.Max(1, SourceDeepVerificationIntervalHours)));
    }

    private void ApplyCodec(CodecPolicy policy)
    {
        CodecProfile = policy.Profile;
        DefaultCodec = policy.Codec;
        HotFileCodec = policy.HotFileOverride;
        CodecLevel = Math.Max(1, policy.Level);
        CodecMinimumKb = Math.Max(0, (int)Math.Round(policy.MinimumBytes / 1024d));
        CodecSkipExtensionsText = string.Join(Environment.NewLine, policy.SkipExtensions);
    }

    private CodecPolicy BuildCodec()
    {
        var defaults = currentConfiguration?.CodecPolicy ?? CodecPolicy.CreateDefault();
        return defaults with
        {
            Codec = DefaultCodec,
            Profile = CodecProfile,
            Level = Math.Clamp(CodecLevel, 1, 22),
            MinimumBytes = Math.Max(0, CodecMinimumKb) * 1024L,
            SkipExtensions = ParseSkipExtensions(CodecSkipExtensionsText),
            HotFileOverride = HotFileCodec
        };
    }

    private void ApplyMaintenance(RepositoryMaintenancePolicy policy)
    {
        MaintenanceRunAutomatically = policy.IsEnabled && policy.RunAutomatically;
        MaintenanceIntervalHours = Math.Max(1, (int)Math.Round(policy.Interval.TotalHours));
        MaintenanceAutoRepairFromMirror = policy.AutoRepairFromMirror;
        RestoreRehearsalVersionCount = Math.Max(0, policy.RestoreRehearsalVersionCount);
    }

    private void ApplyWorkload(WorkloadPolicyConfiguration policy)
    {
        DefaultWorkloadPreset = policy.DefaultPreset;
    }

    private RepositoryMaintenancePolicy BuildMaintenance()
    {
        return new RepositoryMaintenancePolicy(
            IsEnabled: currentConfiguration?.RepositoryMaintenancePolicy?.IsEnabled ?? true,
            Interval: TimeSpan.FromHours(Math.Max(1, MaintenanceIntervalHours)),
            AutoRepairFromMirror: MaintenanceAutoRepairFromMirror,
            RestoreRehearsalVersionCount: Math.Max(0, RestoreRehearsalVersionCount),
            RunAutomatically: MaintenanceRunAutomatically);
    }

    private void ApplyMetadataStore(MetadataStoreConfiguration configuration)
    {
        MetadataStoreProvider = configuration.Provider;
        MetadataStoreHost = configuration.Host;
        MetadataStorePort = configuration.Port;
        MetadataStoreDatabaseName = configuration.DatabaseName;
        MetadataStoreUsername = configuration.Username;
        MetadataStoreServiceName = configuration.ServiceName;
        MetadataStoreBackupDirectory = configuration.BackupDirectory;
        MetadataStoreBackupRetentionDays = configuration.BackupRetentionDays;
        MetadataStoreMaxCaptureWorkers = configuration.MaxCaptureWorkers;
        MetadataStoreMaxDbWriterConcurrency = configuration.MaxDbWriterConcurrency;
        MetadataStoreExportLagWarningMinutes = Math.Max(1, (int)Math.Round(configuration.ExportLagWarningThreshold.TotalMinutes));
    }

    private MetadataStoreConfiguration BuildMetadataStore()
    {
        var defaults = currentConfiguration?.MetadataStore ?? MetadataStoreConfiguration.CreateDefault(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        return defaults with
        {
            Provider = MetadataStoreProvider,
            Host = string.IsNullOrWhiteSpace(MetadataStoreHost) ? defaults.Host : MetadataStoreHost.Trim(),
            Port = Math.Clamp(MetadataStorePort, 1, 65535),
            DatabaseName = string.IsNullOrWhiteSpace(MetadataStoreDatabaseName) ? defaults.DatabaseName : MetadataStoreDatabaseName.Trim(),
            Username = string.IsNullOrWhiteSpace(MetadataStoreUsername) ? defaults.Username : MetadataStoreUsername.Trim(),
            ServiceName = string.IsNullOrWhiteSpace(MetadataStoreServiceName) ? defaults.ServiceName : MetadataStoreServiceName.Trim(),
            BackupDirectory = string.IsNullOrWhiteSpace(MetadataStoreBackupDirectory) ? defaults.BackupDirectory : MetadataStoreBackupDirectory.Trim(),
            BackupRetentionDays = Math.Max(1, MetadataStoreBackupRetentionDays),
            MaxCaptureWorkers = Math.Max(1, MetadataStoreMaxCaptureWorkers),
            MaxDbWriterConcurrency = Math.Max(1, MetadataStoreMaxDbWriterConcurrency),
            ExportLagWarningThreshold = TimeSpan.FromMinutes(Math.Max(1, MetadataStoreExportLagWarningMinutes))
        };
    }

    private void ApplyDiagnostics(DiagnosticsPolicy policy)
    {
        DiagnosticsFileLoggingEnabled = policy.IsFileLoggingEnabled;
        DiagnosticsFileLogLevel = policy.FileLogLevel;
        DiagnosticsLogDirectory = policy.LogDirectory;
        DiagnosticsMaxLogFileMegabytes = Math.Max(1, policy.MaxLogFileMegabytes);
        DiagnosticsRetainedLogFileCount = Math.Max(1, policy.RetainedLogFileCount);
        TelemetrySampleIntervalSeconds = Math.Max(1, (int)Math.Round(policy.TelemetrySampleInterval.TotalSeconds));
        TelemetryRetainedSampleCount = Math.Max(1, policy.RetainedTelemetrySampleCount);
    }

    private DiagnosticsPolicy BuildDiagnostics()
    {
        var defaults = currentConfiguration?.DiagnosticsPolicy
            ?? DiagnosticsPolicy.CreateDefault(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        return new DiagnosticsPolicy(
            IsFileLoggingEnabled: DiagnosticsFileLoggingEnabled,
            FileLogLevel: DiagnosticsFileLogLevel,
            LogDirectory: string.IsNullOrWhiteSpace(DiagnosticsLogDirectory)
                ? defaults.LogDirectory
                : DiagnosticsLogDirectory.Trim(),
            MaxLogFileMegabytes: Math.Max(1, DiagnosticsMaxLogFileMegabytes),
            RetainedLogFileCount: Math.Max(1, DiagnosticsRetainedLogFileCount),
            TelemetrySampleInterval: TimeSpan.FromSeconds(Math.Max(1, TelemetrySampleIntervalSeconds)),
            RetainedTelemetrySampleCount: Math.Max(1, TelemetryRetainedSampleCount));
    }

    private WorkloadPolicyConfiguration BuildWorkload()
    {
        return new WorkloadPolicyConfiguration(DefaultWorkloadPreset);
    }

    private static IReadOnlyList<string> ParseSkipExtensions(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new List<string>();
        foreach (var token in text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var extension = token.StartsWith(".", StringComparison.Ordinal) ? token : "." + token;
            extension = extension.ToLowerInvariant();
            if (extension == "." || !seen.Add(extension))
            {
                continue;
            }

            extensions.Add(extension);
        }

        return extensions;
    }

    private void ApplyExclusions(IReadOnlyList<ProtectionExclusionRule> rules)
    {
        ExclusionRules.Clear();
        foreach (var rule in rules)
        {
            ExclusionRules.Add(rule);
        }
    }

    private void RefreshExplorerContextMenuStatus()
    {
        ApplyExplorerContextMenuStatus(explorerContextMenuService.GetStatus());
    }

    private void ApplyExplorerContextMenuStatus(ExplorerContextMenuStatus status)
    {
        IsExplorerContextMenuRegistered = status.IsRegistered;
        ExplorerContextMenuStatus = status.Message;
    }

    private static string FormatPreview(RepositoryRetentionPreview preview)
    {
        return $"Preview: keep {preview.KeptVersionCount} version(s), prune {preview.PrunableVersionCount} version(s), reclaim about {FormatBytes(preview.EstimatedReclaimableBytes)}. Repository size {FormatBytes(preview.RepositorySizeBytes)}.";
    }

    private static string FormatResult(RepositoryRetentionResult result)
    {
        return $"Retention kept {result.KeptVersionCount} version(s), pruned {result.PrunedVersionCount}, reclaimed {FormatBytes(result.ReclaimedBytes)}.";
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
}
