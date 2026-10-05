using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Ipc;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.TestHost;

internal static class WindowsProtectionDraftProbe
{
    internal static async Task RunAsync(IFluxVaultServiceClient client,string output,string document,List<string> checks,CancellationToken token)
    {
        var root=Path.Combine(output,"local-protection-draft"); Directory.CreateDirectory(root);
        var drafts=new FileProtectionDraftStore(Path.Combine(root,"draft.json"));
        var saves=new FileConfigurationSaveOperationStore(Path.Combine(root,"save.json"));
        var observed=new ObservedClient(client,drafts,saves,token);
        MainWindowViewModel Model()=>new(observed,TimeSpan.FromHours(1),new FileBrowserViewModel(new Files()),new Controller(),new Destination(),new Overwrite(),
            backupOperationStore:new FileBackupOperationStore(Path.Combine(root,"backup.json")),saveOperationStore:saves,protectionDraftStore:drafts);
        var model=Model(); MainWindowViewModel? reopened=null;
        try
        {
            await model.RefreshAsync();
            var before=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),token);
            var source=Path.GetDirectoryName(document)!;
            var rule=model.FileBrowser.GetSelectionRules().SingleOrDefault(item=>item.Path==source) ??
                new ProtectionSelectionRule("native-local-draft",source,ProtectionSelectionMode.RecursiveFolder,
                    CompressionPreference.Off,ResourceProfile.Balanced,true);
            model.FileBrowser.ReplaceSelectionRule(rule with{IncludeRegexRules=[new("local-draft-docx",@".*\.docx$",ProtectionExclusionTarget.File)]});
            Check(await model.PrepareForExitAsync(),"native unsent Protect edit force-flushes and joins before exit");
            await model.StopRepositoryReadsAsync();
            var draft=drafts.Read()!;
            Check(draft.SaveOperationId is null && saves.Read() is null && observed.SaveCount==0 && observed.BackupCount==0,
                "native local draft retention sends no save or backup through the authenticated pipe");
            var unchanged=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),token);
            Check(unchanged.VaultRevision==before.VaultRevision && JsonSerializer.Serialize(unchanged.Status!.Configuration)==JsonSerializer.Serialize(before.Status!.Configuration),
                "native unsent draft leaves real PostgreSQL configuration and revision unchanged");
            reopened=Model(); await reopened.RefreshAsync();
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
            Check(await reopened.PrepareForExitAsync(),"native final draft writer is joined with no outstanding local changes");
        }
        finally
        {
            try
            {
                await model.CancelAndJoinLocalProtectionDraftWriterAsync(); await model.StopRepositoryReadsAsync();
                if(reopened is not null){await reopened.CancelAndJoinLocalProtectionDraftWriterAsync();await reopened.StopRepositoryReadsAsync();}
            }
            finally { Directory.Delete(root,true); }
        }
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
        static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    }
    private sealed class ObservedClient(IFluxVaultServiceClient inner,IProtectionDraftStore drafts,IConfigurationSaveOperationStore saves,CancellationToken deadline):IFluxVaultServiceClient
    {
        internal int SaveCount,BackupCount;
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
            return await inner.SendAsync(request,linked.Token);
        }
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
