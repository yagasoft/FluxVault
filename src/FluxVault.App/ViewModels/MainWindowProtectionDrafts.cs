using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.App.Services;

namespace FluxVault.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IProtectionDraftStore? protectionDraftStore;
    private readonly SemaphoreSlim protectionDraftIoGate = new(1, 1);
    private PendingProtectionDraft? localProtectionDraft;
    private bool localProtectionDraftLoaded;
    private long persistedLocalProtectionDraftGeneration = -1;
    private Task? localProtectionDraftWriter;
    private Task? localProtectionDraftWriterStop;
    private readonly CancellationTokenSource localProtectionDraftWriterCancellation = new();
    private bool localProtectionDraftWriterStopped;

    [ObservableProperty]
    private bool isPreparingForExit;
    partial void OnIsPreparingForExitChanged(bool value) => NotifyConfigurationCommandAvailability();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalProtectionDraftMessage))]
    private string localProtectionDraftMessage = string.Empty;
    public bool HasLocalProtectionDraftMessage => !string.IsNullOrWhiteSpace(LocalProtectionDraftMessage);
    [ObservableProperty]
    private bool requiresLocalProtectionDraftReview;

    partial void OnRequiresLocalProtectionDraftReviewChanged(bool value) => NotifyConfigurationCommandAvailability();

    private async Task LoadLocalProtectionDraftAsync()
    {
        if (protectionDraftStore is null || acceptedConfiguration is null || acceptedVaultId is null || acceptedConfigurationRevision is not > 0) return;
        await protectionDraftIoGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var draft = await Task.Run(protectionDraftStore.Read).ConfigureAwait(true);
            if (localProtectionDraftLoaded)
            {
                if (!SameLocalDraft(localProtectionDraft, draft))
                    RequireLocalDraftReview("The local protection draft changed in another session. Its record and your edits are kept; review before saving or backing up.");
                return;
            }
            localProtectionDraftLoaded = true;
            localProtectionDraft = draft;
            if (draft is null) return;
            var pending = await Task.Run(saveOperationStore.Read).ConfigureAwait(true);
            var linked = pending is not null && IsLinkedLocalDraft(draft,pending);
            var independentDeletion = pending is { Origin: ConfigurationSaveOrigin.HistoryDeletion } && pending.RepositoryId == draft.RepositoryId;
            if (draft.RepositoryId != acceptedVaultId.Value.Value ||
                (pending is not null && !independentDeletion ? !linked : draft.BaselineRevision != acceptedConfigurationRevision ||
                    draft.BaselineFingerprint != PendingProtectionDraft.Fingerprint(acceptedConfiguration) || draft.SaveOperationId is not null))
            {
                RequireLocalDraftReview("The local protection draft does not match the verified saved baseline or has an outstanding save. Its record and your edits are kept; review before saving or backing up.");
                return;
            }
            if (!hasLocalConfigurationChanges)
            {
                RestoreProtectionDraft(draft.Configuration);
                persistedLocalProtectionDraftGeneration = configurationEditGeneration;
                LocalProtectionDraftMessage = linked
                    ? "Your local protection draft was recovered with an outstanding save. Check its original receipt; no save was replayed and no backup has started."
                    : "Your local protection draft was recovered. These changes have not been saved to the service; no backup has started.";
            }
            else if (PendingProtectionDraft.Fingerprint(BuildConfiguration()) != PendingProtectionDraft.Fingerprint(draft.Configuration))
                RequireLocalDraftReview("A different local protection draft was found. Its record and your newer edits are kept; review before saving or backing up.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            RequireLocalDraftReview("The local protection draft could not be read. Its record and your edits are kept; no save or backup will be sent. " + exception.Message);
        }
        finally { protectionDraftIoGate.Release(); }
    }

    private void RequireLocalDraftReview(string message)
    {
        RequiresLocalProtectionDraftReview = true;
        LocalProtectionDraftMessage = message;
        // Prevent a separately retained submitted snapshot from replacing edits
        // whose local record still requires review.
        hasLocalConfigurationChanges = true;
        NotifyOptionsEntryChanged();
    }

    private static bool SameLocalDraft(PendingProtectionDraft? left, PendingProtectionDraft? right) =>
        left is null && right is null || left is not null && right is not null &&
        FileProtectionDraftStore.Encode(left).AsSpan().SequenceEqual(FileProtectionDraftStore.Encode(right));

    private static bool IsLinkedLocalDraft(PendingProtectionDraft draft, PendingConfigurationSave pending) =>
        pending.Origin == ConfigurationSaveOrigin.Protect && draft.DraftId == pending.ProtectionDraftId &&
        draft.RepositoryId == pending.RepositoryId && (draft.SaveOperationId is null || draft.SaveOperationId == pending.OperationId) &&
        (draft.BaselineRevision == pending.Revision && draft.BaselineFingerprint == pending.ProtectionDraftBaselineFingerprint ||
         draft.BaselineRevision == pending.Revision + 1 && draft.BaselineFingerprint == PendingProtectionDraft.Fingerprint(pending.Configuration));

    private async Task<PendingConfigurationSave> ReserveProtectionSaveAsync(PendingConfigurationSave pending, long generation)
    {
        if (protectionDraftStore is null)
        {
            saveOperationStore.Reserve(pending); SetPendingConfigurationSave(pending); return pending;
        }
        await protectionDraftIoGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!localProtectionDraftLoaded || RequiresLocalProtectionDraftReview)
                throw new InvalidOperationException("The local draft must be loaded and reviewed before sending a save.");
            if (generation != configurationEditGeneration || pending.RepositoryId != acceptedVaultId?.Value || pending.Revision != acceptedConfigurationRevision)
                throw new InvalidOperationException("Newer edits or a changed saved baseline arrived before save preparation. Review and save again.");
            await PersistCurrentLocalDraftAsync().ConfigureAwait(true);
            var draft = localProtectionDraft!;
            pending = (pending with { Configuration=pending.IsProtectionPaused is null ? draft.Configuration : pending.Configuration, ProtectionDraftId=draft.DraftId,
                ProtectionDraftBaselineFingerprint=draft.BaselineFingerprint }).Freeze();
            saveOperationStore.Reserve(pending);
            SetPendingConfigurationSave(pending);
            localProtectionDraft = await Task.Run(() => protectionDraftStore.Write(draft,draft with
                { RecordId=Guid.NewGuid(), SaveOperationId=pending.OperationId })).ConfigureAwait(true);
            return pending;
        }
        finally { protectionDraftIoGate.Release(); }
    }

    // The caller holds this gate through receipt cleanup and the UI dirty-state
    // transition. A coalesced writer cannot recreate a retired submitted draft.
    private async Task<bool> CompleteLocalProtectionSaveAsync(PendingConfigurationSave pending, bool saved)
    {
        if (protectionDraftStore is null) return true;
        try
        {
            var current = await Task.Run(protectionDraftStore.Read).ConfigureAwait(true);
            if (!SameLocalDraft(current,localProtectionDraft) || current is not null && !IsLinkedLocalDraft(current,pending))
                throw new InvalidOperationException("The local draft changed or no longer matches the submitted save.");
            if (RequiresLocalProtectionDraftReview)
                throw new InvalidOperationException("The local protection draft requires review.");
            if (current is null)
            {
                // A crash may follow exact retirement and precede receipt cleanup.
                // Retain any new edits before releasing that original receipt.
                if (PendingProtectionDraft.Fingerprint(BuildConfiguration()) != PendingProtectionDraft.Fingerprint(pending.Configuration))
                    await RetainLatestLocalEditsAsync(pending,saved).ConfigureAwait(true);
                return true;
            }
            var configuration = BuildConfiguration();
            var generation = configurationEditGeneration;
            if (saved && PendingProtectionDraft.Fingerprint(configuration) == PendingProtectionDraft.Fingerprint(pending.Configuration))
            {
                await Task.Run(() => protectionDraftStore.Clear(current)).ConfigureAwait(true);
                localProtectionDraft = null;
                // If newer edits arrived during deletion, retain them before
                // releasing the receipt, preserving its editing-session link.
                if (generation != configurationEditGeneration)
                    await RetainLatestLocalEditsAsync(pending,saved).ConfigureAwait(true);
                else persistedLocalProtectionDraftGeneration = generation;
            }
            else
            {
                var next = current with { RecordId=Guid.NewGuid(), Configuration=configuration, SaveOperationId=null,
                    BaselineRevision=saved ? pending.Revision+1 : pending.Revision,
                    BaselineFingerprint=saved ? PendingProtectionDraft.Fingerprint(pending.Configuration) : pending.ProtectionDraftBaselineFingerprint! };
                localProtectionDraft = await Task.Run(() => protectionDraftStore.Write(current,next)).ConfigureAwait(true);
                persistedLocalProtectionDraftGeneration = generation;
                // Recheck edits after asynchronous publication. The writer gate
                // remains held while the newest complete snapshot is retained.
                if (persistedLocalProtectionDraftGeneration != configurationEditGeneration)
                    await RetainLatestLocalEditsAsync(pending,saved).ConfigureAwait(true);
            }
            LocalProtectionDraftMessage = localProtectionDraft is null ? string.Empty :
                "Your newer protection draft is retained locally. It has not been saved to the service.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            RequireLocalDraftReview("The local draft could not be reconciled with the save outcome. Both records and your edits are kept; no backup will start. " + exception.Message);
            saveConfirmedForReview = true; // A definite acknowledgement/rejection was already checked by the caller.
            NotifyConfigurationCommandAvailability();
            return false;
        }
    }

    private void ScheduleLocalProtectionDraft()
    {
        if (protectionDraftStore is null || !localProtectionDraftLoaded || RequiresLocalProtectionDraftReview || localProtectionDraftWriterStopped) return;
        LocalProtectionDraftMessage = "Your protection edits are pending local retention. They have not been saved to the service.";
        if (localProtectionDraftWriter is null || localProtectionDraftWriter.IsCompleted)
            localProtectionDraftWriter = WriteLocalProtectionDraftsAsync();
    }

    private async Task WriteLocalProtectionDraftsAsync()
    {
        var cancellationToken = localProtectionDraftWriterCancellation.Token;
        try
        {
            while (!localProtectionDraftWriterStopped)
            {
                // A rate-limited writer takes the latest snapshot at each delay,
                // so continuous typing cannot indefinitely postpone retention.
                var delay = (acceptedConfiguration?.ProtectionDraft ?? new()).Normalise().SaveDelayMilliseconds;
                await Task.Delay(delay,cancellationToken).ConfigureAwait(true);
                await protectionDraftIoGate.WaitAsync(cancellationToken).ConfigureAwait(true);
                try
                {
                    if (localProtectionDraftWriterStopped || RequiresLocalProtectionDraftReview || !hasLocalConfigurationChanges ||
                        persistedLocalProtectionDraftGeneration == configurationEditGeneration) return;
                    await PersistCurrentLocalDraftAsync().ConfigureAwait(true);
                    if (persistedLocalProtectionDraftGeneration == configurationEditGeneration) return;
                }
                finally { protectionDraftIoGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (exception is InvalidOperationException)
                RequireLocalDraftReview("The local protection draft changed. Its record and your edits are kept; review before saving or backing up. " + exception.Message);
            else LocalProtectionDraftMessage = "The local protection draft could not be retained. Your edits are kept in this window; retry before closing. " + exception.Message;
        }
    }

    private async Task RetainLatestLocalEditsAsync(PendingConfigurationSave? pending=null, bool saved=false)
    {
        // Bounded optimistic stabilisation: continuous editing cannot hold a
        // receipt or Exit indefinitely. A further edit keeps the receipt/window.
        for (var attempt=0; attempt<4; attempt++)
        {
            await PersistCurrentLocalDraftAsync(pending?.ProtectionDraftId,
                pending is null ? null : pending.Revision + (saved ? 1 : 0),
                pending is null ? null : saved ? PendingProtectionDraft.Fingerprint(pending.Configuration) : pending.ProtectionDraftBaselineFingerprint).ConfigureAwait(true);
            if (persistedLocalProtectionDraftGeneration == configurationEditGeneration) return;
        }
        throw new InvalidOperationException("Newer edits continue to arrive during retention. They remain in this window; retry after editing.");
    }

    private async Task PersistCurrentLocalDraftAsync(Guid? continuingDraftId=null,long? baselineRevision=null,string? baselineFingerprint=null)
    {
        if (acceptedConfiguration is null || acceptedVaultId is null || acceptedConfigurationRevision is not > 0)
            throw new InvalidOperationException("A verified saved baseline is required for local draft retention.");
        var generation = configurationEditGeneration;
        var expected = localProtectionDraft;
        var pending = unconfirmedProtectionSave is { Origin:ConfigurationSaveOrigin.Protect, ProtectionDraftId:not null } correlated &&
            correlated.RepositoryId == acceptedVaultId.Value.Value ? correlated : null;
        var next = new PendingProtectionDraft(expected?.DraftId ?? continuingDraftId ?? pending?.ProtectionDraftId ?? Guid.NewGuid(), Guid.NewGuid(), acceptedVaultId.Value.Value,
            baselineRevision ?? (expected is not null && HasUnconfirmedProtectionSave ? expected.BaselineRevision : pending?.Revision ?? acceptedConfigurationRevision.Value),
            baselineFingerprint ?? (expected is not null && HasUnconfirmedProtectionSave ? expected.BaselineFingerprint : pending?.ProtectionDraftBaselineFingerprint ?? PendingProtectionDraft.Fingerprint(acceptedConfiguration)),
            BuildConfiguration(), expected?.SaveOperationId);
        localProtectionDraft = await Task.Run(() => protectionDraftStore!.Write(expected,next)).ConfigureAwait(true);
        persistedLocalProtectionDraftGeneration = generation;
        LocalProtectionDraftMessage = generation == configurationEditGeneration
            ? "Your protection draft is retained locally. These changes have not been saved to the service."
            : "Newer protection edits are pending local retention. They have not been saved to the service.";
    }

    public async Task<bool> PrepareForExitAsync()
    {
        if (protectionDraftStore is null) return true;
        if (IsPreparingForExit) return false;
        if (IsProtectionSaveBusy || isOptionsEditing)
        {
            LocalProtectionDraftMessage = "Exit stopped: finish the current save or Options session first. Your edits are kept.";
            return false;
        }
        IsPreparingForExit = true;
        localProtectionDraftWriterStopped = true;
        var retained = false;
        await protectionDraftIoGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (RequiresLocalProtectionDraftReview)
            {
                LocalProtectionDraftMessage = "Exit stopped: the local draft needs review. Its record and your current edits are kept.";
                return false;
            }
            if (!hasLocalConfigurationChanges) { retained = true; }
            else
            {
                if (!localProtectionDraftLoaded || acceptedConfiguration is null || acceptedVaultId is null || acceptedConfigurationRevision is not > 0)
                {
                    LocalProtectionDraftMessage = "Exit stopped: a verified configuration baseline is needed to retain the pending edits.";
                    return false;
                }
                if (persistedLocalProtectionDraftGeneration != configurationEditGeneration)
                    await PersistCurrentLocalDraftAsync().ConfigureAwait(true);
                if (persistedLocalProtectionDraftGeneration != configurationEditGeneration)
                {
                    LocalProtectionDraftMessage = "Newer edits arrived during local persistence. Exit stopped; retain these edits before closing.";
                    return false;
                }
                retained = true;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            LocalProtectionDraftMessage = "Exit stopped: the local protection draft could not be retained. Your edits are kept in this window. " + exception.Message;
            return false;
        }
        finally
        {
            protectionDraftIoGate.Release();
            if (!retained) { localProtectionDraftWriterStopped = false; IsPreparingForExit = false; }
        }
        await CancelAndJoinLocalProtectionDraftWriterAsync().ConfigureAwait(true);
        return true;
    }

    private async Task DiscardLocalProtectionDraftAsync(PendingConfigurationSave? reviewedSave)
    {
        localProtectionDraftWriterStopped = true;
        await protectionDraftIoGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var generation = configurationEditGeneration;
            var reviewedDraft = localProtectionDraft;
            ProtectionDraftEvidence? corrupt = null;
            if (reviewedDraft is null)
            {
                try
                {
                    if (await Task.Run(protectionDraftStore!.Read).ConfigureAwait(true) is not null)
                        throw new InvalidOperationException("A different local draft appeared. Review it before discarding.");
                }
                catch (InvalidDataException) { corrupt = await Task.Run(protectionDraftStore!.InspectPreservedRecord).ConfigureAwait(true); }
            }
            var response = await SendBoundAsync(FluxVaultIpcRequest.GetStatus()).ConfigureAwait(true);
            if (!response.Success || response.Status is null || response.VaultId != acceptedVaultId ||
                generation != configurationEditGeneration || !ValidateDiscardReview(reviewedSave))
                throw new InvalidOperationException("Saved settings could not be verified, or newer edits or a different pending save arrived.");
            string? quarantine = null;
            if (corrupt is not null)
                quarantine = await Task.Run(() => protectionDraftStore!.Quarantine(corrupt)).ConfigureAwait(true);
            else
            {
                var current = await Task.Run(protectionDraftStore!.Read).ConfigureAwait(true);
                if (!SameLocalDraft(reviewedDraft,current))
                    throw new InvalidOperationException("The local draft changed after review.");
                if (generation != configurationEditGeneration || !ValidateDiscardReview(reviewedSave))
                    throw new InvalidOperationException("Newer edits or a different pending save arrived during draft review.");
                if (current is not null)
                    await Task.Run(() => protectionDraftStore!.Clear(current)).ConfigureAwait(true);
            }
            localProtectionDraft = null;
            persistedLocalProtectionDraftGeneration = -1;
            if (generation != configurationEditGeneration)
                await RetainLatestLocalEditsAsync(reviewedSave,saved:reviewedSave is not null).ConfigureAwait(true);
            if (generation != configurationEditGeneration || !ValidateDiscardReview(reviewedSave))
                throw new InvalidOperationException("Newer edits or a different pending save arrived during draft retirement.");
            if (reviewedSave is not null && !TryClearPendingConfigurationSave(reviewedSave)) return;
            RequiresLocalProtectionDraftReview = false;
            localProtectionDraftLoaded = true;
            ApplyStatus(response.Status,preserveLocalConfiguration:false,forceConfigurationReload:true,
                repositoryId:response.VaultId,revision:response.VaultRevision);
            requiresSaveStatusCheck = false;
            ProtectionSaveState = ProtectionSaveState.Idle;
            ProtectionSaveMessage = string.Empty;
            LocalProtectionDraftMessage = quarantine is null ? string.Empty : "The unreadable local draft was preserved at " + quarantine;
            FileBrowser.RefreshBrowser();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            ProtectionSaveMessage = "Discard stopped: your edits and available records are kept. " + exception.Message;
            SetServiceStatus("Service connection: " + ProtectionSaveMessage);
        }
        finally { protectionDraftIoGate.Release(); localProtectionDraftWriterStopped = false; }
    }

    internal Task CancelAndJoinLocalProtectionDraftWriterAsync() => localProtectionDraftWriterStop ??= StopLocalProtectionDraftWriterCoreAsync();

    private async Task StopLocalProtectionDraftWriterCoreAsync()
    {
        localProtectionDraftWriterStopped = true;
        localProtectionDraftWriterCancellation.Cancel();
        try { if (localProtectionDraftWriter is { } writer) await writer.ConfigureAwait(true); }
        finally { localProtectionDraftWriterCancellation.Dispose(); }
    }
}
