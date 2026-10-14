using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;
using System.IO;

namespace FluxVault.App.Tests;

public sealed class OptionsAutomaticProtectionTests
{
    [Fact]
    public async Task Load_reads_the_separately_saved_automatic_preference_and_runtime_reason()
    {
        var client = new Client(true);
        var model = new OptionsViewModel(client, new Store());
        await model.InitialiseAsync();
        Assert.True(model.AutomaticProtectionAvailable);
        Assert.True(model.AutomaticProtectionEnabled);
        Assert.Contains("creator session", model.AutomaticProtectionStatus);
    }

    [Fact]
    public async Task Unavailable_or_ungranted_preference_is_explained_without_sending_an_enable()
    {
        var client=new Client(false) {CanChangePreference=false};
        var model=new OptionsViewModel(client,new Store());
        await model.InitialiseAsync();
        Assert.False(model.AutomaticProtectionAvailable);
        Assert.False(model.CanApplyAutomaticProtection);
        model.AutomaticProtectionEnabled=true;
        await model.ApplyAutomaticProtectionAsync();
        Assert.DoesNotContain(client.Requests,item=>item.Command==FluxVaultIpcCommand.SetAutomaticProtection);
    }

    [Fact]
    public async Task Explicit_apply_saves_only_the_automatic_preference_and_keeps_other_Options_edits()
    {
        var client = new Client(false);
        var records = new Store();
        var model = new OptionsViewModel(client, records);
        await model.InitialiseAsync();
        model.KeepAllHours = 47;
        model.AutomaticProtectionEnabled = true;
        await model.ApplyAutomaticProtectionAsync();
        var sent = Assert.Single(client.Requests, item => item.Command == FluxVaultIpcCommand.SetAutomaticProtection);
        Assert.True(sent.IsAutomaticProtectionEnabled);
        Assert.Null(sent.Configuration);
        Assert.Equal(client.Id, sent.VaultId);
        Assert.Equal(5, sent.ExpectedVaultRevision);
        Assert.NotEqual(Guid.Empty, sent.OperationId);
        Assert.Equal(47, model.KeepAllHours);
        Assert.Null(records.Pending);
    }

    [Fact]
    public async Task Unknown_apply_is_retained_across_reopen_and_checked_without_a_second_enable()
    {
        var client = new Client(false) { LoseAcknowledgement = true };
        var records = new Store();
        var model = new OptionsViewModel(client, records);
        await model.InitialiseAsync(); model.AutomaticProtectionEnabled = true;
        await model.ApplyAutomaticProtectionAsync();
        Assert.NotNull(records.Pending);
        var reopened = new OptionsViewModel(client, records);
        await reopened.InitialiseAsync();
        Assert.True(reopened.HasUnconfirmedSave);
        Assert.True(reopened.AutomaticProtectionEnabled);
        await reopened.CheckSaveOutcomeAsync();
        Assert.Null(records.Pending);
        Assert.Single(client.Requests, item => item.Command == FluxVaultIpcCommand.SetAutomaticProtection);
        Assert.Single(client.Requests, item => item.Command == FluxVaultIpcCommand.GetOperationStatus);
    }

    private sealed class Store : IConfigurationSaveOperationStore
    {
        internal PendingConfigurationSave? Pending;
        public PendingConfigurationSave? Read() => Pending;
        public void Reserve(PendingConfigurationSave save) { Assert.Null(Pending); Pending = save; }
        public void Clear(PendingConfigurationSave save) { Assert.Equal(Pending, save); Pending = null; }
    }
    private sealed class Client(bool enabled) : IFluxVaultServiceClient
    {
        internal VaultId Id { get; } = VaultId.New();
        internal bool LoseAcknowledgement;
        internal bool CanChangePreference=true;
        internal List<FluxVaultIpcRequest> Requests { get; } = [];
        private long revision = 5;
        private readonly FluxVaultConfiguration configuration = FluxVaultConfiguration.CreateDefault(Path.Combine(Path.GetTempPath(), "FluxVault.OptionsAutomatic"));
        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            FluxVaultIpcResponse response;
            if (request.Command == FluxVaultIpcCommand.GetStatus)
                response = FluxVaultIpcResponse.WithStatus(new(true, configuration, "", null, [], [],
                    AutomaticProtection: new(new(enabled), AutomaticProtectionState.Blocked, "Waiting for creator session.",CanChangePreference:CanChangePreference)));
            else if (request.Command == FluxVaultIpcCommand.SetAutomaticProtection)
            {
                enabled = request.IsAutomaticProtectionEnabled!.Value; revision++;
                if (LoseAcknowledgement) return Task.FromResult(FluxVaultIpcResponse.Failure("lost acknowledgement") with { ErrorCode = FluxVaultIpcErrorCode.OutcomeUnknown });
                response = FluxVaultIpcResponse.Ok();
            }
            else if (request.Command == FluxVaultIpcCommand.GetOperationStatus) response = FluxVaultIpcResponse.Ok();
            else throw new InvalidOperationException("Unexpected Options command " + request.Command);
            return Task.FromResult(response with { VaultId = Id, VaultRevision = revision, OperationId = request.OperationId });
        }
    }
}
