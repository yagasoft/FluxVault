using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Security;

public static class HistoryDeletionRequestValidator
{
    public static RepositoryPurgeScope Validate(FluxVaultIpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.Command is not (FluxVaultIpcCommand.PreviewHistoryDeletion or FluxVaultIpcCommand.DeleteHistory) ||
            request.HistoryDeletionScope is not { } scope || !Enum.IsDefined(scope.Kind))
            throw new ArgumentException("Choose one file or folder whose history is to be reviewed.");
        var path=WindowsLocalPath.Validate(scope.SourcePath);
        var execute=request.Command==FluxVaultIpcCommand.DeleteHistory;
        if(execute && (request.HistoryDeletionFingerprint is not {Length:64} || !request.HistoryDeletionFingerprint.All(Uri.IsHexDigit)) ||
            !execute && request.HistoryDeletionFingerprint is not null)
            throw new ArgumentException("Delete history requires the exact reviewed preview fingerprint.");
        var expected=(execute ? FluxVaultIpcRequest.DeleteHistory(scope,request.HistoryDeletionFingerprint!) : FluxVaultIpcRequest.PreviewHistoryDeletion(scope)) with
            {VaultId=request.VaultId,ExpectedVaultRevision=request.ExpectedVaultRevision,OperationId=execute ? request.OperationId : null};
        if(request!=expected)throw new ArgumentException("History deletion cannot include configuration changes, caller preservation claims or another command's payload.");
        return scope with {SourcePath=path};
    }
}
