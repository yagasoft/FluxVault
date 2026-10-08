using System.IO;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.TestHost;

internal static class WindowsCurrentPagingProbe
{
    internal static async Task RunAsync(IFluxVaultServiceClient client,string source,string document,List<string> checks,CancellationToken token)
    {
        var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),token);
        if(!status.Success || status.VaultId is not {IsValid:true} id || status.VaultRevision is not >0 || status.Status is null)
            throw new InvalidOperationException("Native current inventory status unavailable.");
        Check(status.Status.UsesPagedCurrentEntries && status.Status.TrackedEntries is null && status.Status.HasVersionInventory &&
            status.Status.RepositoryInventoryEpoch is { } epoch && epoch!=Guid.Empty,
            "native protected status omits current inventory with explicit paging capability");
        var query=new RepositoryCurrentEntriesQuery(id,2); var rows=new List<RepositoryCurrentEntry>(); long? generation=null; var reads=0;
        do
        {
            if(++reads>100)throw new InvalidOperationException("Native current paging did not finish.");
            var response=await client.SendAsync(new(FluxVaultIpcCommand.ListCurrentEntriesPage,null,null,null,null,
                VaultId:id,ExpectedVaultRevision:status.VaultRevision,CurrentEntriesQuery:query),token);
            if(!response.Success || response.CurrentEntriesPage is not {} page)throw new InvalidOperationException("Native current page failed: "+response.ErrorMessage);
            Check(response.VaultId==id && response.VaultRevision==status.VaultRevision && response.OperationId is null &&
                page.Query==query && page.Entries.Count<=2 && (generation is null || generation==page.Generation),"native current pages retain exact binding revision and generation");
            generation ??=page.Generation; rows.AddRange(page.Entries); query=query with{Cursor=page.NextCursor};
        }while(query.Cursor is not null);
        Check(rows.Count>2 && rows.Zip(rows.Skip(1)).All(p=>p.First.PathId<p.Second.PathId),"native current keysets advance without duplicates");
        var wrong=await client.SendAsync(new(FluxVaultIpcCommand.ListCurrentEntriesPage,null,null,null,null,VaultId:id,
            CurrentEntriesQuery:new(VaultId.New())),token);
        Check(!wrong.Success && wrong.ErrorCode==FluxVaultIpcErrorCode.InvalidRequest && wrong.CurrentEntriesPage is null,
            "native current query cannot override its admitted repository binding");
        var observedClient=new ObservedClient(client);
        var model=new MainWindowViewModel(observedClient,TimeSpan.FromMilliseconds(50),new FileBrowserViewModel(new Files(source)),new Controller(),new Destination(),new Overwrite());
        var heldDocument=document+".current-paging-held-"+Guid.NewGuid().ToString("N");
        var documentHeld=false;
        try
        {
            await model.RefreshAsync();
            model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
            Check(model.FileBrowser.Files.Single(f=>string.Equals(f.Path,document,StringComparison.OrdinalIgnoreCase)) is { IsPhantom:false, RestorableVersionId:null },
                "native current inventory retains the browser's live working-file contract");
            var beforeAutomatic=observedClient.CurrentReads;
            // Invalidate through real repository work while the generated source is
            // still present. Catalogue-only saves need not invalidate content caches.
            var backupRequest=FluxVaultIpcRequest.RunBackupNow() with
                {VaultId=id,ExpectedVaultRevision=status.VaultRevision,OperationId=Guid.NewGuid()};
            var backup=await client.SendAsync(backupRequest,token);
            Check(backup.Success && backup.Backup is {Success:true} && backup.VaultId==id &&
                backup.VaultRevision==status.VaultRevision && backup.OperationId==backupRequest.OperationId,
                "native fixture backup invalidates the shared runtime cache through its durable receipt");
            var warmed=await client.SendAsync(FluxVaultIpcRequest.GetStatus() with{VaultId=id},token);
            Check(warmed.Success && warmed.Status is {HasVersionInventory:true} && warmed.Status.RepositoryInventoryEpoch!=status.Status.RepositoryInventoryEpoch,
                "native separate full status request warms the new cache epoch before dashboard polling");
            RepositoryVersionSummary? expected=null;
            var verificationQuery=new RepositoryCurrentEntriesQuery(id,2);long? verificationGeneration=null;
            for(var read=0;;read++)
            {
                if(read>=100)throw new InvalidOperationException("Native independent current verification did not finish.");
                var verification=await client.SendAsync(new(FluxVaultIpcCommand.ListCurrentEntriesPage,null,null,null,null,
                    VaultId:id,ExpectedVaultRevision:warmed.VaultRevision,CurrentEntriesQuery:verificationQuery),token);
                if(!verification.Success || verification.CurrentEntriesPage is not {} page || verification.VaultId!=id ||
                    verification.VaultRevision!=warmed.VaultRevision || page.Query!=verificationQuery ||
                    verificationGeneration is {} prior && page.Generation!=prior)throw new InvalidOperationException("Native independent current verification failed.");
                verificationGeneration ??=page.Generation;
                foreach(var row in page.Entries)
                    if(row.Version.EntryKind==RepositoryEntryKind.File && string.Equals(row.Version.SourcePath,document,StringComparison.OrdinalIgnoreCase))
                        expected=row.Version;
                if(page.NextCursor is null)break;
                verificationQuery=verificationQuery with{Cursor=page.NextCursor};
            }
            if(expected is null)throw new InvalidOperationException("Native source has no verified current file entry.");
            // Only phantom rows expose a recovery ID. Hold the generated source
            // after backup; no capture occurs while it is held.
            File.Move(document,heldDocument); documentHeld=true;
            model.RepositoryPath=Path.Combine(source,"pending-UI-edit");
            var automaticApplied=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.FileBrowser.Files.CollectionChanged+=(_,_)=>
            {
                if(model.FileBrowser.Files.Any(f=>string.Equals(f.Path,document,StringComparison.OrdinalIgnoreCase) && f.IsPhantom))automaticApplied.TrySetResult();
            };
            model.StartAutoRefresh();
            await automaticApplied.Task.WaitAsync(TimeSpan.FromSeconds(10),token);
            await model.StopRepositoryReadsAsync();
            Check(model.FileBrowser.Files.Single(f=>string.Equals(f.Path,document,StringComparison.OrdinalIgnoreCase)) is {IsPhantom:true} missing && missing.RestorableVersionId==expected.VersionId,
                "native actual dashboard view-model publishes the verified current entry");
            Check(observedClient.CurrentReads>beforeAutomatic && model.RepositoryPath==Path.Combine(source,"pending-UI-edit"),
                "native automatic warmed-cache refresh pages current entries and retains pending edits before joined shutdown");
        }
        finally
        {
            try { await model.StopRepositoryReadsAsync(); }
            finally { if(documentHeld) File.Move(heldDocument,document); }
        }
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
    }
    private sealed class ObservedClient(IFluxVaultServiceClient client):IFluxVaultServiceClient
    {
        private int currentReads;
        internal int CurrentReads=>Volatile.Read(ref currentReads);
        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request,CancellationToken cancellationToken=default)
        {
            if(request.Command==FluxVaultIpcCommand.ListCurrentEntriesPage)Interlocked.Increment(ref currentReads);
            return client.SendAsync(request,cancellationToken);
        }
    }
    private sealed class Files(string root):IFileBrowserFileSystem
    {
        private readonly WindowsFileBrowserFileSystem files=new();
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots()=>[new(root,"Working files",true,null)];
        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path)=>files.GetChildFolders(path);
        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path)=>files.GetFiles(path);
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
