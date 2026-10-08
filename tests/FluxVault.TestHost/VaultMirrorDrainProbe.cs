using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;

namespace FluxVault.TestHost;

// Called only after the enclosing probe verifies ownership of its private SSPI database.
internal static class VaultMirrorDrainProbe
{
    internal static async Task RunAsync(PostgreSqlVaultCatalogue store, FluxVaultCallerContext owner,
        VaultCatalogueEntry initial, List<string> checks)
    {
        IVaultCatalogue catalogue = store;
        var current = initial;
        var request = Request();
        var admission = await store.AdmitAsync(owner, request);
        var receipt = admission.Receipt!;
        var response = Response("first");
        await Conflict(() => catalogue.CompleteMirrorDrainAsync(receipt, request with { MirrorNodeId = "second" }, response), "drain completion rejects a changed request fingerprint");
        await Conflict(() => catalogue.CompleteMirrorDrainAsync(receipt, request, Response("second")), "drain completion rejects a different reported destination");
        await Conflict(() => catalogue.CompleteMirrorDrainAsync(receipt with { RequiredPermissions = VaultPermission.ReadHistory }, request, response), "drain completion rejects a changed durable permission receipt");
        await Conflict(() => store.CompleteAsync(receipt, response), "ordinary completion cannot bypass drain configuration CAS");
        Check((await store.GetReceiptAsync(owner, current.Binding.Id, receipt.OperationId))?.State == VaultOperationState.Admitted,
            "rejected drain completion retains the admitted receipt");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { await catalogue.CompleteMirrorDrainAsync(receipt, request, response, cancellation.Token); throw new InvalidOperationException("Cancelled completion was accepted."); }
            catch (OperationCanceledException) { checks.Add("cancelled drain completion keeps its receipt admitted and configuration enabled"); }
        }
        var incomplete = await catalogue.CompleteMirrorDrainAsync(receipt, request, response with
        { MirrorRebalance = response.MirrorRebalance! with { Actions = [new(MirrorRebalanceActionKind.Unresolved, MirrorRebalanceArtefactKind.Chunk,
            "first", "First", "owned fixture path", "digest", 1, "unresolved copy")] } });
        Check(incomplete.Success && incomplete.VaultRevision == current.Revision && !incomplete.MirrorRebalance!.IsCompletedDrain,
            "remaining actions prevent disabling even with a healthy zero-count report");
        await Unchanged("incomplete drain preserves every configuration field and revision");

        request = Request(); receipt = (await store.AdmitAsync(owner, request)).Receipt!;
        var failure = await catalogue.CompleteMirrorDrainAsync(receipt, request, FluxVaultIpcResponse.Failure("No drain result"));
        Check(!failure.Success && failure.VaultRevision == current.Revision, "failed drain result is retained without a configuration revision");
        await Unchanged("failed drain completion preserves enabled mirrors and untouched settings");

