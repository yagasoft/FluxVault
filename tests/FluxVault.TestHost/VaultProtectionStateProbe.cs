using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;

internal static class VaultProtectionStateProbe
{
    internal static async Task RunAsync(PostgreSqlVaultCatalogue store, FluxVaultCallerContext owner,
        FluxVaultCallerContext ungranted, List<string> checks)
    {
        var baseline = (await store.AdmitAsync(owner, FluxVaultIpcRequest.GetStatus())).Vault;
        var executor = new NoRepositoryExecutor();
        var handler = new AuthenticatedFluxVaultRequestHandler(store, executor);
        var request = Request(baseline.Revision);
        var denied = await handler.HandleAsync(ungranted, request);
        Check(!denied.Success && denied.ErrorCode == FluxVaultIpcErrorCode.Denied, "ungranted toggle is refused before configuration or receipt mutation");
        await NoReceipt(request, "denied toggle leaves no receipt");
        var toggled = await handler.HandleAsync(owner, request);
        Check(toggled.Success && toggled.VaultRevision == baseline.Revision + 1 && executor.Calls == 0,
            "toggle completes in real catalogue without repository execution");
        var current = (await store.AdmitAsync(owner, FluxVaultIpcRequest.GetStatus())).Vault;
        Check(current.Revision == baseline.Revision + 1 && JsonSerializer.Serialize(current.Configuration) ==
            JsonSerializer.Serialize(baseline.Configuration with { IsEnabled = !baseline.Configuration.IsEnabled }),
            "toggle changes only IsEnabled and preserves every untouched setting");
        var replay = await handler.HandleAsync(owner, request);
        var receipt = await store.GetReceiptAsync(owner, baseline.Binding.Id, request.OperationId!.Value);
        Check(JsonSerializer.Serialize(toggled) == JsonSerializer.Serialize(replay) && receipt is
            { State: VaultOperationState.Completed, RequiredPermissions: VaultPermission.ManageProtection } &&
            JsonSerializer.Serialize(receipt.Response) == JsonSerializer.Serialize(toggled),
            "toggle exact replay and receipt retain the atomic committed revision without another effect");
        var stale = request with { OperationId = Guid.NewGuid() };
        var refused = await handler.HandleAsync(owner, stale);
        Check(refused.ErrorCode == FluxVaultIpcErrorCode.StaleRevision, "another toggle at an old revision is refused");
        await NoReceipt(stale, "stale toggle leaves no receipt");
        var collision = await handler.HandleAsync(owner, request with { Configuration = baseline.Configuration });
        Check(collision.ErrorCode == FluxVaultIpcErrorCode.OperationConflict, "toggle operation identity cannot be reused with a different payload");
        foreach (var malformed in new[] { Request(current.Revision) with { Configuration = baseline.Configuration },
            Request(current.Revision) with { PurgeRemovedSelections = true },
            Request(current.Revision) with { AccessGrants = [] }, Request(current.Revision) with { RemovedSelections = [] },
            Request(current.Revision) with { PreservedSelections = [] } })
        {
            Check((await handler.HandleAsync(owner, malformed)).ErrorCode == FluxVaultIpcErrorCode.InvalidRequest,
                "toggle refuses unrelated write payload before admission: " + checks.Count);
            await NoReceipt(malformed, "invalid toggle leaves no receipt: " + checks.Count);
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var cancelledRequest = Request(current.Revision);
            var outcome = await handler.HandleAsync(owner, cancelledRequest, cancelled.Token);
            Check(outcome.ErrorCode == FluxVaultIpcErrorCode.Unavailable && outcome.ErrorMessage!.Contains("before admission", StringComparison.Ordinal),
                "cancelled queued toggle is a definite refusal");
            await NoReceipt(cancelledRequest, "cancelled toggle leaves no receipt");
        }
        var syncRead = await store.AdmitAsync(owner, new(FluxVaultIpcCommand.GetSyncStatus, null, null, null, null,
            VaultId: baseline.Binding.Id, OperationId: Guid.NewGuid()));
        Check(syncRead.Receipt is null && syncRead.Vault.Revision == current.Revision, "sync status is read-only without a mutation receipt");
        var lost = Request(current.Revision);
        await using (var disconnected = new PostgreSqlVaultCatalogue(store.Endpoint)
            { AfterMutationCommit = (_, _) => throw new JsonException("fixture lost acknowledgement after toggle WAL commit") })
        {
            var uncertain = new AuthenticatedFluxVaultRequestHandler(disconnected, executor);
            var outcome = await uncertain.HandleAsync(owner, lost);
            Check(outcome.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown && outcome.OperationId == lost.OperationId && executor.Calls == 0,
                "postcommit payload exception retains uncertainty without repository execution");
            var completed = await store.GetReceiptAsync(owner, baseline.Binding.Id, lost.OperationId!.Value);
            Check(completed?.Response?.Success == true && completed.Revision == current.Revision + 1,
                "real store retains completed toggle after acknowledgement loss");
            var reconciled = await uncertain.HandleAsync(owner, lost);
            Check(JsonSerializer.Serialize(reconciled) == JsonSerializer.Serialize(completed!.Response),
                "same lost toggle returns its completed receipt without retoggling");
        }
        var final = (await store.AdmitAsync(owner, FluxVaultIpcRequest.GetStatus())).Vault;
        Check(final.Revision == baseline.Revision + 2 && JsonSerializer.Serialize(final.Configuration) == JsonSerializer.Serialize(baseline.Configuration) && executor.Calls == 0,
            "two real toggles and all replays restore the full baseline with exactly two revisions");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldRequest = Request(final.Revision);
        await using (var heldStore = new PostgreSqlVaultCatalogue(store.Endpoint)
            { AfterMutationCommit = (operation, token) => operation == heldRequest.OperationId ? HoldAsync(token) : Task.CompletedTask })
        {
            var snapshotExecutor = new SnapshotExecutor();
            var sequencing = new AuthenticatedFluxVaultRequestHandler(heldStore, snapshotExecutor);
            var toggleTask = sequencing.HandleAsync(owner, heldRequest);
            Task<FluxVaultIpcResponse>? backupTask = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                backupTask = sequencing.HandleAsync(owner, FluxVaultIpcRequest.RunBackupNow() with
                    { VaultId = baseline.Binding.Id, ExpectedVaultRevision = final.Revision + 1, OperationId = Guid.NewGuid() });
                Check(!backupTask.IsCompleted && snapshotExecutor.Calls == 0,
                    "queued backup remains behind the real committed toggle until its acknowledgement is released");
            }
            finally
            {
                release.TrySetResult();
                await toggleTask;
                if (backupTask is not null) await backupTask;
            }
            Check((await toggleTask).Success && backupTask is not null && (await backupTask).Success && snapshotExecutor.Calls == 1 &&
                snapshotExecutor.Observed?.Revision == final.Revision + 1 &&
                JsonSerializer.Serialize(snapshotExecutor.Observed.Configuration) == JsonSerializer.Serialize(final.Configuration with { IsEnabled = !final.Configuration.IsEnabled }),
                "queued backup receives the new authoritative configuration and revision, never the pre-toggle snapshot");
        }
        var restored = await handler.HandleAsync(owner, Request(final.Revision + 1));
        var restoredRecord = (await store.AdmitAsync(owner, FluxVaultIpcRequest.GetStatus())).Vault;
        Check(restored.Success && restoredRecord.Revision == final.Revision + 2 &&
            JsonSerializer.Serialize(restoredRecord.Configuration) == JsonSerializer.Serialize(baseline.Configuration),
            "sequencing checks restore the complete baseline through another atomic toggle");

