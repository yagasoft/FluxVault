using System.Runtime.Versioning;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceExecutorTests
{
    [Fact]
    public void Repeated_separators_are_rejected_before_Windows_normalisation()
    {
        Assert.Throws<UnauthorizedAccessException>(() => WindowsCallerFileAccess.ValidatePath(@"C:\work\\ambiguous"));
    }
    public static TheoryData<FluxVaultIpcCommand> SupportedCommands => new()
    {
        FluxVaultIpcCommand.GetRepositoryHealth, FluxVaultIpcCommand.PreviewRetention, FluxVaultIpcCommand.RunRetentionNow,
        FluxVaultIpcCommand.RunRepositoryScrub, FluxVaultIpcCommand.RunRestoreRehearsal,
        FluxVaultIpcCommand.PreviewMirrorRepair, FluxVaultIpcCommand.RunMirrorRepair,
        FluxVaultIpcCommand.PreviewMirrorRebalance, FluxVaultIpcCommand.RunMirrorRebalance, FluxVaultIpcCommand.PreviewMirrorDrain,
        FluxVaultIpcCommand.RunMirrorDrain, FluxVaultIpcCommand.ExportDiagnostics,
        FluxVaultIpcCommand.PreviewRestoreSelection, FluxVaultIpcCommand.RunRestoreSelection
    };

    [Theory]
    [MemberData(nameof(SupportedCommands))]
    public async Task Supported_maintenance_commands_reach_the_protected_storage_boundary(FluxVaultIpcCommand command)
    {
        var fixture = new Fixture(command);
        await using var executor = new WindowsAuthorisedVaultCommandExecutor(fixture.Endpoint, fixture.Entry.Binding);

        Assert.True(executor.CanExecute(fixture.Request));
        var response = await executor.ExecuteAsync(fixture.Caller, fixture.Admission, fixture.Request);

        Assert.Equal(FluxVaultIpcErrorCode.Unavailable, response.ErrorCode);
        Assert.Contains("Protected vault storage", response.ErrorMessage);
        Assert.False(Directory.Exists(fixture.Entry.Binding.RepositoryPath));
    }

    [Theory]
    [InlineData(FluxVaultIpcCommand.RunRetentionNow, VaultPermission.Maintain)]
    [InlineData(FluxVaultIpcCommand.RunRepositoryScrub, VaultPermission.ReadHistory)]
    [InlineData(FluxVaultIpcCommand.RunRestoreRehearsal, VaultPermission.ReadHistory)]
    [InlineData(FluxVaultIpcCommand.RunMirrorRepair, VaultPermission.ReadHistory)]
    [InlineData(FluxVaultIpcCommand.RunMirrorRebalance, VaultPermission.ReadHistory)]
    [InlineData(FluxVaultIpcCommand.PreviewMirrorDrain, VaultPermission.Maintain)]
    [InlineData(FluxVaultIpcCommand.RunMirrorDrain, VaultPermission.Maintain)]
    [InlineData(FluxVaultIpcCommand.GetRepositoryHealth, VaultPermission.Maintain)]
    [InlineData(FluxVaultIpcCommand.ExportDiagnostics, VaultPermission.Maintain)]
    [InlineData(FluxVaultIpcCommand.PreviewRestoreSelection, VaultPermission.ReadHistory)]
    [InlineData(FluxVaultIpcCommand.RunRestoreSelection, VaultPermission.ReadHistory)]
    public async Task Missing_required_permissions_are_refused_before_storage(FluxVaultIpcCommand command, VaultPermission granted)
    {
        var fixture = new Fixture(command);
        var admission = fixture.Admission with { Vault = fixture.Entry with
            { Access = new("S-1-5-21-1-2-3-1002", [new(fixture.Caller.UserSid, granted)]) } };
        await using var executor = new WindowsAuthorisedVaultCommandExecutor(fixture.Endpoint, fixture.Entry.Binding);

        var response = await executor.ExecuteAsync(fixture.Caller, admission, fixture.Request);

        Assert.Equal(FluxVaultIpcErrorCode.Denied, response.ErrorCode);
        Assert.False(Directory.Exists(fixture.Entry.Binding.RepositoryPath));
    }

    private sealed class Fixture
    {
        internal readonly Caller Caller = new();
        internal VaultCatalogueEndpoint Endpoint { get; }
        internal VaultCatalogueEntry Entry { get; }
        internal FluxVaultIpcRequest Request { get; }
        internal VaultAdmission Admission { get; }
        internal Fixture(FluxVaultIpcCommand command)
        {
            var root = Path.Combine(Path.GetTempPath(), "FluxVault.Tests", Guid.NewGuid().ToString("N"));
            Endpoint = new(Guid.NewGuid(), "127.0.0.1", 1, "owned_fixture", "fixture_service");
            var metadata = MetadataStoreConfiguration.CreateDefault(root) with
                { Host = Endpoint.Host, Port = Endpoint.Port, DatabaseName = Endpoint.Database, Username = Endpoint.ServiceRole };
            var binding = new VaultBinding(new(Endpoint.InstanceId), Path.Combine(root, "repository"), Path.Combine(root, "state"), metadata);
            var configuration = FluxVaultConfiguration.CreateDefault(root) with { RepositoryPath = binding.RepositoryPath, MetadataStore = metadata };
            Entry = new(binding, 7, "FluxVault", new(Caller.UserSid, []), configuration);
            var mutation = PostgreSqlVaultCatalogue.IsMutation(command);
            Request = new FluxVaultIpcRequest(command, null, null, null, null, MirrorNodeId: "first", VaultId: binding.Id,
                ExpectedVaultRevision: mutation ? Entry.Revision : null, OperationId: mutation ? Guid.NewGuid() : null) with
                { ExportPath = command == FluxVaultIpcCommand.ExportDiagnostics ? Path.Combine(root, "output") : null };
            Admission = new(Entry, mutation ? new(Request.OperationId!.Value, binding.Id, Caller.UserSid, command, "owned payload",
                VaultOperationState.Admitted, Entry.Revision, null, VaultPermission.All) : null, false);
        }
    }

    private sealed class Caller : FluxVaultCallerContext
    {
        public override string UserSid => "S-1-5-21-1-2-3-1001";
        public override IReadOnlySet<string> EnabledGroupSids => new HashSet<string>();
        public override bool IsElevated => false;
        public override bool ImpersonationPermitted => true;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => throw new NotSupportedException("Maintenance must not access caller files.");
        public override void Dispose() { }
    }
}
