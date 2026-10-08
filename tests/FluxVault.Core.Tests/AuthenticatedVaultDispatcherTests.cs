using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;

namespace FluxVault.Core.Tests;

public sealed class AuthenticatedVaultDispatcherTests
{
    [Theory]
    [InlineData(FluxVaultIpcCommand.SaveConfiguration)]
    [InlineData(FluxVaultIpcCommand.RunBackupNow)]
    [InlineData(FluxVaultIpcCommand.RestoreVersionPreview)]
    [InlineData(FluxVaultIpcCommand.ExportDiagnostics)]
    public async Task Unsupported_commands_are_definitely_refused_before_admission(FluxVaultIpcCommand command)
    {
        var fixture = new Fixture();
        fixture.Executor.Supported = false;
        var request = fixture.Request with { Command = command, OperationId = Guid.NewGuid(),
            Configuration = command == FluxVaultIpcCommand.SaveConfiguration ? fixture.Catalogue.Entry.Configuration : null };
        var response = await fixture.Handler.HandleAsync(fixture.Caller, request);
        Assert.False(response.Success);
        Assert.Equal(FluxVaultIpcErrorCode.InvalidRequest, response.ErrorCode);
        Assert.Equal(request.VaultId, response.VaultId);
        Assert.Equal(request.OperationId, response.OperationId);
        Assert.Equal(0, fixture.Catalogue.Calls);
        Assert.Equal(0, fixture.Executor.Calls);
        Assert.False(fixture.Executor.EffectObserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_receipt_lookup_remains_available_when_the_original_command_is_unsupported(bool completed)
    {
        var fixture = new Fixture();
        fixture.Executor.Supported = false;
        var operation = Guid.NewGuid();
        var originalResponse = FluxVaultIpcResponse.Ok() with { VaultId = fixture.Request.VaultId, OperationId = operation, VaultRevision = 7 };
        fixture.Catalogue.Replay = new(operation, fixture.Catalogue.Entry.Binding.Id, fixture.Caller.UserSid,
            FluxVaultIpcCommand.SaveConfiguration, "original-payload", completed ? VaultOperationState.Completed : VaultOperationState.Admitted,
            7, completed ? originalResponse : null);
        var response = await fixture.Handler.HandleAsync(fixture.Caller, fixture.Request with
            { Command = FluxVaultIpcCommand.GetOperationStatus, OperationId = operation });
        Assert.Equal(operation, response.OperationId);
        Assert.Equal(fixture.Request.VaultId, response.VaultId);
        Assert.Equal(7, response.VaultRevision);
        if (completed) Assert.Equal(originalResponse, response);
        else Assert.Equal(FluxVaultIpcErrorCode.OutcomeUnknown, response.ErrorCode);
        Assert.Equal(0, fixture.Executor.Calls);
        Assert.False(fixture.Executor.EffectObserved);
    }

    [Theory]
    [InlineData((FluxVaultIpcCommand)26)]
    [InlineData((FluxVaultIpcCommand)27)]
    [InlineData((FluxVaultIpcCommand)28)]
    [InlineData((FluxVaultIpcCommand)29)]
    [InlineData((FluxVaultIpcCommand)30)]
    [InlineData((FluxVaultIpcCommand)32)]
    public async Task Retired_wire_values_touch_neither_installation_record_nor_executor(FluxVaultIpcCommand command)
    {
        var fixture = new Fixture();
        var response = await fixture.Handler.HandleAsync(fixture.Caller, fixture.Request with { Command = command });
        Assert.False(response.Success);
        Assert.Equal(FluxVaultIpcErrorCode.Denied, response.ErrorCode);
        Assert.Equal(0, fixture.Catalogue.Calls);
        Assert.Equal(0, fixture.Executor.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Omitted_or_unknown_target_is_denied_without_repository_materialisation(bool unknown)
    {
        var fixture = new Fixture();
        var response = await fixture.Handler.HandleAsync(fixture.Caller, fixture.Request with { VaultId = unknown ? VaultId.New() : null });
        Assert.False(response.Success);
        Assert.Equal(FluxVaultIpcErrorCode.Denied, response.ErrorCode);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task Initial_status_resolves_only_the_authorised_installed_binding_and_returns_its_identity()
    {
        var fixture = new Fixture();
        var response = await fixture.Handler.HandleAsync(fixture.Caller, FluxVaultIpcRequest.GetStatus());
        Assert.True(response.Success);
        Assert.Equal(fixture.Catalogue.Entry.Binding.Id, response.VaultId);
        Assert.Equal(fixture.Catalogue.Entry.Revision, response.VaultRevision);
        Assert.Equal(fixture.Catalogue.Entry.Binding.Id, fixture.Executor.Request!.VaultId);
        Assert.Equal(1, fixture.Catalogue.Calls);
    }

    [Fact]
    public async Task Unknown_command_and_unusable_caller_touch_neither_catalogue_nor_repository()
    {
        var fixture = new Fixture();
        var unknown = await fixture.Handler.HandleAsync(fixture.Caller, fixture.Request with { Command = (FluxVaultIpcCommand)int.MaxValue });
        Assert.Equal(FluxVaultIpcErrorCode.Denied, unknown.ErrorCode);
        var noToken = await fixture.Handler.HandleAsync(new Caller(impersonation: false), fixture.Request);
        Assert.Equal(FluxVaultIpcErrorCode.Denied, noToken.ErrorCode);
        Assert.Equal(0, fixture.Catalogue.Calls);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Fact]
    public async Task Allowed_command_passes_exact_admission_target_and_owned_caller_to_executor()
    {
        var fixture = new Fixture();
        var response = await fixture.Handler.HandleAsync(fixture.Caller, fixture.Request);
        Assert.True(response.Success);
        Assert.Equal(fixture.Catalogue.Entry.Binding.Id, response.VaultId);
        Assert.Equal(7, response.VaultRevision);
        Assert.Same(fixture.Caller, fixture.Executor.Caller);
        Assert.Equal(fixture.Request, fixture.Executor.Request);
        Assert.Same(fixture.Catalogue.Entry, fixture.Executor.Admission?.Vault);
        Assert.Equal(1, fixture.Executor.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_mutation_returns_receipt_or_unknown_and_never_reexecutes(bool completed)
    {
        var fixture = new Fixture();
        var request = fixture.Request with { Command = FluxVaultIpcCommand.RunBackupNow, OperationId = Guid.NewGuid() };
        fixture.Catalogue.Replay = new(request.OperationId.Value, request.VaultId!.Value, fixture.Caller.UserSid,
            request.Command, "fingerprint", completed ? VaultOperationState.Completed : VaultOperationState.Admitted, 6,
            completed ? FluxVaultIpcResponse.Ok() with { VaultId = request.VaultId, VaultRevision = 6, OperationId = request.OperationId } : null);
        var response = await fixture.Handler.HandleAsync(fixture.Caller, request);
        Assert.Equal(completed, response.Success);
        Assert.Equal(completed ? null : FluxVaultIpcErrorCode.OutcomeUnknown, response.ErrorCode);
        Assert.Equal(0, fixture.Executor.Calls);
        Assert.Equal(request.OperationId, response.OperationId);
    }

    [Fact]
    public async Task Plain_save_commits_through_catalogue_without_repository_execution()
    {
        var fixture = new Fixture();
        var request = fixture.Request with { Command = FluxVaultIpcCommand.SaveConfiguration, Configuration = fixture.Catalogue.Entry.Configuration, OperationId = Guid.NewGuid() };
        var response = await fixture.Handler.HandleAsync(fixture.Caller, request);
        Assert.True(response.Success);
        Assert.Equal(1, fixture.Catalogue.Saves);
        Assert.Equal(0, fixture.Executor.Calls);
        Assert.Equal(request.VaultId, response.VaultId);
    }

    [Fact]
    public async Task Admission_outage_explains_unavailability_without_repository_access_or_endpoint_disclosure()
    {
        var fixture = new Fixture();
        fixture.Catalogue.Error = new IOException("private database address and root");
        var response = await fixture.Handler.HandleAsync(fixture.Caller, fixture.Request);
        Assert.False(response.Success);
        Assert.Equal(FluxVaultIpcErrorCode.Unavailable, response.ErrorCode);
        Assert.DoesNotContain("private", response.ErrorMessage);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_exception_after_effect_is_unknown_without_automatic_reexecution(bool unexpected)
    {
        var fixture = new Fixture();
        fixture.Executor.Error = unexpected ? new InvalidOperationException("internal private detail") : new ArgumentException("late validation after effect");
        var request = fixture.Request with { Command = FluxVaultIpcCommand.RunBackupNow, OperationId = Guid.NewGuid() };
        var response = await fixture.Handler.HandleAsync(fixture.Caller, request);
        Assert.True(fixture.Executor.EffectObserved);
        Assert.Equal(1, fixture.Executor.Calls);
        Assert.False(response.Success);
        Assert.Equal(FluxVaultIpcErrorCode.OutcomeUnknown, response.ErrorCode);
        Assert.Equal(request.OperationId, response.OperationId);
        Assert.DoesNotContain("private", response.ErrorMessage);
    }

    [Fact]
    public async Task Read_executor_io_failure_does_not_claim_no_command_started()
    {
        var fixture = new Fixture();
        fixture.Executor.Error = new IOException("private repository path");
        var response = await fixture.Handler.HandleAsync(fixture.Caller, fixture.Request);
        Assert.Equal(1, fixture.Executor.Calls);
        Assert.False(response.Success);
        Assert.Equal(FluxVaultIpcErrorCode.Unavailable, response.ErrorCode);
        Assert.DoesNotContain("No repository command was started", response.ErrorMessage);
        Assert.DoesNotContain("private", response.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_catalogue_acknowledgement_loss_is_uncertain_even_for_payload_exceptions(bool json)
    {
        var fixture = new Fixture();
        fixture.Catalogue.Error = json ? new System.Text.Json.JsonException("lost committed acknowledgement") :
            new ArgumentException("lost committed acknowledgement");
        var request = fixture.Request with { Command = FluxVaultIpcCommand.SetProtectionPaused, OperationId = Guid.NewGuid() };

        var response = await fixture.Handler.HandleAsync(fixture.Caller, request);

        Assert.Equal(FluxVaultIpcErrorCode.OutcomeUnknown, response.ErrorCode);
        Assert.Equal(request.OperationId, response.OperationId);
        Assert.Equal(0, fixture.Executor.Calls);
    }

    private sealed class Fixture
    {
        internal Caller Caller { get; } = new();
        internal Catalogue Catalogue { get; } = new();
        internal Executor Executor { get; } = new();
        internal AuthenticatedFluxVaultRequestHandler Handler => new(Catalogue, Executor);
        internal FluxVaultIpcRequest Request => new(FluxVaultIpcCommand.ListVersions, null, null, null, null, VaultId: Catalogue.Entry.Binding.Id, ExpectedVaultRevision: 7);
    }
    private sealed class Caller(bool impersonation = true) : FluxVaultCallerContext
    {
        public override string UserSid => "S-1-5-21-1-2-3-1001";
        public override IReadOnlySet<string> EnabledGroupSids => new HashSet<string>();
        public override bool IsElevated => false;
        public override bool ImpersonationPermitted => impersonation;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => throw new NotSupportedException();
        public override void Dispose() { }
    }
    private sealed class Catalogue : IVaultCatalogue
    {
        internal VaultCatalogueEntry Entry { get; } = new(new(VaultId.New(), @"C:\owned\repository", @"C:\owned\state", MetadataStoreConfiguration.CreateDefault(@"C:\owned")), 7, "Allowed", new("S-1-5-21-1-2-3-1001", []), FluxVaultConfiguration.CreateDefault(@"C:\owned"));
        internal int Calls;
        internal int Saves;
        internal Exception? Error;
        internal VaultOperationReceipt? Replay;
        public Task<VaultAdmission> AdmitAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Error is not null) throw Error;
            if (request.VaultId != Entry.Binding.Id && !(request.VaultId is null && request.Command == FluxVaultIpcCommand.GetStatus))
                throw new VaultCatalogueException(VaultCatalogueFailure.Denied);
            return Task.FromResult(new VaultAdmission(Entry, Replay, Replay is not null));
        }
        public Task<VaultAdmission> SaveConfigurationAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Saves++;
            return Task.FromResult(new VaultAdmission(Entry, new(request.OperationId!.Value, Entry.Binding.Id, caller.UserSid, request.Command, "fingerprint", VaultOperationState.Completed, Entry.Revision,
                FluxVaultIpcResponse.Ok() with { VaultId = Entry.Binding.Id, VaultRevision = Entry.Revision, OperationId = request.OperationId }), false));
        }
        public Task<VaultAdmission> SetAccessAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VaultOperationReceipt?> GetReceiptAsync(FluxVaultCallerContext caller, VaultId vaultId, Guid operationId, CancellationToken cancellationToken = default) => Task.FromResult(Replay);
        public Task CompleteAsync(VaultOperationReceipt receipt, FluxVaultIpcResponse response, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Executor : IAuthorisedVaultCommandExecutor
    {
        internal bool Supported = true;
        public bool CanExecute(FluxVaultIpcRequest request) => Supported;
        internal int Calls;
        internal FluxVaultCallerContext? Caller;
        internal FluxVaultIpcRequest? Request;
        internal VaultAdmission? Admission;
        internal Exception? Error;
        internal bool EffectObserved;
        public Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller, VaultAdmission admission, FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Caller = caller; Request = request; Admission = admission;
            EffectObserved = true;
            if (Error is not null) throw Error;
            return Task.FromResult(FluxVaultIpcResponse.Ok());
        }
    }
}
