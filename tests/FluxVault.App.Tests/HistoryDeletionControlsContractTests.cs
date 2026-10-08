using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Configuration;

namespace FluxVault.App.Tests;

public sealed partial class ProtectionSaveContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_stop_protecting_action_stages_selected_subtree_and_keeps_history_until_non_purging_save(bool driveRoot)
    {
        using var fixture=new StoreFixture(false);
        var retired=driveRoot ? "Z:\\" : Path.Combine(fixture.Root,"retired");
        var neighbour=Selection(driveRoot ? "Y:\\" : fixture.Root,"neighbour");
        var configuration=FluxVaultConfiguration.CreateDefault(fixture.Root) with {SelectionRules=
            [Selection(fixture.Root,"retired") with {Path=retired},Selection(retired,"child"),neighbour]};
        await fixture.Store.SaveAsync(configuration);
        var client=new StoreClient(fixture.Store);
        var confirmation=new CancelRemoval();
        var model=CreateViewModel(client,confirmation);
        await model.RefreshAsync();
        model.FileBrowser.SelectedFolder=new(retired,"retired",true,null);
        var stop=Assert.IsAssignableFrom<System.Windows.Input.ICommand>(typeof(MainWindowViewModel).GetProperty("StopProtectingSelectedKeepHistoryCommand")?.GetValue(model));
        stop.Execute(null);
        Assert.Equal(neighbour.Path,Assert.Single(model.FileBrowser.GetSelectionRules()).Path);
        Assert.Equal(3,(await fixture.Store.LoadAsync()).SelectionRules.Count);
        Assert.Equal(0,confirmation.Calls);
        Assert.DoesNotContain(FluxVaultIpcCommand.SaveConfiguration,client.Commands);
        await model.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.False(Assert.Single(client.Requests,request=>request.Command==FluxVaultIpcCommand.SaveConfiguration).PurgeRemovedSelections);
        Assert.DoesNotContain(FluxVaultIpcCommand.DeleteHistory,client.Commands);
        Assert.Equal(0,confirmation.Calls);
    }

    [Fact]
    public async Task Stop_explicit_file_under_protected_ancestor_excludes_inherited_capture_after_save()
    {
        using var fixture=new StoreFixture(false);
        var parent=Selection(fixture.Root,"parent");
        var file=Selection(parent.Path,"document.txt") with {Mode=ProtectionSelectionMode.File};
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root) with {SelectionRules=[parent,file]});
        var client=new StoreClient(fixture.Store);
        var model=CreateViewModel(client,new CancelRemoval());
        await model.RefreshAsync();
        model.FileBrowser.SelectedFile=new(file.Path,"document.txt",10,true);
        Assert.IsAssignableFrom<System.Windows.Input.ICommand>(typeof(MainWindowViewModel).GetProperty("StopProtectingSelectedKeepHistoryCommand")?.GetValue(model)).Execute(null);
        var retained=Assert.Single(model.FileBrowser.GetSelectionRules());
        Assert.Equal(parent.Path,retained.Path);
        Assert.NotEmpty(retained.ExcludeRegexRules!);
        await model.SaveConfigurationCommand.ExecuteAsync(null);
        var saved=await fixture.Store.LoadAsync();
        Assert.NotEmpty(Assert.Single(saved.SelectionRules).ExcludeRegexRules!);
        Assert.False(ProtectionSelectionRegexMatcher.IsFileIncluded(file.Path,saved.SelectionRules));
        Assert.True(ProtectionSelectionRegexMatcher.IsFileIncluded(Path.Combine(parent.Path,"neighbour.txt"),saved.SelectionRules));
        Assert.DoesNotContain(FluxVaultIpcCommand.DeleteHistory,client.Commands);
    }

    [Fact]
    public async Task Cancelled_history_confirmation_keeps_history_configuration_and_independent_edits()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root));
        var before = File.ReadAllBytes(fixture.ConfigPath);
        var confirmation = new CancelRemoval();
        var client = new StoreClient(fixture.Store) { HistoryDeletionPreview = DeletionPreview(fixture.Root) };
        var pending = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root,"pending.json"));
        var model = CreateViewModel(client,confirmation,saveStore:pending);
        await model.RefreshAsync();
        model.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root,"unsaved"));
        SelectDeletionVersion(model,client.HistoryDeletionPreview.Scope.SourcePath);
        await GetControl(model,"PreviewHistoryDeletionCommand").ExecuteAsync(null);
        await GetControl(model,"DeleteHistoryCommand").ExecuteAsync(null);
        Assert.Equal(1,confirmation.Calls);
        Assert.Null(pending.Read());
        Assert.Equal(before,File.ReadAllBytes(fixture.ConfigPath));
        Assert.NotEmpty(model.FileBrowser.PendingChanges);
        Assert.DoesNotContain(client.Commands,command=>command is FluxVaultIpcCommand.DeleteHistory or FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
    }

    [Fact]
    public async Task Confirmed_history_deletion_reserves_exact_review_before_dispatch_without_saving_edits()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root));
        var before = File.ReadAllBytes(fixture.ConfigPath);
        var pending = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root,"pending.json"));
        var client = new StoreClient(fixture.Store) { HistoryDeletionPreview = DeletionPreview(fixture.Root) };
        client.DeletionDispatchCheck = request =>
        {
            var retained = Assert.IsType<PendingConfigurationSave>(pending.Read());
            Assert.Equal(ConfigurationSaveOrigin.HistoryDeletion,retained.Origin);
            Assert.Equal(request.OperationId,retained.OperationId);
            Assert.Equal(client.HistoryDeletionPreview.Scope,request.HistoryDeletionScope);
            Assert.Equal(client.HistoryDeletionPreview.Fingerprint,request.HistoryDeletionFingerprint);
            Assert.Null(retained.ProtectionDraftId);
        };
        var model = CreateViewModel(client,new AcceptRemoval(),saveStore:pending);
        await model.RefreshAsync();
        model.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root,"unsaved"));
        SelectDeletionVersion(model,client.HistoryDeletionPreview.Scope.SourcePath);
        await GetControl(model,"PreviewHistoryDeletionCommand").ExecuteAsync(null);
        await GetControl(model,"DeleteHistoryCommand").ExecuteAsync(null);
        Assert.Single(client.Requests,request=>request.Command==FluxVaultIpcCommand.DeleteHistory);
        Assert.Null(pending.Read());
        Assert.False(model.HasUnconfirmedProtectionSave);
        Assert.NotEmpty(model.FileBrowser.PendingChanges);
        Assert.Equal(before,File.ReadAllBytes(fixture.ConfigPath));
        Assert.DoesNotContain(client.Commands,command=>command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_history_deletion_survives_restart_and_checks_original_receipt_without_replay(bool malformedAcknowledgement)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root));
        var client = new StoreClient(fixture.Store) { HistoryDeletionPreview=DeletionPreview(fixture.Root),
            LoseHistoryDeletionAcknowledgement=!malformedAcknowledgement,UseFramedTransport=malformedAcknowledgement,
            ResponseFault="truncated",ResponseFaultCommand=malformedAcknowledgement?FluxVaultIpcCommand.DeleteHistory:null };
        var pending = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root,"pending.json"));
        var draft = new FileProtectionDraftStore(Path.Combine(fixture.Root,"draft.json"));
        var model = CreateViewModel(client,new AcceptRemoval(),saveStore:pending,draftStore:draft);
        await model.RefreshAsync();
        var selection = Selection(fixture.Root,"independent-edit");
        model.FileBrowser.ReplaceSelectionRule(selection);
        SelectDeletionVersion(model,client.HistoryDeletionPreview.Scope.SourcePath);
        await GetControl(model,"PreviewHistoryDeletionCommand").ExecuteAsync(null);
        await GetControl(model,"DeleteHistoryCommand").ExecuteAsync(null);
        var original = Assert.IsType<PendingConfigurationSave>(pending.Read());
        Assert.True(model.HasUnconfirmedProtectionSave);
        Assert.False(model.CanCheckProtectionSaveOutcome);
        Assert.False(model.CanSaveProtection);
        await model.RunMirrorRepairCommand.ExecuteAsync(null);
        await model.RunMirrorRebalanceCommand.ExecuteAsync(null);
        Assert.DoesNotContain(client.Commands,command=>command is FluxVaultIpcCommand.RunMirrorRepair or FluxVaultIpcCommand.RunMirrorRebalance or FluxVaultIpcCommand.RunRetentionNow);
        Assert.True(await model.PrepareForExitAsync());
        Assert.Null(draft.Read()!.SaveOperationId);
        var restarted = CreateViewModel(client,new AcceptRemoval(),saveStore:pending,draftStore:draft);
        await restarted.RefreshAsync();
        Assert.False(restarted.RequiresLocalProtectionDraftReview);
        Assert.Equal(selection.Path,Assert.Single(restarted.FileBrowser.GetSelectionRules()).Path);
        await GetControl(restarted,"CheckHistoryDeletionOutcomeCommand").ExecuteAsync(null);
        Assert.False(restarted.HasUnconfirmedProtectionSave);
        Assert.Null(pending.Read());
        Assert.NotEmpty(restarted.FileBrowser.PendingChanges);
        Assert.True(await restarted.PrepareForExitAsync());
        Assert.Single(client.Requests,request=>request.Command==FluxVaultIpcCommand.DeleteHistory);
        Assert.All(client.Requests.Where(request=>request.Command==FluxVaultIpcCommand.GetOperationStatus),request=>Assert.Equal(original.OperationId,request.OperationId));
        Assert.DoesNotContain(client.Commands,command=>command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
    }

    [Fact]
    public async Task Changed_history_selection_invalidates_the_preview_and_never_retargets_deletion()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root));
        var client = new StoreClient(fixture.Store) { HistoryDeletionPreview=DeletionPreview(fixture.Root) };
        var model = CreateViewModel(client,new AcceptRemoval());
        await model.RefreshAsync();
        SelectDeletionVersion(model,client.HistoryDeletionPreview.Scope.SourcePath);
        await GetControl(model,"PreviewHistoryDeletionCommand").ExecuteAsync(null);
        Assert.True(GetControl(model,"DeleteHistoryCommand").CanExecute(null));
        SelectDeletionVersion(model,Path.Combine(fixture.Root,"another.txt"));
        Assert.False(GetControl(model,"DeleteHistoryCommand").CanExecute(null));
        await GetControl(model,"DeleteHistoryCommand").ExecuteAsync(null);
        Assert.DoesNotContain(FluxVaultIpcCommand.DeleteHistory,client.Commands);
    }

    private static void SelectDeletionVersion(MainWindowViewModel model,string source) =>
        model.SelectedVersion=new("reviewed-version",source,"now",CaptureConsistency.BestEffort,1);

    [Fact]
    public async Task Pending_deletion_allows_independent_recovery_preview_without_clearing_or_replaying_deletion()
    {
        using var fixture=new StoreFixture(false);
        var configuration=FluxVaultConfiguration.CreateDefault(fixture.Root);
        await fixture.Store.SaveAsync(configuration);
        var client=new StoreClient(fixture.Store);
        var saves=new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root,"pending.json"));
        var original=new PendingConfigurationSave(client.Identity.Value,Guid.NewGuid(),1,configuration,false,
            [new(Path.Combine(fixture.Root,"retired"),RepositoryPurgeScopeKind.RecursiveFolder)],[],
            ConfigurationSaveOrigin.HistoryDeletion,HistoryDeletionFingerprint:new string('A',64));
        saves.Reserve(original);
        var launcher=new PreviewLauncher();
        var model=new MainWindowViewModel(client,TimeSpan.FromHours(1),new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new FixtureServiceController(),new UnusedDestinationPicker(),new UnusedOverwriteConfirmation(),versionPreviewLauncher:launcher,
            previewCache:new FluxVault.Windows.Security.WindowsUserPreviewCache(Path.Combine(fixture.Root,"previews")),saveOperationStore:saves);
        await model.RefreshAsync();
        await model.OpenVersionPreviewAsync(new("retained-neighbour",Path.Combine(fixture.Root,"neighbour.txt"),"now",CaptureConsistency.BestEffort,1));
        var output=Assert.Single(launcher.Opened);
        File.SetAttributes(output,FileAttributes.Normal);
        Assert.True(model.HasUnconfirmedProtectionSave);
        Assert.Equal(original.OperationId,saves.Read()!.OperationId);
        Assert.Contains(FluxVaultIpcCommand.RestoreVersionPreview,client.Commands);
        Assert.DoesNotContain(FluxVaultIpcCommand.DeleteHistory,client.Commands);
    }

    private static RepositoryHistoryDeletionPreview DeletionPreview(string root)
    {
        var source=Path.Combine(root,"unprotected.txt");
        return new(new(source,RepositoryPurgeScopeKind.File),new string('A',64),true,true,1,10,
            [new("reviewed-version",source,DateTimeOffset.UtcNow,CaptureConsistency.BestEffort,10,1)],[],[]);
    }
}
