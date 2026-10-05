using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Ipc;

namespace FluxVault.TestHost;

/// <summary>Actual authenticated executor: a locked working file never becomes a deletion or privileged capture.</summary>
internal static class WindowsManualSourceBoundaryProbe
{
    internal static async Task RunAsync(NamedPipeFluxVaultClient client, string sourceFile, string destination,
        List<string> checks, CancellationToken token)
    {
        var status = await Send(FluxVaultIpcRequest.GetStatus());
        var id = status.VaultId!.Value;
        var revision = status.VaultRevision!.Value;
        Check(status.Status!.Configuration.IsEnabled, "native locked-source proof starts with enabled protection");
        var history = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        var old = Latest(history, sourceFile);
        // Change both length and bytes before locking, so the unchanged-file fast path cannot explain zero capture.
        await File.AppendAllTextAsync(sourceFile, "\nchanged locked working file " + Guid.NewGuid().ToString("N"), token);
        var hash = Hash(sourceFile);
        var length = new FileInfo(sourceFile).Length;
        Check(length != old.LogicalLength, "native pending locked source differs from its recorded length");
        var request = Bind(FluxVaultIpcRequest.RunBackupNow());
        FluxVaultIpcResponse failed;
        await using (var held = new FileStream(sourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            failed = await client.SendAsync(request, token);
            Check(!failed.Success && failed.ErrorCode == FluxVaultIpcErrorCode.Unavailable &&
                failed.Backup is { Success: false, CapturedFileCount: 0, RecordedDeletionCount: 0, FailedFileCount: > 0 } &&
                failed.OperationId == request.OperationId && !string.IsNullOrWhiteSpace(failed.ErrorMessage),
                "native locked-source backup reports terminal failure without capture or deletion");
            var unchanged = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
            Check(Same(history.Versions, unchanged.Versions), "native locked-source failure preserves every durable history identity");
        }
        var replay = await client.SendAsync(request, token);
        var receipt = await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
            VaultId: id, OperationId: request.OperationId), token);
        Check(Same(failed, replay) && Same(failed, receipt),
            "native unlocked original-operation replay and receipt retain the exact terminal failure");
        var afterReplay = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        Check(Same(history.Versions, afterReplay.Versions), "native failed backup replay cannot capture after its source obstruction is removed");
        var backup = await Send(Bind(FluxVaultIpcRequest.RunBackupNow()));
        Check(backup.Backup is { Success: true, CapturedFileCount: 1, RecordedDeletionCount: 0 },
            "native explicitly new manual backup captures the released working file");
        var fresh = Latest(await Send(Bind(FluxVaultIpcRequest.ListVersions())), sourceFile);
        Check(fresh.VersionId != old.VersionId, "native released-source backup records a distinct history version");
        var output = Path.Combine(destination, "after-locked-source.docx");
        var recovery = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(fresh.VersionId, output)));
        Check(recovery.RestoreResult?.VerifiedLogicalBytes == length && Hash(output) == hash && Hash(sourceFile) == hash,
            "native released-source recovery independently matches the pending bytes and preserves the working file");

        FluxVaultIpcRequest Bind(FluxVaultIpcRequest value) => value with { VaultId = id,
            ExpectedVaultRevision = FluxVault.Core.Security.PostgreSqlVaultCatalogue.IsMutation(value.Command) ? revision : null,
            OperationId = FluxVault.Core.Security.PostgreSqlVaultCatalogue.IsMutation(value.Command) ? Guid.NewGuid() : null };
        async Task<FluxVaultIpcResponse> Send(FluxVaultIpcRequest value)
        {
            var result = await client.SendAsync(value, token);
            if (!result.Success) throw new InvalidOperationException("Native source boundary failed: " + value.Command + ": " + result.ErrorMessage);
            return result;
        }
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
    }

    private static RepositoryVersionSummary Latest(FluxVaultIpcResponse history, string source) => history.Versions!
        .Where(item => item.EntryKind == RepositoryEntryKind.File && item.SourcePath == source)
        .OrderByDescending(item => item.CapturedAtUtc).ThenByDescending(item => item.VersionId, StringComparer.Ordinal).First();
    private static bool Same<T>(T first, T second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
