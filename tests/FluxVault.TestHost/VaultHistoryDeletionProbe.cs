using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;

namespace FluxVault.TestHost;

// The enclosing probe has verified ownership of this private SSPI database.
internal static class VaultHistoryDeletionProbe
{
    internal static async Task RunAsync(PostgreSqlVaultCatalogue store,FluxVaultCallerContext owner,
        FluxVaultCallerContext administrator,FluxVaultCallerContext other,List<string> checks)
    {
        var initial = (await store.AdmitAsync(owner,FluxVaultIpcRequest.GetStatus())).Vault;
        var current = initial;
        var scope = new RepositoryPurgeScope(Path.Combine(initial.Binding.StateRoot,"owned-retired-history"),RepositoryPurgeScopeKind.RecursiveFolder);
        var executor = new Executor();
        var handler = new AuthenticatedFluxVaultRequestHandler(store,executor);
        foreach (var permissions in new[] { VaultPermission.ReadHistory,VaultPermission.Maintain,VaultPermission.DeleteHistory,
            VaultPermission.ReadHistory|VaultPermission.Maintain,VaultPermission.ReadHistory|VaultPermission.DeleteHistory,
            VaultPermission.Maintain|VaultPermission.DeleteHistory,VaultPermission.All })
        {
            current = (await store.SetAccessAsync(administrator,new(FluxVaultIpcCommand.SetVaultAccess,null,null,null,null,
                VaultId:current.Binding.Id,ExpectedVaultRevision:current.Revision,OperationId:Guid.NewGuid(),
                AccessGrants:[new(other.UserSid,permissions)]))).Vault;
            var calls = executor.Calls;
            var preview = await handler.HandleAsync(other,Preview());
            var canPreview = (permissions & (VaultPermission.ReadHistory|VaultPermission.Maintain|VaultPermission.DeleteHistory)) ==
                (VaultPermission.ReadHistory|VaultPermission.Maintain|VaultPermission.DeleteHistory);
            Check(preview.Success == canPreview && executor.Calls == calls + (canPreview ? 1 : 0),"history preview authority checked before executor: " + permissions);
            var request = Delete();
            calls = executor.Calls;
            var deleted = await handler.HandleAsync(other,request);
            var canDelete = (permissions & (VaultPermission.Maintain|VaultPermission.DeleteHistory)) == (VaultPermission.Maintain|VaultPermission.DeleteHistory);
            Check(deleted.Success == canDelete && executor.Calls == calls + (canDelete ? 1 : 0),"history deletion needs both destructive grants: " + permissions);
            if (!canDelete) await NoReceipt(request,"denied history deletion has no admitted receipt");
        }
        current = (await store.SetAccessAsync(administrator,new(FluxVaultIpcCommand.SetVaultAccess,null,null,null,null,
            VaultId:current.Binding.Id,ExpectedVaultRevision:current.Revision,OperationId:Guid.NewGuid(),AccessGrants:initial.Access.Grants))).Vault;
        var before = executor.Calls;
        foreach (var invalid in new[] { Delete() with { VaultId=VaultId.New() },Delete() with { ExpectedVaultRevision=current.Revision-1 },
            Delete() with { HistoryDeletionFingerprint=null },Delete() with { PreservedSelections=[] },Delete() with { Configuration=current.Configuration },
            Delete() with { IsProtectionPaused=true },Delete() with { HistoryDeletionScope=scope with {SourcePath="relative"} },
            Delete() with { HistoryDeletionScope=scope with {Kind=(RepositoryPurgeScopeKind)999} } })
        {
            var response = await handler.HandleAsync(owner,invalid);
            Check(!response.Success && response.ErrorCode is FluxVaultIpcErrorCode.Denied or FluxVaultIpcErrorCode.InvalidRequest or FluxVaultIpcErrorCode.StaleRevision,
                "malformed, wrong-binding or stale history deletion refused before execution: " + checks.Count);
            await NoReceipt(invalid,"refused deletion has no receipt: " + checks.Count);
        }
        Check(executor.Calls == before,"invalid history commands never reached the executor");
        var completeRequest = Delete();
        var completed = await handler.HandleAsync(owner,completeRequest);
        var replay = await handler.HandleAsync(owner,completeRequest);
        var receipt = await handler.HandleAsync(owner,new(FluxVaultIpcCommand.GetOperationStatus,null,null,null,null,
            VaultId:current.Binding.Id,OperationId:completeRequest.OperationId));
        Check(completed.Success && completed.VaultRevision == current.Revision && executor.Calls == before+1 &&
            JsonSerializer.Serialize(completed) == JsonSerializer.Serialize(replay) && JsonSerializer.Serialize(completed) == JsonSerializer.Serialize(receipt),
            "completed deletion replay and receipt have one effect and do not change configuration revision");
        Check((await handler.HandleAsync(owner,completeRequest with {HistoryDeletionFingerprint=new string('B',64)})).ErrorCode == FluxVaultIpcErrorCode.OperationConflict,
            "deletion operation cannot be retargeted to a different review");
        var beforeExecution = Delete();
        await using (var cut = new PostgreSqlVaultCatalogue(store.Endpoint)
            {AfterMutationCommit=(_,_)=>throw new IOException("fixture interruption after durable admission")})
        {
            var interrupted = new AuthenticatedFluxVaultRequestHandler(cut,executor);
            var unknown = await interrupted.HandleAsync(owner,beforeExecution);
            var again = await interrupted.HandleAsync(owner,beforeExecution);
            Check(unknown.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown && again.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown && executor.Calls == before+1,
                "interrupted durable deletion admission is uncertain and never executed on replay");
        }
        var originalReceipt = (await store.GetReceiptAsync(owner,current.Binding.Id,beforeExecution.OperationId!.Value))!;
        foreach (var destructive in new[] { Delete(),new FluxVaultIpcRequest(FluxVaultIpcCommand.RunRetentionNow,null,null,null,null,
            VaultId:current.Binding.Id,ExpectedVaultRevision:current.Revision,OperationId:Guid.NewGuid()),
            FluxVaultIpcRequest.SaveConfiguration(current.Configuration with
                {RetentionPolicy=current.Configuration.RetentionPolicy with {IsEnabled=true}}) with
                {
                VaultId=current.Binding.Id,ExpectedVaultRevision=current.Revision,OperationId=Guid.NewGuid() } })
        {
            var blocked = await handler.HandleAsync(owner,destructive);
            Check(!blocked.Success && blocked.ErrorCode == FluxVaultIpcErrorCode.InvalidRequest && executor.Calls == before+1,
                "unresolved deletion blocks fresh effective destructive admission: " + destructive.Command);
            await NoReceipt(destructive,"blocked fresh destructive operation leaves no receipt: " + destructive.Command);
        }
        Check((await handler.HandleAsync(owner,Preview())).Success &&
            (await store.GetReceiptAsync(owner,current.Binding.Id,beforeExecution.OperationId.Value))?.State==VaultOperationState.Admitted,
            "read-only review remains available and does not resolve or mutate the original admitted deletion");
        before++; // The permitted read-only preview invoked the counting executor.
        // The fixture cut above proves no executor ran. Explicitly complete that
        // known no-effect fixture operation before testing another interruption.
        await store.CompleteAsync(originalReceipt,FluxVaultIpcResponse.Failure("fixture-confirmed no effect") with
            {ErrorCode=FluxVaultIpcErrorCode.InvalidRequest,VaultId=current.Binding.Id,VaultRevision=current.Revision,OperationId=beforeExecution.OperationId});
        executor.ThrowAfterEffect = true;
        var afterEffect = Delete();
        var partial = await handler.HandleAsync(owner,afterEffect);
        var partialReplay = await handler.HandleAsync(owner,afterEffect);
        var retained = await store.GetReceiptAsync(owner,current.Binding.Id,afterEffect.OperationId!.Value);
        Check(partial.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown && partialReplay.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown &&
            retained?.State == VaultOperationState.Admitted && executor.Calls == before+2,
            "interrupted deletion after an effect keeps its admitted operation and never repeats it");
        Check(JsonSerializer.Serialize((await store.AdmitAsync(owner,FluxVaultIpcRequest.GetStatus())).Vault.Configuration) == JsonSerializer.Serialize(initial.Configuration),
            "standalone history contracts preserve every configuration setting");

        FluxVaultIpcRequest Preview()=>FluxVaultIpcRequest.PreviewHistoryDeletion(scope) with {VaultId=current.Binding.Id,ExpectedVaultRevision=current.Revision};
        FluxVaultIpcRequest Delete()=>FluxVaultIpcRequest.DeleteHistory(scope,new string('A',64)) with
            {VaultId=current.Binding.Id,ExpectedVaultRevision=current.Revision,OperationId=Guid.NewGuid()};
        void Check(bool condition,string name) {if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
        async Task NoReceipt(FluxVaultIpcRequest request,string name)
        {
            try {await store.GetReceiptAsync(owner,current.Binding.Id,request.OperationId!.Value);throw new InvalidOperationException(name);}
            catch(VaultCatalogueException exception) when(exception.Failure==VaultCatalogueFailure.Denied){checks.Add(name);}
        }
    }

    private sealed class Executor : IAuthorisedVaultCommandExecutor
    {
        internal int Calls;
        internal bool ThrowAfterEffect;
        public Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller,VaultAdmission admission,
            FluxVaultIpcRequest request,CancellationToken cancellationToken=default)
        {
            Calls++;
            if (ThrowAfterEffect) throw new IOException("fixture interruption after an observable effect");
            return Task.FromResult(request.Command == FluxVaultIpcCommand.DeleteHistory
                ? FluxVaultIpcResponse.WithPurge(new(1,1,10,[])) : FluxVaultIpcResponse.Ok());
        }
    }
}
