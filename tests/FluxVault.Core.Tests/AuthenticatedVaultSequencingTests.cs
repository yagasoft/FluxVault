using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;

namespace FluxVault.Core.Tests;

public sealed class AuthenticatedVaultSequencingTests
{
    [Fact]
    public async Task Save_cannot_replace_configuration_while_an_admitted_backup_is_running()
    {
        using var fixture = new Fixture();
        var backup = fixture.Handler.HandleAsync(fixture.Caller, fixture.Request(FluxVaultIpcCommand.RunBackupNow));
        await fixture.Executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var save = fixture.Handler.HandleAsync(fixture.Caller, fixture.Request(FluxVaultIpcCommand.SaveConfiguration));
        try
        {
            Assert.Equal(0, fixture.Catalogue.Saves);
            Assert.False(save.IsCompleted);
        }
        finally { fixture.Executor.Release.TrySetResult(); await Task.WhenAll(backup, save); }
        Assert.Equal(1, fixture.Catalogue.Saves);
    }

    [Fact]
    public async Task Mutation_order_includes_the_durable_completion_acknowledgement()
    {
        using var fixture = new Fixture();
        fixture.Executor.Release.TrySetResult();
        fixture.Catalogue.HoldCompletion = true;
        var backup = fixture.Handler.HandleAsync(fixture.Caller, fixture.Request(FluxVaultIpcCommand.RunBackupNow));
        await fixture.Catalogue.CompletionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var save = fixture.Handler.HandleAsync(fixture.Caller, fixture.Request(FluxVaultIpcCommand.SaveConfiguration));
        try { Assert.Equal(0, fixture.Catalogue.Saves); Assert.False(save.IsCompleted); }
        finally { fixture.Catalogue.CompletionRelease.TrySetResult(); await Task.WhenAll(backup, save); }
    }

    [Fact]
    public async Task Cancelled_queued_mutation_touches_neither_catalogue_nor_executor_and_releases_its_wait()
    {
        using var fixture = new Fixture();
        var backup = fixture.Handler.HandleAsync(fixture.Caller, fixture.Request(FluxVaultIpcCommand.RunBackupNow));
        await fixture.Executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var save = fixture.Handler.HandleAsync(fixture.Caller, fixture.Request(FluxVaultIpcCommand.SaveConfiguration), cancellation.Token);
        cancellation.Cancel();
        try
        {
            var response = await save.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(response.Success);
            Assert.Equal(0, fixture.Catalogue.Saves);
            Assert.Equal(1, fixture.Executor.Calls);
        }
        finally { fixture.Executor.Release.TrySetResult(); await backup; }
        var next = await fixture.Handler.HandleAsync(fixture.Caller, fixture.Request(FluxVaultIpcCommand.SaveConfiguration));
        Assert.True(next.Success);
        Assert.Equal(1, fixture.Catalogue.Saves);
    }

    [Theory]
    [InlineData(FluxVaultIpcCommand.GetStatus)]
    [InlineData(FluxVaultIpcCommand.GetPerformance)]
    public async Task Read_requests_remain_available_during_an_admitted_backup(FluxVaultIpcCommand command)
    {
        using var fixture = new Fixture();
        var backup = fixture.Handler.HandleAsync(fixture.Caller, fixture.Request(FluxVaultIpcCommand.RunBackupNow));
        await fixture.Executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var response = await fixture.Handler.HandleAsync(fixture.Caller, fixture.Request(command)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(response.Success);
            Assert.False(backup.IsCompleted);
        }
        finally { fixture.Executor.Release.TrySetResult(); await backup; }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Caller Caller = new();
        internal readonly Catalogue Catalogue = new();
        internal readonly Executor Executor = new();
        internal AuthenticatedFluxVaultRequestHandler Handler { get; }
        internal Fixture() => Handler = new(Catalogue, Executor);
        internal FluxVaultIpcRequest Request(FluxVaultIpcCommand command) => new(command,
            command == FluxVaultIpcCommand.SaveConfiguration ? Catalogue.Entry.Configuration : null, null, null, null,
            VaultId: Catalogue.Entry.Binding.Id, ExpectedVaultRevision: 1, OperationId: Guid.NewGuid());
        public void Dispose() => Caller.Dispose();
    }
    private sealed class Caller : FluxVaultCallerContext
    {
        public override string UserSid => "S-1-5-21-1-2-3-1001";
        public override IReadOnlySet<string> EnabledGroupSids => new HashSet<string>();
        public override bool IsElevated => false;
        public override bool ImpersonationPermitted => true;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => throw new NotSupportedException();
        public override void Dispose() { }
    }
    private sealed class Catalogue : IVaultCatalogue
    {
        internal VaultCatalogueEntry Entry { get; }
        internal int Saves;
        internal bool HoldCompletion;
        internal TaskCompletionSource CompletionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CompletionRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Catalogue()
        {
            var root = Path.Combine(Path.GetTempPath(), "FluxVault.Sequencing", Guid.NewGuid().ToString("N"));
            var state = Path.Combine(root, "state"); var configuration = FluxVaultConfiguration.CreateDefault(state);
            var binding = new VaultBinding(VaultId.New(), configuration.RepositoryPath, state, configuration.MetadataStore);
            Entry = new(binding, 1, "Sequence", new("S-1-5-21-1-2-3-1001", []), configuration);
        }
        public Task<VaultAdmission> AdmitAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VaultAdmission(Entry, PostgreSqlVaultCatalogue.IsMutation(request.Command) ? Receipt(request, VaultOperationState.Admitted) : null, false));
        public Task<VaultAdmission> SaveConfigurationAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Saves); return Task.FromResult(new VaultAdmission(Entry, Receipt(request, VaultOperationState.Completed), false)); }
        private VaultOperationReceipt Receipt(FluxVaultIpcRequest request, VaultOperationState state) => new(request.OperationId!.Value,
            Entry.Binding.Id, Entry.Access.OwnerSid, request.Command, "fingerprint", state, Entry.Revision,
            state == VaultOperationState.Completed ? FluxVaultIpcResponse.Ok() : null, VaultPermission.ManageProtection);
        public async Task CompleteAsync(VaultOperationReceipt receipt, FluxVaultIpcResponse response, CancellationToken cancellationToken = default)
        { CompletionEntered.TrySetResult(); if (HoldCompletion) await CompletionRelease.Task.WaitAsync(cancellationToken); }
        public Task<VaultAdmission> SetAccessAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VaultOperationReceipt?> GetReceiptAsync(FluxVaultCallerContext caller, VaultId vaultId, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Executor : IAuthorisedVaultCommandExecutor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public async Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller, VaultAdmission admission,
            FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (request.Command == FluxVaultIpcCommand.RunBackupNow) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return FluxVaultIpcResponse.Ok();
        }
    }
}
