using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;

internal static class WindowsProtectionStateProbe
{
    internal static async Task<long> RunAsync(NamedPipeFluxVaultClient client,
        Func<FluxVaultIpcRequest, FluxVaultIpcRequest> bind, Func<FluxVaultIpcRequest, Task<FluxVaultIpcResponse>> send,
        string sourceFile, string destination, List<string> checks, CancellationToken cancellationToken)
    {
        var before = await send(bind(FluxVaultIpcRequest.GetStatus()));
        var configuration = before.Status!.Configuration;
        if (!configuration.IsEnabled) throw new InvalidOperationException("Native protection state requires its enabled baseline.");
        var revision = before.VaultRevision!.Value;
        var pause = Bind(FluxVaultIpcRequest.SetProtectionPaused());
        var paused = await send(pause);
        Check(paused.VaultRevision == revision + 1, "native pause returns its atomic incremented revision");
        revision = paused.VaultRevision!.Value;
        var sync = await send(Bind(new(FluxVaultIpcCommand.GetSyncStatus, null, null, null, null)));
        Check(sync.Status?.Sync is not null && sync.VaultRevision == revision && sync.OperationId is null &&
            JsonSerializer.Serialize(sync.Status.Configuration) == JsonSerializer.Serialize(configuration with { IsEnabled = false }),
            "native sync snapshot is an authorised read and pause preserves every untouched setting");
        Check(sync.Status!.LastMessage.Contains("manual backup paused", StringComparison.Ordinal) &&
            sync.Status.LastMessage.Contains("Automatic protection unavailable", StringComparison.Ordinal),
            "paused status truthfully distinguishes disabled manual capture from unavailable automatic capture");
        var historical = await send(Bind(FluxVaultIpcRequest.ListVersions()));
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, 256 * 1024).Select(index => (byte)((index * 13 + 31) % 251)).ToArray(), cancellationToken);
        var disabledBackup = await send(Bind(FluxVaultIpcRequest.RunBackupNow()));
        Check(disabledBackup.Backup is { Success: true, CapturedFileCount: 0, EnumeratedFileCount: 0 } &&
            disabledBackup.Backup.Message == "Protection is disabled.", "paused backup does not enumerate or capture changed caller files");
        var unchangedHistory = await send(Bind(FluxVaultIpcRequest.ListVersions()));
        Check(JsonSerializer.Serialize(historical.Versions) == JsonSerializer.Serialize(unchangedHistory.Versions),
            "paused backup creates no history and performs no retention or deletion reconciliation");
        var diagnosticsRequest = Bind(FluxVaultIpcRequest.ExportDiagnostics(destination));
        var diagnostics = await send(diagnosticsRequest);
        using (var report = JsonDocument.Parse(await File.ReadAllBytesAsync(diagnostics.OutputPath!, cancellationToken)))
            Check(report.RootElement.GetProperty("status").GetProperty("lastMessage").GetString()!.Contains("manual backup paused", StringComparison.Ordinal),
                "paused diagnostics reports the same truthful capture state");
        File.Delete(diagnostics.OutputPath!);
        var resume = Bind(FluxVaultIpcRequest.SetProtectionPaused());
        var resumed = await send(resume);
        Check(resumed.VaultRevision == revision + 1, "native resume publishes its own atomic revision");
        revision = resumed.VaultRevision!.Value;
        var replay = await send(pause);
        var receipt = await send(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
            VaultId: pause.VaultId, OperationId: pause.OperationId));
        Check(JsonSerializer.Serialize(paused) == JsonSerializer.Serialize(replay) && JsonSerializer.Serialize(paused) == JsonSerializer.Serialize(receipt),
            "replaying old pause after resume returns its historical receipt without toggling the current configuration");
        var status = await send(Bind(FluxVaultIpcRequest.GetStatus()));
        Check(status.VaultRevision == revision && JsonSerializer.Serialize(status.Status!.Configuration) == JsonSerializer.Serialize(configuration) &&
            status.Status.LastMessage.Contains("manual backup available", StringComparison.Ordinal),
            "resume and old replay retain the complete enabled baseline and blocked automatic-protection notice");
        var backup = await send(Bind(FluxVaultIpcRequest.RunBackupNow()));
        Check(backup.Backup is { Success: true, CapturedFileCount: 1 }, "resumed manual backup captures the file changed while paused");
        var history = await send(Bind(FluxVaultIpcRequest.ListVersions()));
        var latest = history.Versions!.Where(version => version.SourcePath == sourceFile).OrderByDescending(version => version.CapturedAtUtc).First();
        var output = Path.Combine(destination, "after-pause-resume.docx");
        var recovered = await send(Bind(FluxVaultIpcRequest.RestoreVersion(latest.VersionId, output)));
        Check(recovered.RestoreResult?.VerifiedLogicalBytes == new FileInfo(sourceFile).Length && Hash(sourceFile) == Hash(output),
            "pause-resume revisions preserve backup-history-verified-recovery through the real pipe");
        return revision;

        FluxVaultIpcRequest Bind(FluxVaultIpcRequest request) => bind(request) with
            { ExpectedVaultRevision = PostgreSqlVaultCatalogue.IsMutation(request.Command) ? revision : null };
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Native protection state failed: " + name); checks.Add(name); }
        static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    }
}
