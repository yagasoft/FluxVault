using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Ipc;
using FluxVault.App.Services;
using System.IO;

namespace FluxVault.App.ViewModels;

public sealed partial class OptionsViewModel
{
    [ObservableProperty] private bool automaticProtectionEnabled;
    [ObservableProperty] private bool automaticProtectionAvailable;
    [ObservableProperty] private string automaticProtectionStatus = "Load the service status first.";
    public bool CanApplyAutomaticProtection => AutomaticProtectionAvailable && CanSaveOptions;
    partial void OnAutomaticProtectionAvailableChanged(bool value)
    { OnPropertyChanged(nameof(CanApplyAutomaticProtection)); ApplyAutomaticProtectionCommand.NotifyCanExecuteChanged(); }

    private void ApplyAutomaticStatus(FluxVaultServiceStatus status, bool loadPreference)
    {
        AutomaticProtectionAvailable = status.AutomaticProtection?.CanChangePreference==true;
        if (loadPreference) AutomaticProtectionEnabled = status.AutomaticProtection?.Policy.Enabled ?? false;
        AutomaticProtectionStatus = status.AutomaticProtection is { } automatic
            ? $"{AutomaticProtectionText.Label(automatic.State)}: {automatic.Reason}" : "Automatic protection is unavailable from this service version.";
    }

    [RelayCommand(CanExecute = nameof(CanApplyAutomaticProtection))]
    public async Task ApplyAutomaticProtectionAsync(CancellationToken cancellationToken = default)
    {
        var entered = false;
        try
        {
            entered = await saveGate.WaitAsync(0, cancellationToken).ConfigureAwait(true);
            if (!entered || !TryReadPendingSave() || !CanApplyAutomaticProtection) return;
            var pending = new PendingConfigurationSave(acceptedVaultId!.Value.Value, Guid.NewGuid(), acceptedRevision!.Value,
                currentConfiguration!, false, [], [], ConfigurationSaveOrigin.Options,
                IsAutomaticProtectionEnabled: AutomaticProtectionEnabled).Freeze();
            saveOperationStore.Reserve(pending); SetPendingSave(pending);
            FluxVaultIpcResponse response;
            try { response = await SendBoundAsync(pending.Request, cancellationToken).ConfigureAwait(true); }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            { response = FluxVaultIpcResponse.Failure(exception.Message) with { ErrorCode = FluxVaultIpcErrorCode.OutcomeUnknown }; }
            if (response.Success && response.ErrorCode is null)
            {
                if (!TryClearPendingSave(pending)) return;
                acceptedRevision = response.VaultRevision;
                StatusText = "Automatic preference saved for the current saved source selection. Other Options edits are kept.";
                AutomaticProtectionStatus = "Preference saved. The service will report session, source and capture status.";
            }
            else
            {
                if (response.ErrorCode is FluxVaultIpcErrorCode.Denied or FluxVaultIpcErrorCode.InvalidRequest or FluxVaultIpcErrorCode.StaleRevision)
                    if (!TryClearPendingSave(pending)) return;
                StatusText = "Automatic preference could not be confirmed: " + response.ErrorMessage + ". Your edits are kept; check the original outcome before retrying.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        { TryReadPendingSave(); StatusText = "Automatic preference did not complete. Your edits and any pending record are kept. " + exception.Message; }
        finally { if (entered) saveGate.Release(); }
    }
}
