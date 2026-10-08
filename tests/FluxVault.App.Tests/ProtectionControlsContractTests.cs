using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed partial class ProtectionSaveContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_pause_preserves_pending_selection_and_later_save_preserves_the_pause(bool profileStore)
    {
        using var fixture = new StoreFixture(profileStore);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root) with { IsEnabled = true });
        var client = new StoreClient(fixture.Store) { RequireBoundRequests = true };
        var model = CreateViewModel(client);
        await model.RefreshAsync();
        var selection = Selection(fixture.Root, "unsaved-selection");
        model.FileBrowser.ReplaceSelectionRule(selection);

        var pause = GetControl(model, "PauseProtectionCommand");
        Assert.True(pause.CanExecute(null));
        await pause.ExecuteAsync(null);

        Assert.False((await fixture.Reopen().LoadAsync()).IsEnabled);
        Assert.Equal(selection.Path, Assert.Single(model.FileBrowser.GetSelectionRules()).Path);
        Assert.NotEmpty(model.FileBrowser.PendingChanges);
        Assert.DoesNotContain(client.Commands, command => command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        var request = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SetProtectionPaused);
        Assert.True(JsonNode.Parse(FluxVaultIpcSerializer.SerializeRequest(request))!["isProtectionPaused"]!.GetValue<bool>());
        Assert.False(pause.CanExecute(null));
        Assert.False(model.RunBackupNowCommand.CanExecute(null));
        await model.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.False((await fixture.Reopen().LoadAsync()).IsEnabled);
        Assert.Empty(model.FileBrowser.PendingChanges);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);

        await GetControl(model, "ResumeProtectionCommand").ExecuteAsync(null);
        Assert.True((await fixture.Reopen().LoadAsync()).IsEnabled);
        Assert.True(model.RunBackupNowCommand.CanExecute(null));
        Assert.Equal(selection.Path, Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules).Path);
    }

    [Fact]
    public async Task Lost_pause_acknowledgement_retains_original_operation_and_checks_without_repeating_it()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root));
        var client = new StoreClient(fixture.Store) { LoseProtectionStateAcknowledgement = true };
        var saveStore = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-control.json"));
        var model = CreateViewModel(client, saveStore: saveStore);
        await model.RefreshAsync();
        var selection = Selection(fixture.Root, "newer-edit");
        model.FileBrowser.ReplaceSelectionRule(selection);

        await GetControl(model, "PauseProtectionCommand").ExecuteAsync(null);

        var pending = Assert.IsType<PendingConfigurationSave>(saveStore.Read());
        Assert.False((await fixture.Reopen().LoadAsync()).IsEnabled);
        Assert.True(model.HasUnconfirmedProtectionSave);
        Assert.False(GetControl(model, "ResumeProtectionCommand").CanExecute(null));
        await model.RunBackupNowCommand.ExecuteAsync(null);
        await model.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        Assert.Null(saveStore.Read());
        Assert.False(model.HasUnconfirmedProtectionSave);
        Assert.NotEmpty(model.FileBrowser.PendingChanges);
        Assert.Equal(selection.Path, Assert.Single(model.FileBrowser.GetSelectionRules()).Path);
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SetProtectionPaused);
        Assert.All(client.Requests.Where(request => request.Command == FluxVaultIpcCommand.GetOperationStatus),
            request => Assert.Equal(pending.OperationId, request.OperationId));
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    private static IAsyncRelayCommand GetControl(MainWindowViewModel model, string name) =>
        Assert.IsAssignableFrom<IAsyncRelayCommand>(typeof(MainWindowViewModel).GetProperty(name)?.GetValue(model));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_rebases_the_real_local_draft_and_restart_keeps_edits_without_saving_them(bool acknowledgementLost)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root));
        var client = new StoreClient(fixture.Store) { LoseProtectionStateAcknowledgement = acknowledgementLost };
        var saveStore = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root,"pending-control.json"));
        var draftStore = new FileProtectionDraftStore(Path.Combine(fixture.Root,"editing-draft.json"));
        var model = CreateViewModel(client,saveStore:saveStore,draftStore:draftStore);
        await model.RefreshAsync();
        var selection = Selection(fixture.Root,"retained-draft");
        model.FileBrowser.ReplaceSelectionRule(selection);

        await GetControl(model,"PauseProtectionCommand").ExecuteAsync(null);
        Assert.True(await model.PrepareForExitAsync());
        Assert.NotNull(draftStore.Read());
        Assert.False((await fixture.Store.LoadAsync()).IsEnabled);
        Assert.Empty((await fixture.Store.LoadAsync()).SelectionRules);

        var restarted = CreateViewModel(client,saveStore:saveStore,draftStore:draftStore);
        await restarted.RefreshAsync();
        Assert.False(restarted.RequiresLocalProtectionDraftReview);
        Assert.Equal(selection.Path,Assert.Single(restarted.FileBrowser.GetSelectionRules()).Path);
        Assert.NotEmpty(restarted.FileBrowser.PendingChanges);
        if (acknowledgementLost)
            await restarted.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        Assert.False(restarted.HasUnconfirmedProtectionSave);
        Assert.False(restarted.RequiresLocalProtectionDraftReview);
        Assert.NotNull(draftStore.Read());
        await restarted.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.False((await fixture.Store.LoadAsync()).IsEnabled);
        Assert.Equal(selection.Path,Assert.Single((await fixture.Store.LoadAsync()).SelectionRules).Path);
        Assert.Null(draftStore.Read());
        Assert.True(await restarted.PrepareForExitAsync());
        Assert.Single(client.Requests,request=>request.Command==FluxVaultIpcCommand.SetProtectionPaused);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow,client.Commands);
    }
}
