using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Ipc;
using FluxVault.Testing;
using FluxVault.Windows.Security;

namespace FluxVault.TestHost;

/// <summary>Clean native service-process replacement in one existing owned fixture.</summary>
internal static class WindowsServiceRestartProbe
{
    private static readonly JsonSerializerOptions Json = new()
    { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const int CheckpointLimit = 128 * 1024;

    internal static async Task<int> RunAsync(string configurationPath, string actor, string phase)
    {
        var fixture = WindowsDatabaseProbeConfiguration.Read(configurationPath);
        using var identity = WindowsIdentity.GetCurrent();
        if (!fixture.RunRestartTests || !fixture.RunSingleVaultTests ||
            !fixture.Actors.TryGetValue(actor, out var expected) || identity.User?.Value != expected ||
            actor != "System" && (actor != "A" || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)))
            throw new UnauthorizedAccessException("Restart proof requires its exact native fixture actor.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(actor == "System" ? 60 : 20));
        if (actor == "System")
        {
            if (phase != "server") throw new ArgumentException("Unknown SYSTEM restart phase.");
            return await RunServerAsync(fixture, deadline);
        }
        if (phase is not ("before" or "after")) throw new ArgumentException("Unknown creator restart phase.");

        var id = new VaultId(Guid.ParseExact(fixture.FixtureId, "N"));
        var client = new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(
            "FluxVault.Tests." + fixture.FixtureId, fixture.Actors["System"]));
        var checks = new List<string>();
        var status = await Send(FluxVaultIpcRequest.GetStatus());
        Check(status.VaultId == id && status.VaultRevision is > 0 && status.Status is not null,
            "native creator receives the same installation binding");
        var revision = status.VaultRevision!.Value;
        var sourcePath = Path.Combine(fixture.Root, "output-A", "office", "document.docx");
        var checkpointPath = Path.Combine(fixture.Root, "output-A", "restart-baseline.json");

        if (phase == "before")
        {
            var saveRequest = Bind(FluxVaultIpcRequest.SaveConfiguration(status.Status!.Configuration));
            var saved = await Send(saveRequest);
            Check(saved.OperationId == saveRequest.OperationId && saved.VaultRevision == revision + 1,
                "complete configuration save has a durable revisioned receipt");
            revision = saved.VaultRevision!.Value;
            var accepted = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
            Check(Same(status.Status.Configuration, accepted.Status!.Configuration), "saving preserves the complete accepted configuration");
            var backupRequest = Bind(FluxVaultIpcRequest.RunBackupNow());
            var backup = await Send(backupRequest);
            Check(backup.OperationId == backupRequest.OperationId && backup.Backup is { Success: true }, "backup has its original successful receipt");
            var history = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
            var version = LatestFile(history.Versions!, sourcePath);
            var hash = Hash(sourcePath);
            var sourceBytes = new FileInfo(sourcePath).Length;
            var restored = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(version.VersionId,
                Path.Combine(fixture.Root, "output-A", "restart-before.docx"))));
            Check(restored.RestoreResult?.VerifiedLogicalBytes == sourceBytes && Hash(restored.OutputPath!) == hash,
                "pre-restart recovery independently matches the captured source");
            var unknownRequest = Bind(FluxVaultIpcRequest.RestoreVersionPreview(version.VersionId) with { OutputPath = restored.OutputPath });
            var refusedPreview = await client.SendAsync(unknownRequest, deadline.Token);
            Check(!refusedPreview.Success && refusedPreview.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown && Hash(restored.OutputPath!) == hash,
                "admitted new-file preview fails on the existing destination without changing its bytes");
            var unknownReceipt = await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
                VaultId: id, OperationId: unknownRequest.OperationId), deadline.Token);
            Check(!unknownReceipt.Success && unknownReceipt.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown &&
                unknownReceipt.OperationId == unknownRequest.OperationId && unknownReceipt.VaultRevision == revision && unknownReceipt.OutputPath is null,
                "the admitted failed effect has its durable unknown receipt rather than a successful publication");
            var baseline = new Checkpoint(fixture.FixtureId, accepted.Status.Configuration, revision,
                saveRequest, saved, backupRequest, backup, version.VersionId, hash, sourceBytes, unknownRequest, unknownReceipt);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(baseline, Json);
            if (bytes.Length > CheckpointLimit) throw new InvalidDataException("Restart checkpoint exceeds its bound.");
            WindowsDatabaseProbeConfiguration.RejectReparseComponents(checkpointPath);
            await using (var output = new FileStream(checkpointPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(bytes, deadline.Token);
                output.Flush(flushToDisk: true);
            }
            checks.Add("creator retains bounded original requests and receipt envelopes before process replacement");
        }
        else
        {
            // The A-owned checkpoint is read only as A; it is never SYSTEM cleanup authority.
            WindowsDatabaseProbeConfiguration.RejectReparseComponents(checkpointPath);
            Checkpoint baseline;
            using (var input = new FileStream(checkpointPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length is <= 0 or > CheckpointLimit) throw new InvalidDataException("Restart checkpoint size is invalid.");
                baseline = JsonSerializer.Deserialize<Checkpoint>(input, Json) ?? throw new InvalidDataException("Restart checkpoint is missing.");
            }
            ValidateCheckpoint(baseline, fixture, id);
            Check(revision == baseline.Revision && Same(status.Status!.Configuration, baseline.Configuration),
                "process replacement retains exact revision and complete configuration");
            var before = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
            var originalHash = Hash(sourcePath);
            Check(originalHash == baseline.SourceHash, "pre-replay source still matches the checkpoint");
            await File.WriteAllTextAsync(sourcePath, "changed native source after service process replacement " + fixture.FixtureId, deadline.Token);
            var changedHash = Hash(sourcePath);
            Check(changedHash != baseline.SourceHash, "independent source hash differs before receipt replay");
            var formerlyOccupied = Path.Combine(fixture.Root, "output-A", "restart-before.docx");
            Check(Hash(formerlyOccupied) == baseline.SourceHash,
                "refused pre-restart preview destination retains its original independent hash");
            File.Delete(formerlyOccupied); // Make duplicate execution observably possible; only native A removes its own output.
            var stillUnknown = await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
                VaultId: id, OperationId: baseline.UnknownRequest.OperationId), deadline.Token);
            Check(Same(stillUnknown, baseline.UnknownReceipt) && stillUnknown.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown && !stillUnknown.Success,
                "original admitted unknown receipt survives actual process replacement without invented success");
            var unknownReplay = await client.SendAsync(baseline.UnknownRequest, deadline.Token);
            Check(Same(unknownReplay, baseline.UnknownReceipt) && !File.Exists(formerlyOccupied),
                "unknown request replay cannot recreate output even after the original destination obstruction is removed");
            foreach (var pair in new[] { (baseline.SaveRequest, baseline.SaveResponse), (baseline.BackupRequest, baseline.BackupResponse) })
            {
                var receipt = await Send(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
                    VaultId: id, OperationId: pair.Item1.OperationId));
                Check(Same(receipt, pair.Item2), "original complete receipt envelope survives process replacement: " + pair.Item1.Command);
                var replay = await Send(pair.Item1);
                Check(Same(replay, pair.Item2), "exact request replay returns the original envelope: " + pair.Item1.Command);
            }
            var unchanged = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
            Check(Same(before.Versions, unchanged.Versions), "save and backup replay do not mutate retained history despite changed source");
            var current = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
            Check(current.VaultRevision == baseline.Revision && Same(current.Status!.Configuration, baseline.Configuration),
                "replay cannot advance or replace the accepted configuration");
            var recovered = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(baseline.VersionId,
                Path.Combine(fixture.Root, "output-A", "restart-original.docx"))));
            Check(recovered.RestoreResult?.VerifiedLogicalBytes == baseline.SourceBytes && Hash(recovered.OutputPath!) == baseline.SourceHash,
                "post-restart original recovery matches retained SHA rather than the changed live source");
            var freshBackup = await Send(Bind(FluxVaultIpcRequest.RunBackupNow()));
            Check(freshBackup.Backup is { Success: true }, "fresh authorised backup succeeds after process replacement");
            var fresh = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
            var changed = LatestFile(fresh.Versions!, sourcePath);
            Check(changed.VersionId != baseline.VersionId, "fresh backup records a new version of the changed source");
            var freshRecovery = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(changed.VersionId,
                Path.Combine(fixture.Root, "output-A", "restart-changed.docx"))));
            Check(freshRecovery.RestoreResult?.VerifiedLogicalBytes == new FileInfo(sourcePath).Length && Hash(freshRecovery.OutputPath!) == changedHash,
                "fresh post-restart recovery independently matches the new source hash");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Actor = actor, Phase = phase, WindowsSid = identity.User!.Value,
            Passed = checks.Count, Checks = checks, NativeRestartVerified = true,
            UnknownOutcomePreservedWithoutReplay = phase == "after" }));
        return 0;

        FluxVaultIpcRequest Bind(FluxVaultIpcRequest request) => request with { VaultId = id,
            ExpectedVaultRevision = PostgreSqlMutation(request.Command) ? revision : null,
            OperationId = PostgreSqlMutation(request.Command) ? Guid.NewGuid() : null };
        async Task<FluxVaultIpcResponse> Send(FluxVaultIpcRequest request)
        {
            var response = await client.SendAsync(request, deadline.Token);
            if (!response.Success) throw new InvalidOperationException("Restart command failed: " + request.Command + ": " + response.ErrorMessage);
            return response;
        }
        void Check(bool result, string name)
        { if (!result) throw new InvalidOperationException("Native restart contract failed: " + name); checks.Add(name); }
    }

    private static async Task<int> RunServerAsync(WindowsDatabaseProbeConfiguration fixture, CancellationTokenSource deadline)
    {
        var bootstrap = Path.Combine(fixture.Root, "catalogue", "single", "state", "installation.json");
        using var installation = WindowsVaultInstallation.Open(bootstrap);
        var binding = installation.Configuration.Binding;
        var endpoint = installation.Configuration.Endpoint;
        if (binding.Id.Value != Guid.ParseExact(fixture.FixtureId, "N") || endpoint.InstanceId != binding.Id.Value ||
            endpoint.Host != "127.0.0.1" || endpoint.Port != fixture.Port || endpoint.Database != fixture.Database ||
            endpoint.ServiceRole != fixture.Role || installation.Configuration.CreatorSid != fixture.Actors["A"])
            throw new InvalidDataException("Restart bootstrap does not belong to this fixture.");
        // Ordinary product open verifies existing storage/catalogue; no provisioning or adoption.
        await using var service = await WindowsSingleVaultService.OpenAsync(bootstrap, deadline.Token);
        var server = new NamedPipeFluxVaultServer(service, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(
            "FluxVault.Tests." + fixture.FixtureId), new WindowsFluxVaultCallerContextProvider());
        var serving = server.RunAsync(deadline.Token);
        try
        {
            using var process = Process.GetCurrentProcess();
            var ready = Path.Combine(fixture.Root, "runtime", "restart-ready.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { ProcessId = process.Id,
                StartedUtc = process.StartTime.ToUniversalTime().ToString("O"), Executable = process.MainModule!.FileName,
                FixtureId = fixture.FixtureId, NativeRestartServer = true });
            var temporary = ready + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await output.WriteAsync(bytes, deadline.Token); output.Flush(flushToDisk: true); }
                File.Move(temporary, ready); // Publish only the closed, durable complete document; refuse replacement.
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            while (!File.Exists(Path.Combine(fixture.Root, "runtime", "restart-stop")))
            {
                if (serving.IsCompleted) { await serving; throw new IOException("Restart server stopped before its owned stop marker."); }
                await Task.Delay(50, deadline.Token);
            }
        }
        finally { await deadline.CancelAsync(); await serving; }
        Console.WriteLine(JsonSerializer.Serialize(new { Actor = "System", NativeRestartServer = true,
            ExistingBootstrapOpened = true, RepositoryId = binding.Id, RequestsJoined = true }));
        return 0;
    }

    private static bool PostgreSqlMutation(FluxVaultIpcCommand command) =>
        FluxVault.Core.Security.PostgreSqlVaultCatalogue.IsMutation(command);
    private static RepositoryVersionSummary LatestFile(IReadOnlyList<RepositoryVersionSummary> history, string path) =>
        history.Where(item => item.EntryKind == RepositoryEntryKind.File && item.SourcePath == path)
            .OrderByDescending(item => item.CapturedAtUtc).ThenByDescending(item => item.VersionId, StringComparer.Ordinal).First();
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool Same<T>(T first, T second) => JsonSerializer.Serialize(first, Json) == JsonSerializer.Serialize(second, Json);
    private static void ValidateCheckpoint(Checkpoint value, WindowsDatabaseProbeConfiguration fixture, VaultId id)
    {
        if (value.FixtureId != fixture.FixtureId || value.Revision <= 1 || value.Configuration is null ||
            value.SaveRequest is null || value.SaveResponse is null || value.BackupRequest is null || value.BackupResponse is null ||
            value.SaveRequest.Command != FluxVaultIpcCommand.SaveConfiguration || value.BackupRequest.Command != FluxVaultIpcCommand.RunBackupNow ||
            value.SaveRequest.VaultId != id || value.BackupRequest.VaultId != id ||
            value.SaveRequest.OperationId is null || value.SaveRequest.OperationId == Guid.Empty)
            throw new InvalidDataException("Restart checkpoint identity is invalid.");
        if (value.BackupRequest.OperationId is null || value.BackupRequest.OperationId == Guid.Empty ||
            value.SaveResponse.OperationId != value.SaveRequest.OperationId || value.BackupResponse.OperationId != value.BackupRequest.OperationId ||
            value.SaveResponse.VaultId != id || value.BackupResponse.VaultId != id || !value.SaveResponse.Success || !value.BackupResponse.Success ||
            value.SaveResponse.VaultRevision != value.Revision || value.BackupResponse.VaultRevision != value.Revision ||
            value.SaveRequest.ExpectedVaultRevision != value.Revision - 1 || value.BackupRequest.ExpectedVaultRevision != value.Revision ||
            !Same(value.SaveRequest.Configuration, value.Configuration) || string.IsNullOrWhiteSpace(value.VersionId) ||
            value.SourceBytes < 0 || value.SourceHash is not { Length: 64 } || value.SourceHash.Any(character => !Uri.IsHexDigit(character)) ||
            value.UnknownRequest is null || value.UnknownReceipt is null ||
            value.UnknownRequest.Command != FluxVaultIpcCommand.RestoreVersionPreview || value.UnknownRequest.VaultId != id ||
            value.UnknownRequest.OperationId is null || value.UnknownRequest.OperationId == Guid.Empty ||
            value.UnknownRequest.ExpectedVaultRevision != value.Revision || value.UnknownRequest.VersionId != value.VersionId ||
            value.UnknownRequest.OutputPath != Path.Combine(fixture.Root, "output-A", "restart-before.docx") ||
            value.UnknownReceipt.Success || value.UnknownReceipt.ErrorCode != FluxVaultIpcErrorCode.OutcomeUnknown ||
            value.UnknownReceipt.VaultId != id || value.UnknownReceipt.OperationId != value.UnknownRequest.OperationId ||
            value.UnknownReceipt.VaultRevision != value.Revision || value.UnknownReceipt.OutputPath is not null)
            throw new InvalidDataException("Restart checkpoint requests or receipt envelopes are invalid.");
    }
    private sealed record Checkpoint(string FixtureId, FluxVaultConfiguration Configuration, long Revision,
        FluxVaultIpcRequest SaveRequest, FluxVaultIpcResponse SaveResponse, FluxVaultIpcRequest BackupRequest,
        FluxVaultIpcResponse BackupResponse, string VersionId, string SourceHash, long SourceBytes,
        FluxVaultIpcRequest UnknownRequest, FluxVaultIpcResponse UnknownReceipt);
}
