using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.ViewModels;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class MirrorDrainContractTests
{
    [Fact]
    public async Task Completed_drain_rebases_only_the_disabled_mirror_and_revision_preserving_pending_protection_edits()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.View.RefreshAsync();
        fixture.View.SelectedMirrorNode = fixture.View.MirrorNodes.Single(node => node.Id == "first");
        var selection = fixture.Edit();
        await fixture.View.RunSelectedMirrorDrainCommand.ExecuteAsync(null);
        Assert.False(fixture.View.MirrorNodes.Single(node => node.Id == "first").IsEnabled);
        Assert.NotEmpty(fixture.View.FileBrowser.PendingChanges);
        Assert.Contains("completed", fixture.View.RepositoryHealthStatus);
        await fixture.View.SaveConfigurationCommand.ExecuteAsync(null);
        var save = Assert.Single(fixture.Client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.Equal(2, save.ExpectedVaultRevision);
        Assert.False(save.Configuration!.MirrorSet.Nodes.Single(node => node.Id == "first").IsEnabled);
        Assert.Equal(selection.Path, Assert.Single(save.Configuration.SelectionRules!).Path);
        var restored = await fixture.Store.LoadAsync();
        Assert.Equal(JsonSerializer.Serialize(fixture.Original.MetadataStore), JsonSerializer.Serialize(restored.MetadataStore));
        Assert.Equal(fixture.Original.RepositoryMaintenancePolicy, restored.RepositoryMaintenancePolicy);
        Assert.Equal(JsonSerializer.Serialize(fixture.Original.Sync), JsonSerializer.Serialize(restored.Sync));
        Assert.Equal(fixture.Original.DiagnosticsPolicy, restored.DiagnosticsPolicy);
        Assert.Equal(fixture.Original.IsEnabled, restored.IsEnabled);
    }

    [Theory]
    [InlineData(DrainOutcome.Incomplete)]
    [InlineData(DrainOutcome.Unknown)]
    [InlineData(DrainOutcome.WrongSelector)]
    [InlineData(DrainOutcome.WrongRevision)]
    [InlineData(DrainOutcome.NullActions)]
    [InlineData(DrainOutcome.NullNodes)]
    public async Task Unconfirmed_or_incomplete_drain_keeps_enabled_mirror_and_pending_edits(DrainOutcome outcome)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Client.Outcome = outcome;
        await fixture.View.RefreshAsync(); fixture.View.SelectedMirrorNode = fixture.View.MirrorNodes[0]; fixture.Edit();
        await fixture.View.RunSelectedMirrorDrainCommand.ExecuteAsync(null);
        Assert.True(fixture.View.MirrorNodes[0].IsEnabled);
        Assert.NotEmpty(fixture.View.FileBrowser.PendingChanges);
        Assert.DoesNotContain(fixture.Client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
        Assert.Equal(JsonSerializer.Serialize(fixture.Original), JsonSerializer.Serialize(await fixture.Store.LoadAsync()));
        Assert.Contains(outcome == DrainOutcome.Incomplete ? "incomplete" : "failed", fixture.View.RepositoryHealthStatus);
    }

    [Fact]
    public async Task Delayed_drain_acknowledgement_cannot_overwrite_a_newer_accepted_configuration_or_draft()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Client.HoldDrain = true;
        await fixture.View.RefreshAsync(); fixture.View.SelectedMirrorNode = fixture.View.MirrorNodes[0];
        var drain = fixture.View.RunSelectedMirrorDrainCommand.ExecuteAsync(null);
        await fixture.Client.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await fixture.Client.PublishNewerConfigurationAsync();
            await fixture.View.RefreshAsync(); fixture.Edit();
        }
        finally { fixture.Client.DrainRelease.TrySetResult(); await drain; }
        Assert.NotEmpty(fixture.View.FileBrowser.PendingChanges);
        Assert.Equal("Newer second label", fixture.View.MirrorNodes.Single(node => node.Id == "second").Label);
        await fixture.View.SaveConfigurationCommand.ExecuteAsync(null);
        var save = Assert.Single(fixture.Client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.Equal(3, save.ExpectedVaultRevision);
        Assert.Equal("Newer second label", save.Configuration!.MirrorSet.Nodes.Single(node => node.Id == "second").Label);
        Assert.Equal(29, save.Configuration.RetentionPolicy.MinimumVersionsPerFile);
    }

    public enum DrainOutcome { Completed, Incomplete, Unknown, WrongSelector, WrongRevision, NullActions, NullNodes }
    private sealed class Fixture(string root, FileFluxVaultConfigurationStore store, FluxVaultConfiguration original, Client client) : IDisposable
    {
        internal FileFluxVaultConfigurationStore Store => store;
        internal FluxVaultConfiguration Original => original;
        internal Client Client => client;
        internal MainWindowViewModel View { get; } = new(client, TimeSpan.FromHours(1));
        internal ProtectionSelectionRule Edit()
        {
            var selection = new ProtectionSelectionRule("work", Path.Combine(root, "pending-work"), ProtectionSelectionMode.RecursiveFolder,
                CompressionPreference.Off, ResourceProfile.Balanced, true);
            View.FileBrowser.ReplaceSelectionRule(selection); return selection;
        }
        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "FluxVault.DrainTests", Guid.NewGuid().ToString("N"));
            var store = new FileFluxVaultConfigurationStore(Path.Combine(root, "config.json"), root);
            await store.SaveAsync(FluxVaultConfiguration.CreateDefault(root) with
            { IsEnabled = false, MirrorSet = new([new("first", "First", Path.Combine(root, "first")), new("second", "Second", Path.Combine(root, "second"))]) });
            return new(root, store, await store.LoadAsync(), new(store));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Client(FileFluxVaultConfigurationStore store) : IFluxVaultServiceClient
    {
        internal readonly List<FluxVaultIpcRequest> Requests = [];
        internal readonly VaultId Id = VaultId.New();
        internal DrainOutcome Outcome;
        internal bool HoldDrain;
        internal TaskCompletionSource DrainEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DrainRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long revision = 1;
        internal async Task PublishNewerConfigurationAsync()
        {
            var configuration = await store.LoadAsync();
            await store.SaveAsync(configuration with { MirrorSet = new(configuration.MirrorSet.Nodes.Select(node => node.Id == "first" ?
                node with { IsEnabled = false } : node with { Label = "Newer second label" }).ToArray(), configuration.MirrorSet.PlacementPolicy),
                RetentionPolicy = configuration.RetentionPolicy with { MinimumVersionsPerFile = 29 } });
            revision = 3;
        }
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Command == FluxVaultIpcCommand.GetStatus)
                return FluxVaultIpcResponse.WithStatus(new(true, await store.LoadAsync(cancellationToken), "Fixture", null, [], [])) with
                { VaultId = Id, VaultRevision = revision };
            if (request.Command == FluxVaultIpcCommand.RunMirrorDrain)
            {
                DrainEntered.TrySetResult();
                var configuration = await store.LoadAsync(cancellationToken);
                var admittedRevision = revision;
                if (HoldDrain) await DrainRelease.Task.WaitAsync(cancellationToken);
                if (Outcome == DrainOutcome.Unknown) return FluxVaultIpcResponse.Failure("Outcome could not be confirmed; check its status before retrying.") with
                    { ErrorCode = FluxVaultIpcErrorCode.OutcomeUnknown, VaultId = Id, OperationId = request.OperationId };
                var completed = Outcome == DrainOutcome.Completed;
                if (completed && revision == admittedRevision)
                {
                    await store.SaveAsync(configuration with { MirrorSet = new(configuration.MirrorSet.Nodes.Select(node => node.Id == request.MirrorNodeId ?
                        node with { IsEnabled = false } : node).ToArray(), configuration.MirrorSet.PlacementPolicy) }, cancellationToken);
                    revision++;
                }
                return FluxVaultIpcResponse.WithMirrorRebalance(new(DateTimeOffset.UtcNow,
                    Outcome == DrainOutcome.Incomplete ? RepositoryHealthState.Warning : RepositoryHealthState.Healthy,
                    1, 0, 0, 0, Outcome == DrainOutcome.NullNodes ? null! : [], Outcome == DrainOutcome.NullActions ? null! : [],
                    MirrorRebalanceOperation.Drain, false, Outcome == DrainOutcome.WrongSelector ? "second" : request.MirrorNodeId)) with
                    { VaultId = Id, VaultRevision = admittedRevision + (Outcome == DrainOutcome.Incomplete || Outcome == DrainOutcome.WrongRevision ? 0 : 1), OperationId = request.OperationId };
            }
            if (request.Command == FluxVaultIpcCommand.SaveConfiguration)
            {
                if (request.ExpectedVaultRevision != revision) return FluxVaultIpcResponse.Failure("Stale revision") with { ErrorCode = FluxVaultIpcErrorCode.StaleRevision };
                await store.SaveAsync(request.Configuration!, cancellationToken); revision++;
            }
            return FluxVaultIpcResponse.Ok() with { VaultId = Id, VaultRevision = revision, OperationId = request.OperationId };
        }
    }
}
