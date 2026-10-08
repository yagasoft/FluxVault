using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.TestHost;

internal static class WindowsProtectionDraftProbe
{
    internal static async Task RunAsync(IFluxVaultServiceClient client,string output,string document,List<string> checks,CancellationToken token)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            _=dispatcher.InvokeAsync(async ()=>
            {
                try { await RunOnUiThreadAsync(client,output,document,checks,token); completion.TrySetResult(); }
                catch(Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(token); }
        finally { if(!thread.Join(TimeSpan.FromSeconds(5)))throw new IOException("Native protection WPF thread did not exit."); }
        checks.Add("native protection WPF dispatcher exited and its thread was joined");
    }

    private static async Task RunOnUiThreadAsync(IFluxVaultServiceClient client,string output,string document,List<string> checks,CancellationToken token)
    {
        RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var root=Path.Combine(output,"local-protection-draft"); Directory.CreateDirectory(root);
        var drafts=new FileProtectionDraftStore(Path.Combine(root,"draft.json"));
        var saves=new FileConfigurationSaveOperationStore(Path.Combine(root,"save.json"));
        var observed=new ObservedClient(client,drafts,saves,token);
        var confirmation=new HistoryConfirmation();
        MainWindowViewModel Model()=>new(observed,TimeSpan.FromHours(1),new FileBrowserViewModel(new Files()),new Controller(),new Destination(),new Overwrite(),
            protectionRemovalConfirmation:confirmation,backupOperationStore:new FileBackupOperationStore(Path.Combine(root,"backup.json")),saveOperationStore:saves,protectionDraftStore:drafts);
        var model=Model(); MainWindowViewModel? reopened=null; MainWindowViewModel? deletionReopened=null;
        var window=new FluxVault.App.MainWindow(new FileDataGridLayoutStore(Path.Combine(root,"layout.json")))
            {DataContext=model,Width=1440,Height=900,ShowInTaskbar=false};
        try
        {
            window.Show();
            await model.RefreshAsync();
            var before=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),token);
            var source=Path.GetDirectoryName(document)!;
            var rule=model.FileBrowser.GetSelectionRules().SingleOrDefault(item=>item.Path==source) ??
                new ProtectionSelectionRule("native-local-draft",source,ProtectionSelectionMode.RecursiveFolder,
                    CompressionPreference.Off,ResourceProfile.Balanced,true);
            model.FileBrowser.ReplaceSelectionRule(rule with{IncludeRegexRules=[new("local-draft-docx",@".*\.docx$",ProtectionExclusionTarget.File)]});
            window.UpdateLayout();
            var pause=(Button)window.FindName("PauseProtectionButton");
            var resume=(Button)window.FindName("ResumeProtectionButton");
            Check(pause.IsEnabled && !resume.IsEnabled,"native WPF offers explicit Pause for the enabled real vault");
            await ((IAsyncRelayCommand)pause.Command).ExecuteAsync(null);
            var paused=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),token);
            Check(paused.Success && !paused.Status!.Configuration.IsEnabled && paused.VaultRevision==before.VaultRevision+1 &&
                model.FileBrowser.PendingChanges.Count>0 && observed.SaveCount==0 && observed.BackupCount==0,
                "native WPF Pause reaches the catalogue without saving the independent selection draft");
            window.UpdateLayout();
            Check(!pause.IsEnabled && resume.IsEnabled,"native WPF switches availability after the atomic pause acknowledgement");
            await ((IAsyncRelayCommand)resume.Command).ExecuteAsync(null);
            before=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),token);
            Check(before.Status!.Configuration.IsEnabled && before.VaultRevision==paused.VaultRevision+1 && model.FileBrowser.PendingChanges.Count>0,
                "native WPF Resume preserves pending edits and restores capture authority");
            Check(await model.PrepareForExitAsync(),"native unsent Protect edit force-flushes and joins before exit");
            await model.StopRepositoryReadsAsync();
            var draft=drafts.Read()!;
            Check(draft.SaveOperationId is null && saves.Read() is null && observed.SaveCount==0 && observed.BackupCount==0,
                "native local draft retention sends no save or backup through the authenticated pipe");
            var unchanged=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),token);
            Check(unchanged.VaultRevision==before.VaultRevision && JsonSerializer.Serialize(unchanged.Status!.Configuration)==JsonSerializer.Serialize(before.Status!.Configuration),
                "native unsent draft leaves real PostgreSQL configuration and revision unchanged");
            reopened=Model(); await reopened.RefreshAsync();
            window.DataContext=reopened;
            Check(reopened.FileBrowser.GetSelectionRules().Single(item=>item.Path==rule.Path).IncludeRegexRules?.Single().Id=="local-draft-docx" &&
                !reopened.RequiresLocalProtectionDraftReview && observed.SaveCount==0 && observed.BackupCount==0,
                "native actual view-model restart restores editable draft without replay");
            await File.WriteAllTextAsync(document,"generated native draft recovery bytes "+Guid.NewGuid().ToString("N"),token);
            await reopened.RunBackupNowCommand.ExecuteAsync(null);
            if (!observed.AssociationVerified || observed.SaveCount!=1 || observed.BackupCount!=1 || drafts.Read() is not null || saves.Read() is not null)
                throw new InvalidOperationException($"Native draft/save sequencing: associated={observed.AssociationVerified}, saves={observed.SaveCount}, backups={observed.BackupCount}, state={reopened.ProtectionSaveState}, message={reopened.ProtectionSaveMessage}, draft={drafts.Read()?.RecordId}, pending={saves.Read()?.OperationId}");
            Check(observed.AssociationVerified && observed.SaveCount==1 && observed.BackupCount==1 && drafts.Read() is null && saves.Read() is null,
                "native save association is durable before dispatch and confirmed save permits exactly one dependent backup");
            var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),token);
            Check(status.Success && status.VaultRevision==before.VaultRevision+1 &&
                status.Status!.Configuration.SelectionRules.Single(item=>item.Path==rule.Path).IncludeRegexRules?.Single().Id=="local-draft-docx",
                "native explicit save publishes recovered selection edit at the exact successor revision");
            var history=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with{VaultId=status.VaultId},token);
            var version=history.Versions!.Where(v=>v.SourcePath==document).OrderByDescending(v=>v.CapturedAtUtc).First();
            var destination=Path.Combine(output,"destination","after-local-draft.docx");
            var recovery=await client.SendAsync(FluxVaultIpcRequest.RestoreVersion(version.VersionId,destination) with
                {VaultId=status.VaultId,ExpectedVaultRevision=status.VaultRevision,OperationId=Guid.NewGuid()},token);
            Check(recovery.Success && recovery.RestoreResult?.VerifiedLogicalBytes==new FileInfo(document).Length && Hash(document)==Hash(destination),
                "native recovered draft flows through save backup history and independently verified recovery");
            var retired=Path.Combine(root,"owned-retired-folder"); Directory.CreateDirectory(retired);
            var retiredFile=Path.Combine(retired,"delete-only-synthetic.txt");
            await File.WriteAllTextAsync(retiredFile,"unique deliberately deletable fixture bytes "+Guid.NewGuid().ToString("N"),token);
            var retiredHash=Hash(retiredFile);
            reopened.FileBrowser.ReplaceSelectionRule(new("owned-deletion",retired,ProtectionSelectionMode.RecursiveFolder,
                CompressionPreference.Off,ResourceProfile.Balanced,true));
            await reopened.RunBackupNowCommand.ExecuteAsync(null);
            await reopened.RefreshAsync();
            var captured=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=status.VaultId},token);
            var folder=captured.Versions!.Where(v=>v.SourcePath==retired && v.EntryKind==RepositoryEntryKind.Folder).OrderByDescending(v=>v.CapturedAtUtc).First();
            reopened.SelectedVersion=reopened.RecentVersions.Single(value=>value.VersionId==folder.VersionId);
            reopened.SelectedWorkspaceIndex=2; window.UpdateLayout();
            var review=(Button)window.FindName("ReviewHistoryDeletionButton");
            var delete=(Button)window.FindName("DeleteHistoryButton");
            await ((IAsyncRelayCommand)review.Command).ExecuteAsync(null); window.UpdateLayout();
            Check(reopened.HistoryDeletionPreview is {CanDelete:false,IsComplete:true} && !delete.IsEnabled && observed.DeleteCount==0,
                "native WPF complete preview refuses currently protected history without deletion");
            var protectedReview=reopened.HistoryDeletionPreview;
            await reopened.RefreshAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReferenceEquals(protectedReview,reopened.HistoryDeletionPreview) && reopened.SelectedVersion?.VersionId==folder.VersionId,
                "native rendered history selection and protected review survive unchanged authenticated status refresh");
            reopened.SelectedWorkspaceIndex=1;
            reopened.FileBrowser.SelectedFolder=new(retired,"owned retired folder",true,null); window.UpdateLayout();
            var stop=(Button)window.FindName("StopProtectingKeepHistoryButton");
            Check(stop.IsEnabled,"native WPF offers the named Stop protecting, keep history action for the selected synthetic folder");
            stop.Command.Execute(null);
            await reopened.SaveConfigurationCommand.ExecuteAsync(null);
            reopened.SelectedWorkspaceIndex=2; window.UpdateLayout();
            captured=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=status.VaultId},token);
            Check(captured.Versions!.Any(v=>v.VersionId==folder.VersionId) && confirmation.Calls==0 && Hash(retiredFile)==retiredHash,
                "native stop-protecting Save keeps synthetic folder history and never asks for deletion confirmation");
            await ((IAsyncRelayCommand)review.Command).ExecuteAsync(null); window.UpdateLayout();
            Check(reopened.HistoryDeletionPreview is {CanDelete:true,IsComplete:true} && delete.IsEnabled,
                "native WPF offers deliberate deletion only after a complete unprotected scope review");
            var unprotectedReview=reopened.HistoryDeletionPreview;
            await reopened.RefreshAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReferenceEquals(unprotectedReview,reopened.HistoryDeletionPreview) && delete.IsEnabled,
                "native rendered deletion remains available after unchanged authenticated status refresh");
            await ((IAsyncRelayCommand)delete.Command).ExecuteAsync(null);
            Check(confirmation.Calls==1 && observed.DeleteCount==0 && saves.Read() is null,
                "native WPF cancelled confirmation admits no operation and deletes no history");
            var independent=rule with {IncludeRegexRules=[new("after-delete-draft",@".*\.docx$",ProtectionExclusionTarget.File)]};
            reopened.FileBrowser.ReplaceSelectionRule(independent);
            confirmation.Allow=true; observed.LoseDeletionAcknowledgement=true;
            var savesBefore=observed.SaveCount; var backupsBefore=observed.BackupCount;
            await ((IAsyncRelayCommand)delete.Command).ExecuteAsync(null);
            var original=saves.Read()!;
            Check(original.Origin==ConfigurationSaveOrigin.HistoryDeletion && reopened.HasUnconfirmedProtectionSave && observed.DeleteCount==1 &&
                observed.SaveCount==savesBefore && observed.BackupCount==backupsBefore && drafts.Read()!.SaveOperationId is null,
                "native confirmed deletion has durable exact intent before dispatch and retains independent edits after acknowledgement loss");
            Check(await reopened.PrepareForExitAsync(),"native unknown deletion flushes independent draft and joins before desktop restart");
            await reopened.StopRepositoryReadsAsync();
            deletionReopened=Model(); await deletionReopened.RefreshAsync(); window.DataContext=deletionReopened; window.UpdateLayout();
            Check(!deletionReopened.RequiresLocalProtectionDraftReview && deletionReopened.FileBrowser.PendingChanges.Count>0 &&
                deletionReopened.FileBrowser.GetSelectionRules().Single(v=>v.Path==rule.Path).IncludeRegexRules!.Single().Id=="after-delete-draft",
                "native desktop restart retains the independent dirty draft alongside the original uncertain deletion");
            var check=(Button)window.FindName("CheckHistoryDeletionButton");
            Check(check.IsEnabled,"native WPF offers the original deletion receipt check after restart");
            await ((IAsyncRelayCommand)check.Command).ExecuteAsync(null);
            Check(saves.Read() is null && !deletionReopened.HasUnconfirmedProtectionSave && observed.DeleteCount==1 && observed.LastDeletionReceipt==original.OperationId,
                "native original completed deletion is reconciled without replay or retargeting");
            var afterDeletion=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=status.VaultId},token);
            Check(!afterDeletion.Versions!.Any(v=>v.SourcePath==retired || v.SourcePath.StartsWith(retired+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) &&
                Hash(retiredFile)==retiredHash,"native PostgreSQL history deletion removes only reviewed history and preserves live synthetic source bytes");
            var neighbour=afterDeletion.Versions!.Where(v=>v.SourcePath==document).OrderByDescending(v=>v.CapturedAtUtc).First();
            var afterDeleteRecovery=Path.Combine(output,"destination","after-reviewed-delete.docx");
            var current=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),token);
            var recovered=await client.SendAsync(FluxVaultIpcRequest.RestoreVersion(neighbour.VersionId,afterDeleteRecovery) with
                {VaultId=current.VaultId,ExpectedVaultRevision=current.VaultRevision,OperationId=Guid.NewGuid()},token);
            Check(recovered.Success && Hash(document)==Hash(afterDeleteRecovery),"native neighbouring history remains independently SHA-256 recoverable after deliberate deletion");
            Check(await deletionReopened.PrepareForExitAsync(),"native final independent draft writer is joined after deletion reconciliation");
        }
        finally
        {
            try
            {
                await model.CancelAndJoinLocalProtectionDraftWriterAsync(); await model.StopRepositoryReadsAsync();
                if(reopened is not null){await reopened.CancelAndJoinLocalProtectionDraftWriterAsync();await reopened.StopRepositoryReadsAsync();}
                if(deletionReopened is not null){await deletionReopened.CancelAndJoinLocalProtectionDraftWriterAsync();await deletionReopened.StopRepositoryReadsAsync();}
            }
            finally { window.Close(); Directory.Delete(root,true); }
        }
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
        static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    }
    private sealed class ObservedClient(IFluxVaultServiceClient inner,IProtectionDraftStore drafts,IConfigurationSaveOperationStore saves,CancellationToken deadline):IFluxVaultServiceClient
    {
        internal int SaveCount,BackupCount;
        internal int DeleteCount;
        internal bool LoseDeletionAcknowledgement;
        internal Guid? LastDeletionReceipt;
        internal bool AssociationVerified;
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request,CancellationToken cancellationToken=default)
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(deadline,cancellationToken);
            if(request.Command==FluxVaultIpcCommand.SaveConfiguration)
            {
                SaveCount++; var draft=drafts.Read()!; var pending=saves.Read()!;
                AssociationVerified=draft.SaveOperationId==request.OperationId && pending.OperationId==request.OperationId &&
                    pending.ProtectionDraftId==draft.DraftId && pending.ProtectionDraftBaselineFingerprint==draft.BaselineFingerprint &&
                    JsonSerializer.Serialize(request.Configuration)==JsonSerializer.Serialize(draft.Configuration);
                if(!AssociationVerified)throw new InvalidOperationException("Native save was dispatched without durable exact draft correlation.");
            }
            if(request.Command==FluxVaultIpcCommand.RunBackupNow)BackupCount++;
            if(request.Command==FluxVaultIpcCommand.GetOperationStatus && saves.Read()?.Origin==ConfigurationSaveOrigin.HistoryDeletion)LastDeletionReceipt=request.OperationId;
            if(request.Command==FluxVaultIpcCommand.DeleteHistory)
            {
                var pending=saves.Read()!;
                if(pending.Origin!=ConfigurationSaveOrigin.HistoryDeletion || pending.OperationId!=request.OperationId ||
                    pending.HistoryDeletionFingerprint!=request.HistoryDeletionFingerprint || pending.RemovedSelections.Single()!=request.HistoryDeletionScope ||
                    pending.ProtectionDraftId is not null)throw new InvalidOperationException("Native deletion dispatched without durable exact review.");
                DeleteCount++;
            }
            var response=await inner.SendAsync(request,linked.Token);
            if(request.Command==FluxVaultIpcCommand.DeleteHistory && LoseDeletionAcknowledgement)
            {
                if(!response.Success)throw new InvalidOperationException("Native deletion failed before simulated acknowledgement loss: "+response.ErrorMessage);
                throw new IOException("fixture lost completed deletion acknowledgement");
            }
            return response;
        }
    }
    private sealed class HistoryConfirmation : IProtectionRemovalConfirmation
    {
        internal bool Allow;
        internal int Calls;
        public bool ConfirmPurge(IReadOnlyList<RepositoryPurgeScope> scopes)=>throw new InvalidOperationException("Stop protecting must never request purge confirmation.");
        public bool ConfirmHistoryDeletion(RepositoryHistoryDeletionPreview preview){Calls++;if(!preview.CanDelete || !preview.IsComplete)throw new InvalidOperationException();return Allow;}
    }
    private sealed class Files:IFileBrowserFileSystem
    {
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots()=>[];
        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path)=>[];
        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path)=>[];
    }
    private sealed class Controller:IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new FluxVaultWindowsServiceStatus("Fixture",FluxVaultWindowsServiceState.Running,"Fixture"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken=default)=>throw new InvalidOperationException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken=default)=>throw new InvalidOperationException();
    }
    private sealed class Destination:IRestoreDestinationPicker{public string? PickDestination(VersionRow version)=>throw new InvalidOperationException();}
    private sealed class Overwrite:IRestoreOverwriteConfirmation{public bool ConfirmOverwrite(string destinationPath)=>throw new InvalidOperationException();}
}
