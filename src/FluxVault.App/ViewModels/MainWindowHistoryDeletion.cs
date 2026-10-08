using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;

namespace FluxVault.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private RepositoryHistoryDeletionPreview? historyDeletionPreview;

    [ObservableProperty]
    private string historyDeletionMessage = "Stopping protection keeps history subject to retention. Select a history row to review deliberate deletion of all versions in its file or folder scope.";

    private VaultId? historyReviewVaultId;
    private long? historyReviewRevision;
    private bool IsHistoryDeletionPending => unconfirmedProtectionSave?.Origin == ConfigurationSaveOrigin.HistoryDeletion;

    public bool CanPreviewHistoryDeletion => CanSaveProtection && !HasUnconfirmedBackup && SelectedVersion is not null &&
        acceptedVaultId is not null && acceptedConfiguration is not null && acceptedConfigurationRevision is > 0;
    public bool CanDeleteHistory => CanPreviewHistoryDeletion && HistoryDeletionPreview is { CanDelete: true, IsComplete: true } preview &&
        historyReviewVaultId == acceptedVaultId && historyReviewRevision == acceptedConfigurationRevision && ScopeMatchesSelection(preview.Scope);
    public bool CanCheckHistoryDeletionOutcome => !IsPreparingForExit && !IsProtectionSaveBusy && !saveRecordBlocked &&
        unconfirmedProtectionSave is { Origin: ConfigurationSaveOrigin.HistoryDeletion } pending && pending.RepositoryId == acceptedVaultId?.Value;

    partial void OnHistoryDeletionPreviewChanged(RepositoryHistoryDeletionPreview? value) => NotifyHistoryDeletionAvailability();

    private void NotifyHistoryDeletionAvailability()
    {
        OnPropertyChanged(nameof(CanPreviewHistoryDeletion));
        OnPropertyChanged(nameof(CanDeleteHistory));
        OnPropertyChanged(nameof(CanCheckHistoryDeletionOutcome));
        PreviewHistoryDeletionCommand.NotifyCanExecuteChanged();
        DeleteHistoryCommand.NotifyCanExecuteChanged();
        CheckHistoryDeletionOutcomeCommand.NotifyCanExecuteChanged();
    }

    private RepositoryPurgeScope SelectedHistoryDeletionScope() => new(SelectedVersion!.SourcePath,
        SelectedVersion.EntryKind == RepositoryEntryKind.Folder ? RepositoryPurgeScopeKind.RecursiveFolder : RepositoryPurgeScopeKind.File);

    private bool ScopeMatchesSelection(RepositoryPurgeScope scope) => SelectedVersion is not null &&
        scope.Kind == SelectedHistoryDeletionScope().Kind && string.Equals(scope.SourcePath,SelectedVersion.SourcePath,StringComparison.OrdinalIgnoreCase);

    private void InvalidateHistoryDeletionPreview()
    {
        if (HistoryDeletionPreview is { } preview && !ScopeMatchesSelection(preview.Scope))
        {
            HistoryDeletionPreview = null;
            HistoryDeletionMessage = "History selection changed. Review the new exact scope before deleting anything.";
        }
        NotifyHistoryDeletionAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanPreviewHistoryDeletion))]
    private async Task PreviewHistoryDeletionAsync()
    {
        if (!TryLoadPendingConfigurationSave() || !CanPreviewHistoryDeletion) return;
        var scope = SelectedHistoryDeletionScope();
        var vault = acceptedVaultId;
        var revision = acceptedConfigurationRevision;
        IsProtectionSaveBusy = true;
        HistoryDeletionPreview = null;
        HistoryDeletionMessage = "Reviewing history and retained references; no history is being deleted…";
        try
        {
            var response = await SendBoundAsync(FluxVaultIpcRequest.PreviewHistoryDeletion(scope) with
                { VaultId = vault, ExpectedVaultRevision = revision }).ConfigureAwait(true);
            if (!response.Success || response.VaultId != vault || response.VaultRevision != revision ||
                response.HistoryDeletionPreview is not { } preview || preview.Scope != scope ||
                preview.Fingerprint is not { Length: 64 } || !preview.Fingerprint.All(Uri.IsHexDigit) ||
                preview.Candidates is null || preview.BlockingDependencies is null || preview.Warnings is null ||
                preview.CandidateVersionCount < 0 || preview.CandidateLogicalBytes < 0 ||
                preview.IsComplete && preview.Candidates.Count != preview.CandidateVersionCount ||
                !ScopeMatchesSelection(scope) || acceptedVaultId != vault || acceptedConfigurationRevision != revision)
            {
                HistoryDeletionMessage = "A complete current history review could not be confirmed. Nothing was deleted. " + response.ErrorMessage;
                return;
            }
            historyReviewVaultId = vault;
            historyReviewRevision = revision;
            HistoryDeletionPreview = preview;
            HistoryDeletionMessage = $"{preview.Scope.Kind}: {preview.Scope.SourcePath} — {preview.CandidateVersionCount:N0} version(s), {preview.CandidateLogicalBytes:N0} logical bytes. " +
                (preview.CanDelete && preview.IsComplete ? "Review every candidate below. Deletion requires separate confirmation and cannot be undone by software rollback. Live source files remain." :
                    "Deletion is unavailable. " + string.Join(" ",preview.Warnings));
        }
        catch (Exception exception) when (IsHistoryOperationException(exception))
        {
            HistoryDeletionMessage = "History review failed. Nothing was deleted. " + exception.Message;
        }
        finally { IsProtectionSaveBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteHistory))]
    private async Task DeleteHistoryAsync()
    {
        if (!TryLoadPendingConfigurationSave() || !CanDeleteHistory) return;
        var preview = HistoryDeletionPreview!;
        var vault = historyReviewVaultId!.Value;
        var revision = historyReviewRevision!.Value;
        IsProtectionSaveBusy = true;
        var dispatched = false;
        try
        {
            if (!protectionRemovalConfirmation.ConfirmHistoryDeletion(preview))
            {
                HistoryDeletionMessage = "History deletion cancelled. History, source files and your pending protection edits are kept.";
                return;
            }
            // A modal confirmation can pump selection/status events. Never
            // retarget the reviewed action to a newer row or configuration.
            if (HistoryDeletionPreview != preview || !ScopeMatchesSelection(preview.Scope) ||
                acceptedVaultId != vault || acceptedConfigurationRevision != revision)
            {
                HistoryDeletionPreview = null;
                HistoryDeletionMessage = "The reviewed scope or saved configuration changed during confirmation. Nothing was deleted; review again.";
                return;
            }
            var pending = new PendingConfigurationSave(vault.Value,Guid.NewGuid(),revision,acceptedConfiguration!,false,
                [preview.Scope],[],ConfigurationSaveOrigin.HistoryDeletion,HistoryDeletionFingerprint:preview.Fingerprint).Freeze();
            await ReserveHistoryDeletionAsync(pending).ConfigureAwait(true);
            dispatched = true;
            var response = await SendBoundAsync(pending.Request).ConfigureAwait(true);
            FinishHistoryDeletion(pending,response,receipt:false);
        }
        catch (Exception exception) when (IsHistoryOperationException(exception))
        {
            TryLoadPendingConfigurationSave();
            HistoryDeletionMessage = dispatched || HasUnconfirmedProtectionSave
                ? "History deletion outcome is uncertain. Keep its original operation and check the outcome; do not submit another deletion. " + exception.Message
                : "History deletion preparation failed. Nothing was dispatched. Your edits are kept. " + exception.Message;
        }
        finally { IsProtectionSaveBusy = false; }
    }

    private async Task ReserveHistoryDeletionAsync(PendingConfigurationSave pending)
    {
        await protectionDraftIoGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (RequiresLocalProtectionDraftReview || pending.RepositoryId != acceptedVaultId?.Value || pending.Revision != acceptedConfigurationRevision)
                throw new InvalidOperationException("The saved baseline or local draft requires a fresh review.");
            if (protectionDraftStore is not null)
            {
                if (!localProtectionDraftLoaded) throw new InvalidOperationException("The independent editing draft has not been loaded.");
                if (hasLocalConfigurationChanges) await PersistCurrentLocalDraftAsync().ConfigureAwait(true);
            }
            // Deletion changes no configuration revision. The editing draft
            // remains independent and must never be linked to this operation.
            saveOperationStore.Reserve(pending);
            SetPendingConfigurationSave(pending);
        }
        finally { protectionDraftIoGate.Release(); }
    }

    [RelayCommand(CanExecute = nameof(CanCheckHistoryDeletionOutcome))]
    private async Task CheckHistoryDeletionOutcomeAsync()
    {
        if (!TryLoadPendingConfigurationSave() || !CanCheckHistoryDeletionOutcome ||
            unconfirmedProtectionSave is not { Origin: ConfigurationSaveOrigin.HistoryDeletion } pending) return;
        IsProtectionSaveBusy = true;
        try
        {
            var response = await SendBoundAsync(new(FluxVaultIpcCommand.GetOperationStatus,null,null,null,null,
                VaultId:new(pending.RepositoryId),OperationId:pending.OperationId)).ConfigureAwait(true);
            FinishHistoryDeletion(pending,response,receipt:true);
        }
        catch (Exception exception) when (IsHistoryOperationException(exception))
        {
            HistoryDeletionMessage = "The original deletion outcome is still uncertain. Its record and your edits are kept; no deletion was repeated. " + exception.Message;
        }
        finally { IsProtectionSaveBusy = false; }
    }

    private void FinishHistoryDeletion(PendingConfigurationSave pending,FluxVaultIpcResponse response,bool receipt)
    {
        var bound = response.VaultId?.Value == pending.RepositoryId && response.OperationId == pending.OperationId &&
            (response.VaultRevision == pending.Revision || !receipt && response.VaultRevision is null);
        var deleted = bound && response.VaultRevision == pending.Revision && response.Success && response.ErrorCode is null &&
            response.Purge is { Success: true, PurgedVersionCount: > 0, MirrorWarnings: not null };
        var refused = bound && !response.Success && response.Purge is null && response.ErrorCode is
            FluxVaultIpcErrorCode.HistoryChanged or FluxVaultIpcErrorCode.InvalidRequest or FluxVaultIpcErrorCode.StaleRevision or FluxVaultIpcErrorCode.Denied;
        HistoryDeletionPreview = null;
        if (!deleted && !refused)
        {
            requiresSaveStatusCheck = true;
            HistoryDeletionMessage = "History deletion outcome is uncertain or partial. Its original operation is retained. Check the outcome before changing protection or repository state; no deletion will be replayed. History and recovery remain available.";
            ProtectionSaveMessage = "A history deletion outcome is pending. Check its original outcome in Repository before changing protection. Your edits are kept.";
            return;
        }
        if (!TryClearPendingConfigurationSave(pending))
        {
            HistoryDeletionMessage = "The deletion outcome was returned, but its local record could not be cleared. Check the same original outcome before continuing.";
            return;
        }
        HistoryDeletionMessage = deleted
            ? $"Deleted {response.Purge!.PurgedVersionCount:N0} reviewed version(s). Source files and pending protection edits are kept. " + string.Join(" ",response.Purge.MirrorWarnings!)
            : "History deletion was refused before changes. Review the current history again. " + response.ErrorMessage;
        ProtectionSaveMessage = "Your protection edits have not been saved by history deletion.";
    }

    private static bool IsHistoryOperationException(Exception exception) => exception is IOException or TimeoutException or
        OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;
}
