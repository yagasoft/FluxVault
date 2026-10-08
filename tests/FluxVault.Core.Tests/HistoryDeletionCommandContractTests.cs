using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;

namespace FluxVault.Core.Tests;

public sealed class HistoryDeletionCommandContractTests
{
    [Fact]
    public void Preview_is_read_only_and_requires_history_and_deliberate_deletion_authority()
    {
        Assert.True(VaultCommandPolicy.TryGet(FluxVaultIpcCommand.PreviewHistoryDeletion,out var preview));
        Assert.Equal(VaultPermission.ReadHistory|VaultPermission.Maintain|VaultPermission.DeleteHistory,preview.Permissions);
        Assert.False(PostgreSqlVaultCatalogue.IsMutation(FluxVaultIpcCommand.PreviewHistoryDeletion));
        Assert.True(VaultCommandPolicy.TryGet(FluxVaultIpcCommand.DeleteHistory,out var delete));
        Assert.Equal(VaultPermission.Maintain|VaultPermission.DeleteHistory,delete.Permissions);
        Assert.True(PostgreSqlVaultCatalogue.IsMutation(FluxVaultIpcCommand.DeleteHistory));
        Assert.Equal(38,(int)FluxVaultIpcCommand.PreviewHistoryDeletion);
        Assert.Equal(39,(int)FluxVaultIpcCommand.DeleteHistory);
        foreach(var retired in new[]{26,27,28,29,30,32})Assert.False(Enum.IsDefined((FluxVaultIpcCommand)retired));
    }

    [Fact]
    public void Deletion_payload_validation_refuses_other_mutations_or_caller_preservation_claims()
    {
        var scope=new RepositoryPurgeScope("C:\\owned-history",RepositoryPurgeScopeKind.RecursiveFolder);
        var request=FluxVaultIpcRequest.DeleteHistory(scope,new string('A',64));
        Assert.Equal(scope,HistoryDeletionRequestValidator.Validate(request));
        foreach(var invalid in new[]{request with {IsProtectionPaused=true},request with {PurgeRemovedSelections=true},
            request with {PreservedSelections=[]},request with {RemovedSelections=[]},request with {HistoryDeletionFingerprint=null},
            request with {HistoryDeletionScope=scope with {Kind=(RepositoryPurgeScopeKind)999}},request with {SourcePath="C:\\another"}})
            Assert.Throws<ArgumentException>(()=>HistoryDeletionRequestValidator.Validate(invalid));
        Assert.Throws<UnauthorizedAccessException>(()=>HistoryDeletionRequestValidator.Validate(request with {HistoryDeletionScope=scope with {SourcePath="relative"}}));
        Assert.Throws<ArgumentException>(()=>HistoryDeletionRequestValidator.Validate(FluxVaultIpcRequest.PreviewHistoryDeletion(scope) with {OperationId=Guid.NewGuid()}));
    }
}
