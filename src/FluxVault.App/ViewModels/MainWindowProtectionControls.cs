using System.IO;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Ipc;
using FluxVault.App.Services;

namespace FluxVault.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool? requestedProtectionPause;

    public bool CanPauseProtection => CanChangeProtectionState && acceptedConfiguration!.IsEnabled;
    public bool CanResumeProtection => CanChangeProtectionState && !acceptedConfiguration!.IsEnabled;
    public bool CanRunBackupNow => CanSaveProtection && !HasUnconfirmedBackup && acceptedConfiguration?.IsEnabled == true;
    public bool CanStopProtectingSelectedKeepHistory => CanSaveProtection && GetSelectedBrowserRestoreSelection() is not null;
    private bool CanChangeProtectionState => CanSaveProtection && !HasUnconfirmedBackup &&
        !requiresSaveStatusCheck && !requiresPurgeReconciliation && acceptedConfiguration is not null &&
        acceptedVaultId is not null && acceptedConfigurationRevision is > 0;

    public string ProtectionControlStatus => requestedProtectionPause is { } paused
        ? paused ? "Pausing: waiting for any accepted capture to finish…" : "Resuming protection…"
        : HasUnconfirmedProtectionSave && unconfirmedProtectionSave?.IsProtectionPaused is not null
            ? "Protection state uncertain: check the original outcome before continuing."
            : acceptedConfiguration is null ? "Protection state unavailable."
            : acceptedConfiguration.IsEnabled
                ? "Protection enabled. Automatic capture is not available in this release."
                : "Protection paused. New captures are stopped; history and recovery remain available.";

    private void NotifyProtectionControlAvailability()
    {
        OnPropertyChanged(nameof(CanPauseProtection));
        OnPropertyChanged(nameof(CanResumeProtection));
        OnPropertyChanged(nameof(ProtectionControlStatus));
        OnPropertyChanged(nameof(CanRunBackupNow));
        PauseProtectionCommand.NotifyCanExecuteChanged();
        ResumeProtectionCommand.NotifyCanExecuteChanged();
        RunBackupNowCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStopProtectingSelectedKeepHistory));
        StopProtectingSelectedKeepHistoryCommand.NotifyCanExecuteChanged();
        NotifyHistoryDeletionAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanPauseProtection))]
    private Task PauseProtectionAsync() => SetProtectionStateAsync(isPaused:true);

    [RelayCommand(CanExecute = nameof(CanResumeProtection))]
    private Task ResumeProtectionAsync() => SetProtectionStateAsync(isPaused:false);

    [RelayCommand(CanExecute = nameof(CanStopProtectingSelectedKeepHistory))]
    private void StopProtectingSelectedKeepHistory()
    {
        if (!CanStopProtectingSelectedKeepHistory || GetSelectedBrowserRestoreSelection() is not { } selection) return;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selection.Path));
        var prefix = Path.EndsInDirectorySeparator(path) ? path : path+Path.DirectorySeparatorChar;
        if (selection.IsDirectory)
        {
            // Remove explicit descendants as well as the selected root, then use
            // the existing exclusion contract if an ancestor still protects it.
            foreach (var rule in FileBrowser.GetSelectionRules().Where(rule =>
                string.Equals(rule.Path,path,StringComparison.OrdinalIgnoreCase) ||
                rule.Path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray())
                FileBrowser.RemoveSelectionRule(rule.Path);
        }
        else FileBrowser.RemoveSelectionRule(path);
        FileBrowser.RemovePathSelection(path,selection.IsDirectory);
        ProtectionSaveMessage = "Stop protecting is pending. Save selections to apply it. Existing history is kept subject to retention; no history deletion was requested.";
    }

    private async Task SetProtectionStateAsync(bool isPaused)
    {
        if (!TryLoadPendingConfigurationSave() || !CanChangeProtectionState) return;
        var vaultId = acceptedVaultId;
        var generation = configurationEditGeneration;
        var dispatched = false;
        IsProtectionSaveBusy = true;
        requestedProtectionPause = isPaused;
        NotifyProtectionControlAvailability();
        try
        {
            // Capture only the accepted baseline. Unsaved selections and mirrors
            // stay in the separately linked editing draft.
            var pending = new PendingConfigurationSave(vaultId!.Value.Value, Guid.NewGuid(), acceptedConfigurationRevision!.Value,
                acceptedConfiguration! with { IsEnabled = !isPaused }, false, [], [], IsProtectionPaused:isPaused).Freeze();
            pending = await ReserveProtectionSaveAsync(pending,generation).ConfigureAwait(true);
            lastDispatchedSaveConfiguration = pending.Configuration;
            ProtectionSaveState = ProtectionSaveState.Saving;
            ProtectionSaveMessage = ProtectionControlStatus;
            dispatched = true;
            var response = await SendBoundAsync(pending.Request).ConfigureAwait(true);
            var saved = response.Success;
            if (!saved && response.ErrorCode is not (FluxVaultIpcErrorCode.Denied or FluxVaultIpcErrorCode.InvalidRequest or FluxVaultIpcErrorCode.StaleRevision))
            {
                requiresSaveStatusCheck = true;
                CompleteProtectionSave(ProtectionSaveState.Unknown,vaultId,generation,
                    "Protection control could not be confirmed. Check the original outcome; your edits and history are kept. No backup has started.");
                return;
            }
            if (saved)
            {
                acceptedConfiguration = pending.Configuration;
                acceptedConfigurationRevision = response.VaultRevision;
            }
            await protectionDraftIoGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (!await CompleteLocalProtectionSaveAsync(pending,saved).ConfigureAwait(true) || !TryClearPendingConfigurationSave(pending)) return;
                hasLocalConfigurationChanges = ComputeConfigurationFingerprint(BuildConfiguration()) != ComputeConfigurationFingerprint(acceptedConfiguration!);
            }
            finally { protectionDraftIoGate.Release(); }
            CompleteProtectionSave(saved ? ProtectionSaveState.Saved : ProtectionSaveState.Failed,vaultId,generation,
                saved ? isPaused ? "Protection paused. Your pending edits and history are kept." : "Protection resumed. Your pending edits and history are kept."
                    : $"Protection control was rejected ({response.ErrorMessage}). Your edits and history are kept.");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or
            UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            TryLoadPendingConfigurationSave();
            requiresSaveStatusCheck = HasUnconfirmedProtectionSave;
            CompleteProtectionSave(HasUnconfirmedProtectionSave ? ProtectionSaveState.Unknown : ProtectionSaveState.Failed,vaultId,generation,
                dispatched ? "Protection control could not be confirmed. Check the original outcome before continuing; your edits and history are kept."
                    : "Protection control preparation failed. Your edits and any pending records are kept. " + exception.Message);
        }
        finally
        {
            requestedProtectionPause = null;
            IsProtectionSaveBusy = false;
            NotifyProtectionControlAvailability();
        }
    }
}
