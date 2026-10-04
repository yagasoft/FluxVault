using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;

namespace FluxVault.TestHost;

internal static class VaultDiagnosticsExportProbe
{
    internal static async Task RunAsync(PostgreSqlVaultCatalogue store, FluxVaultCallerContext owner, VaultCatalogueEntry current, List<string> checks)
    {
        foreach (var directory in new[] { "relative", @"C:relative", @"\\server\share", @"C:\work\..\elsewhere", @"C:\work\CON.txt", @"C:\work\trailing.", @"C:\work\\ambiguous", "" })
        {
            var request = FluxVaultIpcRequest.ExportDiagnostics(directory) with
            { VaultId = current.Binding.Id, ExpectedVaultRevision = current.Revision, OperationId = Guid.NewGuid() };
            try { await store.AdmitAsync(owner, request); throw new InvalidOperationException("Invalid diagnostics directory reached admission: " + directory); }
            catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.InvalidConfiguration) { }
            try
            {
                await store.GetReceiptAsync(owner, current.Binding.Id, request.OperationId.Value);
                throw new InvalidOperationException("Invalid diagnostics directory left an accessible receipt.");
            }
            catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.Denied) { }
            checks.Add("invalid diagnostics directory refused before receipt: " + directory);
        }
        var valid = FluxVaultIpcRequest.ExportDiagnostics(@"C:\not-created\owned") with
        { VaultId = current.Binding.Id, ExpectedVaultRevision = current.Revision, OperationId = Guid.NewGuid() };
        var admitted = await store.AdmitAsync(owner, valid);
        if (admitted.Receipt is not { State: VaultOperationState.Admitted, RequiredPermissions: VaultPermission.ReadHistory })
            throw new InvalidOperationException("Diagnostics admission did not retain read-history authority.");
        checks.Add("syntactically valid diagnostics directory admits without privileged filesystem access");
        var output = @"C:\not-created\owned\report.json";
        var response = FluxVaultIpcResponse.WithOutputPath(output) with { DiagnosticsExport = new(output, ["Permissions need review."]) };
        await store.CompleteAsync(admitted.Receipt, response);
        var replay = await store.AdmitAsync(owner, valid);
        if (!replay.IsReplay || replay.Receipt?.Response?.DiagnosticsExport?.Warnings.SingleOrDefault() != "Permissions need review.")
            throw new InvalidOperationException("Diagnostics receipt lost publication warnings.");
        checks.Add("real diagnostics receipt round trip retains publication warning without re-execution");
    }
}
