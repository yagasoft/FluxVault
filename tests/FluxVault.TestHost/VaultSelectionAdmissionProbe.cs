using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;

namespace FluxVault.TestHost;

internal static class VaultSelectionAdmissionProbe
{
    internal static async Task RunAsync(PostgreSqlVaultCatalogue store, FluxVaultCallerContext owner, VaultCatalogueEntry current, List<string> checks)
    {
        var valid = FluxVaultIpcRequest.RunRestoreSelection(@"C:\not-created\source.txt", false,
            RestoreSelectionDestinationMode.Elsewhere, @"C:\not-created\recovered.txt");
        var invalid = new[]
        {
            valid with { SourcePath = "relative" }, valid with { SourcePath = @"C:\work\CON.txt" },
            valid with { OutputPath = "relative" }, valid with { OutputPath = @"C:\work\\ambiguous" },
            valid with { DestinationMode = (RestoreSelectionDestinationMode)999 },
            valid with { DestinationMode = RestoreSelectionDestinationMode.Original }
        };
        foreach (var command in new[] { FluxVaultIpcCommand.PreviewRestoreSelection, FluxVaultIpcCommand.RunRestoreSelection })
        foreach (var item in invalid)
        {
            var request = item with { Command = command, VaultId = current.Binding.Id, ExpectedVaultRevision = current.Revision, OperationId = Guid.NewGuid() };
            try { await store.AdmitAsync(owner, request); throw new InvalidOperationException("Invalid selection reached admission: " + command); }
            catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.InvalidConfiguration) { }
            try { await store.GetReceiptAsync(owner, current.Binding.Id, request.OperationId!.Value); throw new InvalidOperationException("Invalid selection left a receipt."); }
            catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.Denied) { }
            checks.Add("invalid selection refused before receipt: " + command + "/" + checks.Count);
        }
        var preview = await store.AdmitAsync(owner, valid with { Command = FluxVaultIpcCommand.PreviewRestoreSelection,
            VaultId = current.Binding.Id, ExpectedVaultRevision = current.Revision });
        if (preview.Receipt is not null) throw new InvalidOperationException("Advisory selection preview admitted a mutation.");
        checks.Add("selection preview is read-only without privileged filesystem inspection");
        valid = valid with { VaultId = current.Binding.Id, ExpectedVaultRevision = current.Revision, OperationId = Guid.NewGuid() };
        var admitted = await store.AdmitAsync(owner, valid);
        if (admitted.Receipt is not { State: VaultOperationState.Admitted, RequiredPermissions: VaultPermission.Recover })
            throw new InvalidOperationException("Selection admission lost recover authority.");
        var response = FluxVaultIpcResponse.WithRestoreSelection(new(valid.SourcePath!, false, RestoreSelectionDestinationMode.Elsewhere,
            valid.OutputPath!, 1, 0, 1, [], ["Publication warning"])) with
        { OutputPath = valid.OutputPath, RestoreResult = new(valid.OutputPath!, 10, 1, ["Publication warning"]) };
        await store.CompleteAsync(admitted.Receipt, response);
        var replay = await store.AdmitAsync(owner, valid);
        if (!replay.IsReplay || replay.Receipt?.Response?.RestoreResult?.VerifiedLogicalBytes != 10 ||
            replay.Receipt.Response.RestoreSelection?.Warnings?.SingleOrDefault() != "Publication warning")
            throw new InvalidOperationException("Selection receipt lost verification or warnings.");
        checks.Add("real selection receipt retains verified result and warnings without re-execution");
    }
}
