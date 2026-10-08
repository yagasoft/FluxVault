using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class CurrentEntriesPagingContractTests
{
    [Fact]
    public async Task Replacing_a_parent_recovery_pointer_preserves_loaded_descendants_and_selected_child_file()
    {
        var root=Path.Combine(Path.GetTempPath(),"FluxVault-current-nested-"+Guid.NewGuid().ToString("N"));
        var parentPath=Path.Combine(root,"Project");var childPath=Path.Combine(parentPath,"Drawings");var filePath=Path.Combine(childPath,"part.dwg");
        Directory.CreateDirectory(childPath);File.WriteAllText(filePath,"part");
        var client=new Client{SourceRoot=root,Inventory=Inventory(1)};
        var browser=new FileBrowserViewModel(new RealFiles(root));
        var model=new MainWindowViewModel(client,TimeSpan.FromHours(1),browser,new Controller(),new Destination(),new Overwrite());
        try
        {
            await model.RefreshAsync();browser.SelectFolder(browser.Roots.Single());
            var parent=browser.Roots.Single().Children.Single();parent.IsExpanded=true;browser.LoadChildren(parent);
            var child=parent.Children.Single();child.IsExpanded=true;browser.SelectFolder(child);browser.SelectedFile=browser.Files.Single();
            browser.ReplaceSelectionRule(new("pending",filePath,ProtectionSelectionMode.File,CompressionPreference.Zstd,ResourceProfile.Balanced,true));
            model.RepositoryPath=Path.Combine(root,"pending-repository");
            Directory.Delete(parentPath,true);
            client.Inventory=Inventory(2);client.Epoch=Guid.NewGuid();await model.RefreshAsync();
            var phantomChild=browser.SelectedFolder!;var phantomFile=browser.Files.Single();browser.SelectedFile=phantomFile;
            Assert.True(phantomChild.IsPhantom);Assert.True(phantomChild.IsExpanded);Assert.True(phantomChild.HasLoadedChildren);
            browser.BeginAddressPathEdit();browser.AddressPath=Path.Combine(root,"typing-address");
            client.Inventory=Inventory(3);client.Epoch=Guid.NewGuid();await model.RefreshAsync();
            Assert.Equal(3.ToString("x32"),browser.Roots.Single().Children.Single().RestorableVersionId);
            Assert.Same(phantomChild,browser.SelectedFolder);Assert.True(phantomChild.IsExpanded);Assert.True(phantomChild.HasLoadedChildren);
            Assert.Same(phantomFile,browser.SelectedFile);Assert.Same(phantomFile,browser.Files.Single());
            Assert.Equal(Path.Combine(root,"typing-address"),browser.AddressPath);
            Assert.Equal(filePath,Assert.Single(browser.GetSelectionRules()).Path);Assert.Single(browser.PendingChanges);
            Assert.Equal(Path.Combine(root,"pending-repository"),model.RepositoryPath);
        }
        finally{await model.StopRepositoryReadsAsync();Directory.Delete(root,true);}
        IReadOnlyList<RepositoryCurrentEntry> Inventory(int parentVersion)=>[
            new(1,new(parentVersion.ToString("x32"),parentPath,DateTimeOffset.UtcNow,CaptureConsistency.BestEffort,0,0,EntryKind:RepositoryEntryKind.Folder)),
            new(2,new(4.ToString("x32"),childPath,DateTimeOffset.UtcNow,CaptureConsistency.BestEffort,0,0,EntryKind:RepositoryEntryKind.Folder)),
            new(3,new(5.ToString("x32"),filePath,DateTimeOffset.UtcNow,CaptureConsistency.BestEffort,4,0))];
    }

    [Theory]
    [InlineData(RepositoryEntryKind.File)]
    [InlineData(RepositoryEntryKind.Folder)]
    public async Task Automatic_inventory_refresh_reconciles_real_missing_and_restored_items_without_losing_edits(RepositoryEntryKind kind)
    {
        var root=Path.Combine(Path.GetTempPath(),"FluxVault-current-browser-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,kind==RepositoryEntryKind.File ? "drawing.dwg" : "Project");
        if(kind==RepositoryEntryKind.File)File.WriteAllText(path,"drawing");else Directory.CreateDirectory(path);
        var client=new Client{SourceRoot=root,Inventory=[Entry(1)]};
        var browser=new FileBrowserViewModel(new RealFiles(root));
        var model=new MainWindowViewModel(client,TimeSpan.FromMilliseconds(20),browser,new Controller(),new Destination(),new Overwrite());
        try
        {
            await model.RefreshAsync();browser.SelectFolder(browser.Roots.Single());
            browser.Roots.Single().IsExpanded=true;
            browser.ReplaceSelectionRule(new("pending",path,kind==RepositoryEntryKind.File ? ProtectionSelectionMode.File : ProtectionSelectionMode.RecursiveFolder,
                CompressionPreference.Zstd,ResourceProfile.Balanced,true));
            if(kind==RepositoryEntryKind.File)browser.SelectedFile=browser.Files.Single();
            else browser.SelectFolder(browser.Roots.Single().Children.Single());
            model.RepositoryPath=Path.Combine(root,"pending-repository");
            browser.BeginAddressPathEdit();browser.AddressPath=Path.Combine(root,"typing-address");
            if(kind==RepositoryEntryKind.File)File.Delete(path);else Directory.Delete(path);
            await UpdateAsync("Missing",2);
            if(kind==RepositoryEntryKind.File)
            {
                Assert.True(Assert.Single(browser.Files).IsPhantom);
                Assert.Equal(2.ToString("x32"),browser.SelectedFile!.RestorableVersionId);
            }
            else
            {
                Assert.True(Assert.Single(browser.Roots.Single().Children).IsPhantom);
                Assert.Equal(2.ToString("x32"),browser.SelectedFolder!.RestorableVersionId);
                Assert.True(browser.SelectedFolder.HasLoadedChildren);
            }
            await UpdateAsync("New recovery pointer",3);
            Assert.Equal(3.ToString("x32"),kind==RepositoryEntryKind.File ? browser.SelectedFile!.RestorableVersionId : browser.SelectedFolder!.RestorableVersionId);
            if(kind==RepositoryEntryKind.File)File.WriteAllText(path,"restored drawing");else Directory.CreateDirectory(path);
            await UpdateAsync("Restored",4);
            if(kind==RepositoryEntryKind.File)
            {
                Assert.False(Assert.Single(browser.Files).IsPhantom);Assert.Null(browser.SelectedFile!.RestorableVersionId);
            }
            else
            {
                Assert.False(Assert.Single(browser.Roots.Single().Children).IsPhantom);Assert.Null(browser.SelectedFolder!.RestorableVersionId);
            }
            Assert.Equal(Path.Combine(root,"pending-repository"),model.RepositoryPath);
            Assert.Equal(Path.Combine(root,"typing-address"),browser.AddressPath);
            Assert.Equal(path,Assert.Single(browser.GetSelectionRules()).Path);
            Assert.Single(browser.PendingChanges);Assert.True(browser.Roots.Single().IsExpanded);
        }
        finally
        {
            await model.StopRepositoryReadsAsync();Directory.Delete(root,true);
        }
        RepositoryCurrentEntry Entry(int version)=>new(1,new(version.ToString("x32"),path,DateTimeOffset.UtcNow,CaptureConsistency.BestEffort,7,0,EntryKind:kind));
        async Task UpdateAsync(string message,int version)
        {
            var applied=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender,System.ComponentModel.PropertyChangedEventArgs args)
            {
                if(args.PropertyName==nameof(model.ServiceStatus) && model.ServiceStatus.Contains(message,StringComparison.Ordinal))applied.TrySetResult();
            }
            model.PropertyChanged+=Changed;
            try
            {
                client.Inventory=[Entry(version)];client.Epoch=Guid.NewGuid();client.Message=message;model.StartAutoRefresh();
                await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally{model.PropertyChanged-=Changed;}
        }
    }

    [Fact]
    public async Task Status_only_save_reconciliation_does_not_accept_epoch_without_a_current_sweep()
    {
        var client=new Client();var ledger=new MemoryConfigurationSaveOperationStore();
        var model=Model(client,TimeSpan.FromMilliseconds(20),ledger);
        await model.RefreshAsync();model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        var pending=new PendingConfigurationSave(client.Id.Value,Guid.NewGuid(),9,client.Configuration with{RepositoryBrowse=new(1)},false,[],[]);
        ledger.Reserve(pending);await model.RefreshAsync();
        Assert.True(model.HasUnconfirmedProtectionSave);
        client.Receipt=pending;client.Revision=10;client.Epoch=Guid.NewGuid();client.NewSnapshot=true;
        await model.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        Assert.Null(ledger.Read());Assert.Equal(4,client.Queries.Count);
        Assert.Equal(new[]{"next.dwg","old.dwg"},model.FileBrowser.Files.Select(f=>f.Name).Order());
        var applied=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged+=(_,args)=>
        {
            if(args.PropertyName==nameof(model.ServiceStatus) && model.ServiceStatus.Contains("After reconciliation",StringComparison.Ordinal))applied.TrySetResult();
        };
        client.Message="After reconciliation";model.StartAutoRefresh();
        try { await applied.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await model.StopRepositoryReadsAsync(); }
        Assert.Equal(5,client.Queries.Count);Assert.Equal(new[]{"new.dwg"},model.FileBrowser.Files.Select(f=>f.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Warm_automatic_status_reloads_current_entries_only_when_runtime_epoch_changes(bool changed)
    {
        var client=new Client(); var model=Model(client,TimeSpan.FromMilliseconds(20));
        await model.RefreshAsync(); model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        var applied=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged+=(_,args)=>
        {
            if(args.PropertyName==nameof(model.ServiceStatus) && model.ServiceStatus.Contains("Warm snapshot",StringComparison.Ordinal)) applied.TrySetResult();
        };
        if(changed){client.Epoch=Guid.NewGuid();client.NewSnapshot=true;}
        client.Message="Warm snapshot"; model.StartAutoRefresh();
        try { await applied.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await model.StopRepositoryReadsAsync(); }
        Assert.Equal(changed ? new[]{"new.dwg"} : new[]{"next.dwg","old.dwg"},model.FileBrowser.Files.Select(f=>f.Name).Order());
        Assert.Equal(changed ? 3 : 2,client.Queries.Count);
    }

    [Fact]
    public async Task Automatic_failed_sweep_does_not_accept_epoch_and_next_warm_poll_retries()
    {
        var client=new Client(); var model=Model(client,TimeSpan.FromMilliseconds(20));
        await model.RefreshAsync(); model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        model.RepositoryPath=@"C:\PendingDraft";
        var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged+=(_,args)=>
        {
            if(args.PropertyName!=nameof(model.ServiceStatus))return;
            if(model.ServiceStatus.Contains("inventory denied",StringComparison.Ordinal))failed.TrySetResult();
            if(model.ServiceStatus.Contains("Retry snapshot",StringComparison.Ordinal))applied.TrySetResult();
        };
        client.Epoch=Guid.NewGuid(); client.Failure="denied"; model.StartAutoRefresh();
        try
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[]{"next.dwg","old.dwg"},model.FileBrowser.Files.Select(f=>f.Name).Order());
            client.Failure=null;client.NewSnapshot=true;client.Message="Retry snapshot";
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await model.StopRepositoryReadsAsync(); }
        Assert.Equal(new[]{"new.dwg"},model.FileBrowser.Files.Select(f=>f.Name));
        Assert.Equal(@"C:\PendingDraft",model.RepositoryPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_full_reload_after_cache_invalidation_refreshes_current_entries_and_keeps_pending_edits(bool edit)
    {
        var client=new Client(); var model=Model(client,TimeSpan.FromMilliseconds(20));
        await model.RefreshAsync(); model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        Assert.Equal(2,model.FileBrowser.Files.Count);
        if(edit)model.RepositoryPath=@"C:\PendingDraft";
        var applied=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged+=(_,args)=>
        {
            if(args.PropertyName==nameof(model.ServiceStatus) && model.ServiceStatus.Contains("Refreshed snapshot",StringComparison.Ordinal)) applied.TrySetResult();
        };
        client.NewSnapshot=true; client.InvalidateFast=true; model.StartAutoRefresh();
        try { await applied.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await model.StopRepositoryReadsAsync(); }
        Assert.Equal(new[]{"new.dwg"},model.FileBrowser.Files.Select(f=>f.Name));
        Assert.Equal(3,client.Queries.Count);
        if(edit)Assert.Equal(@"C:\PendingDraft",model.RepositoryPath);
    }

    [Fact]
    public async Task Complete_pages_publish_once_and_preserve_deleted_files_and_pending_edits()
    {
        var client = new Client(); var model = Model(client);
        await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        Assert.Equal(new[] { "next.dwg", "old.dwg" }, model.FileBrowser.Files.Select(f => f.Name).Order());
        Assert.Equal(2, client.Queries.Count);
        Assert.All(client.Queries, r => { Assert.Equal(client.Id, r.VaultId); Assert.Equal(9, r.ExpectedVaultRevision); });
        Assert.Equal(1, client.Queries[1].CurrentEntriesQuery!.Cursor!.PathId);
        model.RepositoryPath = @"C:\PendingDraft";
        client.NewSnapshot = true;
        await model.RefreshAsync();
        Assert.Equal(@"C:\PendingDraft", model.RepositoryPath);
        Assert.Equal(new[] { "new.dwg" }, model.FileBrowser.Files.Select(f => f.Name));
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("changed")]
    [InlineData("null")]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("identity")]
    [InlineData("duplicate_path")]
    [InlineData("duplicate_id")]
    [InlineData("cursor")]
    [InlineData("echo")]
    [InlineData("cancelled")]
    [InlineData("json")]
    [InlineData("epoch_missing")]
    [InlineData("epoch_empty")]
    public async Task Incomplete_or_invalid_refresh_retains_the_previous_complete_browser_and_draft(string failure)
    {
        var client = new Client(); var model = Model(client);
        await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        Assert.Equal(2, model.FileBrowser.Files.Count);
        model.RepositoryPath = @"C:\PendingDraft";
        client.Failure = failure;
        await model.RefreshAsync();
        Assert.Equal(@"C:\PendingDraft", model.RepositoryPath);
        Assert.Equal(new[] { "next.dwg", "old.dwg" }, model.FileBrowser.Files.Select(f => f.Name).Order());
        Assert.Contains("inventory", model.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Queries.Select(r => r.Command));
    }

    [Fact]
    public async Task Authoritative_empty_page_clears_prior_browser_only_after_successful_completion()
    {
        var client = new Client(); var model = Model(client); await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single()); Assert.Equal(2, model.FileBrowser.Files.Count);
        client.Empty = true; await model.RefreshAsync(); Assert.Empty(model.FileBrowser.Files);
    }

    [Fact]
    public async Task Short_pages_with_continuations_are_not_treated_as_complete()
    {
        var client = new Client { PageSize = 3 }; var model = Model(client); await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        Assert.Equal(2, client.Queries.Count); Assert.Equal(2, model.FileBrowser.Files.Count);
    }

    [Fact]
    public async Task Edits_and_previous_entries_survive_while_a_new_sweep_is_in_flight()
    {
        var client = new Client(); var model = Model(client); await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforePage = async (query, token) => { if (query.Cursor is not null) { started.TrySetResult(); await release.Task.WaitAsync(token); } };
        var refresh = model.RefreshAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, model.FileBrowser.Files.Count);
            model.RepositoryPath = @"C:\NewerDraft";
        }
        finally { release.TrySetResult(); }
        await refresh;
        Assert.Equal(@"C:\NewerDraft", model.RepositoryPath);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("startup")]
    [InlineData("automatic")]
    public async Task Shutdown_cancels_and_joins_paging_and_refuses_new_refreshes(string entryPoint)
    {
        var client = new Client(); var model = Model(client,TimeSpan.FromMilliseconds(20));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforePage = async (query, token) =>
        {
            if (query.Cursor is null) return;
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); await release.Task; throw; }
        };
        var refresh = entryPoint=="startup" ? model.ApplyStartupRequestAsync(new(AppStartupRequestAction.ShowVersions,@"C:\Work\old.dwg")) :
            entryPoint=="manual" ? model.RefreshAsync() : Task.CompletedTask;
        if(entryPoint=="automatic")model.StartAutoRefresh();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = model.StopRepositoryReadsAsync();
        try
        {
            Assert.Same(stop, model.StopRepositoryReadsAsync());
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted); if(entryPoint!="automatic")Assert.False(refresh.IsCompleted);
            var requests = client.Queries.Count; await model.RefreshAsync(); Assert.Equal(requests, client.Queries.Count);
        }
        finally { release.TrySetResult(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(5)); await refresh;
        Assert.True(model.StopRepositoryReadsAsync().IsCompleted);
    }

    private static MainWindowViewModel Model(Client client,TimeSpan? refreshInterval=null,IConfigurationSaveOperationStore? ledger=null) => new(client, refreshInterval??TimeSpan.FromHours(1),
        new FileBrowserViewModel(new Files()), new Controller(), new Destination(), new Overwrite(),saveOperationStore:ledger);
    private sealed class Files : IFileBrowserFileSystem
    {
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots() => [new(@"C:\Work", "Work", true, null)];
        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path) => [];
        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path) => [];
    }
    private sealed class RealFiles(string root) : IFileBrowserFileSystem
    {
        private readonly WindowsFileBrowserFileSystem files=new();
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots()=>[new(root,"Working files",true,null)];
        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path)=>files.GetChildFolders(path);
        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path)=>files.GetFiles(path);
    }
    private sealed class Client : IFluxVaultServiceClient
    {
        public VaultId Id { get; } = VaultId.New();
        public FluxVaultConfiguration Configuration { get; }=FluxVaultConfiguration.CreateDefault(@"C:\Repository");
        public long Revision { get; set; }=9;
        public PendingConfigurationSave? Receipt { get; set; }
        public List<FluxVaultIpcRequest> Queries { get; } = [];
        public bool NewSnapshot { get; set; }
        public bool InvalidateFast { get; set; }
        public Guid? Epoch { get; set; }=Guid.NewGuid();
        public string? Message { get; set; }
        public bool Empty { get; set; }
        public string? Failure { get; set; }
        public int PageSize { get; set; } = 1;
        public string SourceRoot { get; set; }=@"C:\Work";
        public IReadOnlyList<RepositoryCurrentEntry>? Inventory { get; set; }
        public Func<RepositoryCurrentEntriesQuery,CancellationToken,Task>? BeforePage { get; set; }
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            FluxVaultIpcResponse response;
            if (request.Command == FluxVaultIpcCommand.GetStatus)
                response = FluxVaultIpcResponse.WithStatus(new(true, Configuration with
                    { RepositoryBrowse = new(PageSize) }, Message ?? (NewSnapshot ? "Refreshed snapshot" : "Fixture"), null, [], [],
                    HasVersionInventory: !(InvalidateFast && request.StatusDetailLevel==FluxVaultStatusDetailLevel.Fast), UsesPagedCurrentEntries: true,
                    RepositoryInventoryEpoch:Failure=="epoch_missing" ? null : Failure=="epoch_empty" ? Guid.Empty : Epoch));
            else if (request.Command == FluxVaultIpcCommand.ListCurrentEntriesPage)
            {
                Queries.Add(request); var query = request.CurrentEntriesQuery!;
                if (BeforePage is not null) await BeforePage(query,cancellationToken);
                if(Inventory is { } inventory)
                {
                    var remaining=inventory.Where(r=>r.PathId>(query.Cursor?.PathId??0)).ToArray();
                    var entries=remaining.Take(query.PageSize).ToArray();
                    return FluxVaultIpcResponse.Ok() with{VaultId=Id,VaultRevision=Revision,CurrentEntriesPage=new(query,1,entries,
                        remaining.Length>entries.Length ? new(Id,query.PageSize,1,entries[^1].PathId) : null)};
                }
                var second = query.Cursor is not null;
                if (second && Failure == "cancelled") throw new OperationCanceledException("Cancelled fixture");
                if (second && Failure == "json") System.Text.Json.JsonSerializer.Deserialize<FluxVaultIpcResponse>("{ malformed }");
                if (second && Failure is "denied" or "changed")
                    return FluxVaultIpcResponse.Failure("Current inventory " + Failure) with
                        { ErrorCode = Failure == "changed" ? FluxVaultIpcErrorCode.HistoryChanged : FluxVaultIpcErrorCode.Denied };
                var rowId = second ? 2 : 1;
                var name = NewSnapshot ? "new.dwg" : second ? "next.dwg" : "old.dwg";
                var row = new RepositoryCurrentEntry(rowId, new(rowId.ToString("x32"), Path.Combine(SourceRoot,name),
                    DateTimeOffset.UtcNow, CaptureConsistency.BestEffort, 0, 0, IsDeleted: true));
                if (second && Failure == "duplicate_path") row = row with { Version = row.Version with { SourcePath = @"c:\WORK\OLD.DWG" } };
                if (second && Failure == "duplicate_id") row = row with { PathId = 1 };
                var page = new RepositoryCurrentEntriesPage(query, second && Failure == "generation" ? 2 : 1,
                    Empty ? [] : [row], second || NewSnapshot || Empty ? null : new(Id, query.PageSize, 1, 1));
                if (second && Failure == "cursor") page = page with { NextCursor = new(Id, 1, 1, 1) };
                if (second && Failure == "echo") page = page with { Query = query with { PageSize = 2 } };
                response = FluxVaultIpcResponse.Ok() with { CurrentEntriesPage = second && Failure == "null" ? null : page };
                if (second && Failure == "revision") return response with { VaultId = Id, VaultRevision = 10 };
                if (second && Failure == "identity") return response with { VaultId = VaultId.New(), VaultRevision = 9 };
            }
            else if(request.Command==FluxVaultIpcCommand.GetOperationStatus && Receipt?.OperationId==request.OperationId)
                response=FluxVaultIpcResponse.Ok() with{OperationId=request.OperationId};
            else throw new InvalidOperationException("Unexpected command " + request.Command);
            return response with { VaultId = Id, VaultRevision = Revision };
        }
    }
    private sealed class Controller : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FluxVaultWindowsServiceStatus("Fixture", FluxVaultWindowsServiceState.Running, "Fixture"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class Destination : IRestoreDestinationPicker { public string? PickDestination(VersionRow version) => throw new InvalidOperationException(); }
    private sealed class Overwrite : IRestoreOverwriteConfirmation { public bool ConfirmOverwrite(string destinationPath) => throw new InvalidOperationException(); }
}
