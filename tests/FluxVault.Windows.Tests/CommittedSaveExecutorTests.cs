using System.Runtime.Versioning;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class CommittedSaveExecutorTests
{
    [Fact]
    public async Task Combined_save_purge_is_supported_and_reports_saved_configuration_when_storage_is_unavailable()
    {
        var fixture = new Fixture();
        await using var executor = new WindowsAuthorisedVaultCommandExecutor(fixture.Endpoint, fixture.Entry.Binding);

        Assert.True(executor.CanExecute(fixture.Request));
        var response = await executor.ExecuteAsync(fixture.Caller, fixture.Admission, fixture.Request);

        Assert.True(response.Success);
        Assert.NotNull(response.Purge);
        Assert.False(response.Purge.Success);
        Assert.False(Directory.Exists(fixture.Entry.Binding.RepositoryPath));
    }

    [Theory]
    [InlineData("receipt-revision")]
    [InlineData("request-revision")]
    [InlineData("permissions")]
    public async Task Combined_save_purge_refuses_inconsistent_admission_before_storage(string defect)
    {
        var fixture = new Fixture();
        var request = defect == "request-revision" ? fixture.Request with { ExpectedVaultRevision = 5 } : fixture.Request;
        var admission = fixture.Admission;
        if (defect == "receipt-revision") admission = admission with { Receipt = admission.Receipt! with { Revision = 5 } };
        if (defect == "permissions") admission = admission with
        {
            Vault = fixture.Entry with { Access = new("S-1-5-21-1-2-3-1002", [new(fixture.Caller.UserSid, VaultPermission.ManageProtection)]) }
        };
        await using var executor = new WindowsAuthorisedVaultCommandExecutor(fixture.Endpoint, fixture.Entry.Binding);

        var response = await executor.ExecuteAsync(fixture.Caller, admission, request);

        Assert.False(response.Success);
        Assert.Equal(FluxVaultIpcErrorCode.Denied, response.ErrorCode);
        Assert.Null(response.Purge);
        Assert.False(Directory.Exists(fixture.Entry.Binding.RepositoryPath));
    }

    private sealed class Fixture
    {
        internal readonly Caller Caller = new();
        internal VaultCatalogueEndpoint Endpoint { get; }
        internal VaultCatalogueEntry Entry { get; }
        internal FluxVaultIpcRequest Request { get; }
        internal VaultAdmission Admission { get; }
        internal Fixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "FluxVault.Tests", Guid.NewGuid().ToString("N"));
            Endpoint = new(Guid.NewGuid(), "127.0.0.1", 1, "owned_fixture", "fixture_service");
            var metadata = MetadataStoreConfiguration.CreateDefault(root) with
                { Host = Endpoint.Host, Port = Endpoint.Port, DatabaseName = Endpoint.Database, Username = Endpoint.ServiceRole };
            var binding = new VaultBinding(new(Endpoint.InstanceId), Path.Combine(root, "repository"), Path.Combine(root, "state"), metadata);
            var configuration = FluxVaultConfiguration.CreateDefault(root) with { RepositoryPath = binding.RepositoryPath, MetadataStore = metadata };
            Entry = new(binding, 7, "FluxVault", new(Caller.UserSid, []), configuration);
            Request = FluxVaultIpcRequest.SaveConfiguration(configuration, purgeRemovedSelections: true,
                removedSelections: [new(Path.Combine(root, "cad"), RepositoryPurgeScopeKind.RecursiveFolder)], preservedSelections: []) with
                { VaultId = binding.Id, ExpectedVaultRevision = 6, OperationId = Guid.NewGuid() };
            Admission = new(Entry, new(Request.OperationId.Value, binding.Id, Caller.UserSid, Request.Command, "owned payload",
                VaultOperationState.Admitted, Entry.Revision, null, VaultPermission.ManageProtection | VaultPermission.Maintain | VaultPermission.DeleteHistory), false);
        }
    }

    private sealed class Caller : FluxVaultCallerContext
    {
        public override string UserSid => "S-1-5-21-1-2-3-1001";
        public override IReadOnlySet<string> EnabledGroupSids => new HashSet<string>();
        public override bool IsElevated => false;
        public override bool ImpersonationPermitted => true;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => throw new NotSupportedException("Purge must not access caller files.");
        public override void Dispose() { }
    }
}
