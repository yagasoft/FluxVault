using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;

namespace FluxVault.TestHost;

internal static class WindowsSelectionRecoveryProbe
{
    internal const string HistoricalFirst = "generated first historical bytes";
    internal const string HistoricalSecond = "generated nested historical bytes";

    internal static async Task RunAsync(NamedPipeFluxVaultClient client, Func<FluxVaultIpcRequest, FluxVaultIpcRequest> bind,
        Func<FluxVaultIpcRequest, Task<FluxVaultIpcResponse>> send, string output, List<string> checks, CancellationToken token)
    {
        var file = Path.Combine(output, "office", "document.docx"); var source = Path.GetDirectoryName(file)!;
        var destination = Path.Combine(output, "destination"); var recovered = Path.Combine(destination, "selection.docx");
        var preview = await send(bind(FluxVaultIpcRequest.PreviewRestoreSelection(file, false, RestoreSelectionDestinationMode.Elsewhere, recovered)));
        Check(preview.RestoreSelection is { FileCount: 1, ConflictCount: 0, RestoredCount: 0 } && !File.Exists(recovered),
            "selection preview traverses blind fixture ancestors without creating output");
        var request = bind(FluxVaultIpcRequest.RunRestoreSelection(file, false, RestoreSelectionDestinationMode.Elsewhere, recovered));
        var result = await send(request);
        Check(Verified(result, recovered, 1) && Hash(file) == Hash(recovered), "selection file recovery returns independently verified caller output");
        await File.WriteAllTextAsync(recovered, "caller edited recovery", token);
        var replay = await send(request);
        var receipt = await send(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null, VaultId: request.VaultId, OperationId: request.OperationId));
        Check(JsonSerializer.Serialize(result) == JsonSerializer.Serialize(replay) && JsonSerializer.Serialize(result) == JsonSerializer.Serialize(receipt) &&
            await File.ReadAllTextAsync(recovered, token) == "caller edited recovery", "selection replay/status preserve caller edits and exact verification receipt");
        var refused = await client.SendAsync(request with { OperationId = Guid.NewGuid() }, token);
        Check(!refused.Success && refused.ErrorCode == FluxVaultIpcErrorCode.InvalidRequest && await File.ReadAllTextAsync(recovered, token) == "caller edited recovery",
            "selection refuses unconfirmed overwrite before publication");
        var refusalReceipt = await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null, VaultId: request.VaultId, OperationId: refused.OperationId), token);
        Check(JsonSerializer.Serialize(refused) == JsonSerializer.Serialize(refusalReceipt), "definite selection refusal has a durable outcome");
        var overwritten = await send(bind(FluxVaultIpcRequest.RunRestoreSelection(file, false, RestoreSelectionDestinationMode.Elsewhere, recovered, true)));
        Check(Verified(overwritten, recovered, 1) && Hash(file) == Hash(recovered), "explicit file overwrite uses caller-authorised publication");
        File.Delete(recovered); await send(request);
        Check(!File.Exists(recovered), "selection replay never recreates a deleted published file");
        var folderOutput = Path.Combine(destination, "selection-folder");
        var folderPreview = await send(bind(FluxVaultIpcRequest.PreviewRestoreSelection(source, true, RestoreSelectionDestinationMode.Elsewhere, folderOutput)));
        Check(folderPreview.RestoreSelection is { FileCount: 3, ConflictCount: 0 } && !Directory.Exists(folderOutput), "folder selection preview includes the earlier inherited CAD copy without creating output");
        var folderResult = await send(bind(FluxVaultIpcRequest.RunRestoreSelection(source, true, RestoreSelectionDestinationMode.Elsewhere, folderOutput)));
        Check(Verified(folderResult, folderOutput, 3) && Hash(file) == Hash(Path.Combine(folderOutput, "document.docx")) &&
            Hash(Path.Combine(source, "nested", "notes.docx")) == Hash(Path.Combine(folderOutput, "nested", "notes.docx")) &&
            Hash(Path.Combine(source, "drawing-copy.docx")) == Hash(Path.Combine(folderOutput, "drawing-copy.docx")), "exact folder selection publishes all three files as one independently verified tree");
        var existingFolder = await client.SendAsync(bind(FluxVaultIpcRequest.RunRestoreSelection(source, true, RestoreSelectionDestinationMode.Elsewhere, folderOutput, true)), token);
        Check(!existingFolder.Success && existingFolder.ErrorCode == FluxVaultIpcErrorCode.InvalidRequest &&
            Directory.EnumerateFiles(folderOutput, "*", SearchOption.AllDirectories).Count() == 3, "existing recovery folder is never merged despite overwrite flag");
        var wrongKind = await client.SendAsync(bind(FluxVaultIpcRequest.RunRestoreSelection(file, false, RestoreSelectionDestinationMode.Elsewhere, folderOutput)), token);
        Check(!wrongKind.Success && wrongKind.ErrorCode == FluxVaultIpcErrorCode.InvalidRequest, "wrong-kind existing destination is a conflict rather than missing");
        var privateDirectory = Path.Combine(Path.GetDirectoryName(output)!, "catalogue", "single", "state");
        var privatePath = Path.Combine(privateDirectory, "forbidden-selection.docx");
        var deniedPreview = await client.SendAsync(bind(FluxVaultIpcRequest.PreviewRestoreSelection(file, false, RestoreSelectionDestinationMode.Elsewhere, privatePath)), token);
        Check(!deniedPreview.Success && deniedPreview.RestoreSelection is null && deniedPreview.RestoreResult is null,
            "preview never treats an unavailable caller destination as missing");
        var denied = await client.SendAsync(bind(FluxVaultIpcRequest.RunRestoreSelection(file, false, RestoreSelectionDestinationMode.Elsewhere, privatePath)), token);
        Check(!denied.Success && denied.RestoreResult is null, "SYSTEM cannot recover into its private directory for an unauthorised caller");
        var historical = Path.Combine(output, "historical-fallback"); var forestOutput = Path.Combine(destination, "historical-folder");
        var forestPreview = await send(bind(FluxVaultIpcRequest.PreviewRestoreSelection(historical, true, RestoreSelectionDestinationMode.Elsewhere, forestOutput)));
        Check(forestPreview.RestoreSelection is { FileCount: 2 } && !Directory.Exists(forestOutput), "legacy independent-file history previews as one folder without output");
        var forest = await send(bind(FluxVaultIpcRequest.RunRestoreSelection(historical, true, RestoreSelectionDestinationMode.Elsewhere, forestOutput)));
        Check(Verified(forest, forestOutput, 2) && Hash(Path.Combine(forestOutput, "a.txt")) == HashText(HistoricalFirst) &&
            Hash(Path.Combine(forestOutput, "nested", "b.txt")) == HashText(HistoricalSecond), "real PostgreSQL fallback history publishes one independently verified caller tree");
        var noHistoryOutput = Path.Combine(destination, "never-published");
        var noHistory = await client.SendAsync(bind(FluxVaultIpcRequest.RunRestoreSelection(Path.Combine(output, "never-protected"), true,
            RestoreSelectionDestinationMode.Elsewhere, noHistoryOutput)), token);
        Check(!noHistory.Success && noHistory.ErrorCode == FluxVaultIpcErrorCode.InvalidRequest && !Directory.Exists(noHistoryOutput), "missing history cannot publish an empty success");
        var beforeDelete = Hash(file); File.Delete(file); await send(bind(FluxVaultIpcRequest.RunBackupNow()));
        var original = await send(bind(FluxVaultIpcRequest.RunRestoreSelection(file, false, RestoreSelectionDestinationMode.Original, file, true)));
        Check(Verified(original, file, 1) && Hash(file) == beforeDelete, "explicit tombstoned file restores verified last-good bytes to its original caller path");
        Check(!Directory.EnumerateFileSystemEntries(output, ".FluxVault-recovery-*", SearchOption.AllDirectories).Any(), "all selection recovery staging is gone before caller exit");

        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Selection recovery contract failed: " + name); checks.Add(name); }
        static bool Verified(FluxVaultIpcResponse response, string path, int files) => response.RestoreResult is { Warnings: not null } result &&
            result.OutputPath == path && result.RestoredFileCount == files && result.VerifiedLogicalBytes > 0 && response.OutputPath == path &&
            response.RestoreSelection is { FailedPaths.Count: 0, Warnings: not null } summary && summary.RestoredCount == files && summary.FileCount == files && summary.DestinationPath == path;
        static string Hash(string path) { using var bytes = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(bytes)); }
        static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