        request = Request(); receipt = (await store.AdmitAsync(owner, request)).Receipt!;
        var newer = current.Configuration with { IsEnabled = !current.Configuration.IsEnabled };
        current = (await store.SaveConfigurationAsync(owner, new(FluxVaultIpcCommand.SaveConfiguration, newer, null, null, null,
            VaultId: current.Binding.Id, ExpectedVaultRevision: current.Revision, OperationId: Guid.NewGuid()))).Vault;
        try { await catalogue.CompleteMirrorDrainAsync(receipt, request, response); throw new InvalidOperationException("Stale completion was accepted."); }
        catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.StaleRevision)
        { checks.Add("drain completion CAS rejects newer configuration after repository effects"); }
        await Unchanged("failed drain CAS preserves newer configuration and revision");
        Check((await store.GetReceiptAsync(owner, current.Binding.Id, receipt.OperationId))?.State == VaultOperationState.Admitted,
            "failed drain CAS remains uncertain rather than falsely completed");

        request = Request(); receipt = (await store.AdmitAsync(owner, request)).Receipt!;
        await using (var cut = new PostgreSqlVaultCatalogue(store.Endpoint)
            { BeforeMirrorDrainCommit = (_, _) => throw new IOException("fixture failure before atomic commit") })
        {
            try { await ((IVaultCatalogue)cut).CompleteMirrorDrainAsync(receipt, request, response); throw new InvalidOperationException("Cut point did not fail."); }
            catch (IOException) { checks.Add("failure before drain commit rolls back configuration and receipt together"); }
        }
        await Unchanged("rolled-back drain has no configuration effect");
        Check((await store.GetReceiptAsync(owner, current.Binding.Id, receipt.OperationId))?.State == VaultOperationState.Admitted,
            "rolled-back drain keeps its original admitted revision");
        var completed = await catalogue.CompleteMirrorDrainAsync(receipt, request, response);
        var expected = current.Configuration with { MirrorSet = new(current.Configuration.MirrorSet.Nodes.Select(node =>
            node.Id == "first" ? node with { IsEnabled = false } : node).ToArray(), current.Configuration.MirrorSet.PlacementPolicy) };
        var latest = (await store.AdmitAsync(owner, FluxVaultIpcRequest.GetStatus() with { VaultId = current.Binding.Id })).Vault;
        Check(completed.VaultRevision == current.Revision + 1 && JsonSerializer.Serialize(expected) == JsonSerializer.Serialize(latest.Configuration),
            "completed drain atomically increments revision and disables only its requested mirror");
        var replay = await store.AdmitAsync(owner, request);
        var lookup = await store.GetReceiptAsync(owner, current.Binding.Id, receipt.OperationId);
        Check(replay.IsReplay && lookup?.State == VaultOperationState.Completed && lookup.Revision == latest.Revision &&
            JsonSerializer.Serialize(completed) == JsonSerializer.Serialize(replay.Receipt!.Response) &&
            JsonSerializer.Serialize(completed) == JsonSerializer.Serialize(lookup.Response),
            "disabled destination drain replays its exact completed response before current selector validation");
        current = latest;

        request = Request() with { MirrorNodeId = "second" }; receipt = (await store.AdmitAsync(owner, request)).Receipt!;
        await using (var cut = new PostgreSqlVaultCatalogue(store.Endpoint)
            { AfterMutationCommit = (_, _) => throw new IOException("fixture acknowledgement lost after drain commit") })
        {
            try { await ((IVaultCatalogue)cut).CompleteMirrorDrainAsync(receipt, request, Response("second")); throw new InvalidOperationException("Acknowledgement cut did not fail."); }
            catch (IOException) { checks.Add("lost drain acknowledgement leaves a durable atomic result"); }
        }
        lookup = await store.GetReceiptAsync(owner, current.Binding.Id, receipt.OperationId);
        replay = await store.AdmitAsync(owner, request);
        latest = (await store.AdmitAsync(owner, FluxVaultIpcRequest.GetStatus() with { VaultId = current.Binding.Id })).Vault;
        Check(lookup?.Response?.Success == true && lookup.Revision == current.Revision + 1 && latest.Revision == lookup.Revision &&
            !latest.Configuration.MirrorSet.Nodes.Single(node => node.Id == "second").IsEnabled && replay.IsReplay &&
            JsonSerializer.Serialize(lookup.Response) == JsonSerializer.Serialize(replay.Receipt!.Response),
            "lost drain acknowledgement reconciles exactly once without another configuration effect");
        current = latest;
        foreach (var selector in new string?[] { null, " ", "missing", "first", "third" })
        {
            var invalid = Request() with { MirrorNodeId = selector };
            try { await store.AdmitAsync(owner, invalid); throw new InvalidOperationException("Invalid drain selector was admitted."); }
            catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.InvalidConfiguration)
            { checks.Add("drain rejects missing, blank, unknown, disabled or final enabled destination: " + (selector ?? "null")); }
            try { await store.GetReceiptAsync(owner, current.Binding.Id, invalid.OperationId!.Value); throw new InvalidOperationException("Invalid drain left a receipt."); }
            catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.Denied) { }
        }
        await Unchanged("invalid and final-destination drain refusal preserves configuration and revision");

        FluxVaultIpcRequest Request() => FluxVaultIpcRequest.RunMirrorDrain("first") with
        { VaultId = current.Binding.Id, ExpectedVaultRevision = current.Revision, OperationId = Guid.NewGuid() };
        static FluxVaultIpcResponse Response(string selector) => FluxVaultIpcResponse.WithMirrorRebalance(new(DateTimeOffset.UtcNow,
            RepositoryHealthState.Healthy, 1, 0, 0, 0, [], [], MirrorRebalanceOperation.Drain, false, selector));
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Mirror drain contract failed: " + name); checks.Add(name); }
        async Task Unchanged(string name)
        {
            var observed = (await store.AdmitAsync(owner, FluxVaultIpcRequest.GetStatus() with { VaultId = current.Binding.Id })).Vault;
            Check(observed.Revision == current.Revision && JsonSerializer.Serialize(observed.Configuration) == JsonSerializer.Serialize(current.Configuration), name);
        }
        async Task Conflict(Func<Task> action, string name)
        {
            try { await action(); } catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.OperationConflict) { checks.Add(name); return; }
            throw new InvalidOperationException("Mirror drain contract did not reject: " + name);
        }
    }
}