        FluxVaultIpcRequest Request(long revision) => FluxVaultIpcRequest.SetProtectionPaused() with
            { VaultId = baseline.Binding.Id, ExpectedVaultRevision = revision, OperationId = Guid.NewGuid() };
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Protection state contract failed: " + name); checks.Add(name); }
        async Task NoReceipt(FluxVaultIpcRequest command, string name)
        {
            try { await store.GetReceiptAsync(owner, baseline.Binding.Id, command.OperationId!.Value); throw new InvalidOperationException(name); }
            catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.Denied) { checks.Add(name); }
        }
        async Task HoldAsync(CancellationToken token) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
    }

    private sealed class NoRepositoryExecutor : IAuthorisedVaultCommandExecutor
    {
        internal int Calls;
        public Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller, VaultAdmission admission,
            FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("A catalogue-only toggle reached repository execution."); }
    }

    private sealed class SnapshotExecutor : IAuthorisedVaultCommandExecutor
    {
        internal int Calls;
        internal VaultCatalogueEntry? Observed;
        public Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller, VaultAdmission admission,
            FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Command != FluxVaultIpcCommand.RunBackupNow) throw new InvalidOperationException("Toggle escaped the catalogue-only path.");
            Calls++; Observed = admission.Vault; return Task.FromResult(FluxVaultIpcResponse.Ok());
        }
    }
}
