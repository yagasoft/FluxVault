using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Ipc;
using FluxVault.Testing;
using FluxVault.Windows.Security;
using Npgsql;

namespace FluxVault.TestHost;

/// <summary>Owned external completion fault plus actual in-flight native service death.</summary>
internal static class WindowsInterruptedEffectProbe
{
    private const string Function = "fv_control.fixture_hold_completion";
    private const string Trigger = "fixture_hold_completion";
    private static readonly JsonSerializerOptions Json = new()
    { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static async Task<int> RunAsync(string configurationPath, string actor, string phase)
    {
        var fixture = WindowsDatabaseProbeConfiguration.Read(configurationPath);
        using var identity = WindowsIdentity.GetCurrent();
        if (!fixture.RunInterruptedEffectTests || !fixture.RunSingleVaultTests ||
            !fixture.Actors.TryGetValue(actor, out var expected) || identity.User?.Value != expected ||
            actor != "System" && (actor != "A" || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)))
            throw new UnauthorizedAccessException("Interrupted-effect proof requires its exact native actor.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(actor == "System" ? 60 : 20));
        if (actor == "System")
        {
            if (phase is not ("held-server" or "reopened-server")) throw new ArgumentException("Unknown interrupted server phase.");
            return await Server(fixture, phase, deadline);
        }
        if (phase is not ("before" or "after")) throw new ArgumentException("Unknown interrupted creator phase.");
        var id = new VaultId(Guid.ParseExact(fixture.FixtureId, "N"));
        var client = new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(
            "FluxVault.Tests." + fixture.FixtureId, fixture.Actors["System"]));
        var checks = new List<string>();
        var status = await Send(FluxVaultIpcRequest.GetStatus());
        Check(status.VaultId == id && status.VaultRevision is > 0 && status.Status is not null, "native creator receives the existing single-vault binding");
        var revision = status.VaultRevision!.Value;
        var output = Path.Combine(fixture.Root, "output-A", "interrupted-preview.docx");
        var checkpoint = Path.Combine(fixture.Root, "output-A", "interrupted-checkpoint.json");
        if (phase == "before")
        {
            var source = Path.Combine(fixture.Root, "output-A", "office", "document.docx");
            var history = await Send(FluxVaultIpcRequest.ListVersions() with { VaultId = id, ExpectedVaultRevision = revision });
            var version = history.Versions!.Where(item => item.EntryKind == RepositoryEntryKind.File && item.SourcePath == source)
                .OrderByDescending(item => item.CapturedAtUtc).ThenByDescending(item => item.VersionId, StringComparer.Ordinal).First();
            var expectedHash = HashFile(source);
            var bytes = new FileInfo(source).Length;
            Check(!File.Exists(output), "interrupted recovery begins with a new destination");
            var request = FluxVaultIpcRequest.RestoreVersionPreview(version.VersionId) with
            { VaultId = id, ExpectedVaultRevision = revision, OperationId = id.Value, OutputPath = output };
            var pending = client.SendAsync(request, deadline.Token);
            try
            {
                while (!File.Exists(output))
                {
                    if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Completion was not held before publication."); }
                    await Task.Delay(20, deadline.Token);
                }
                Check(HashFile(output) == expectedHash && new FileInfo(output).Length == bytes && !pending.IsCompleted,
                    "creator independently verifies published bytes while the original request is unacknowledged");
                var receipt = await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
                    VaultId: id, OperationId: id.Value), deadline.Token);
                Check(!receipt.Success && receipt.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown && receipt.VaultRevision == revision &&
                    receipt.VaultId == id && receipt.OperationId == id.Value && receipt.OutputPath is null && !pending.IsCompleted,
                    "the published effect still has its original durable admitted receipt");
                await Publish(checkpoint, new Checkpoint(fixture.FixtureId, request, receipt, expectedHash, bytes), deadline.Token);
                try { await pending; throw new InvalidOperationException("Interrupted command received a completion acknowledgement."); }
                catch (IOException) { checks.Add("owned process death disconnects and joins the unacknowledged creator request"); }
            }
            finally
            {
                // Join even a failed proof before actor teardown; do not abandon a client request.
                if (!pending.IsCompleted) await deadline.CancelAsync();
                try { await pending; } catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
            }
        }
        else
        {
            WindowsDatabaseProbeConfiguration.RejectReparseComponents(checkpoint);
            await using var input = new FileStream(checkpoint, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is 0 or > 65536) throw new InvalidDataException("Interrupted checkpoint exceeds its bound.");
            var saved = await JsonSerializer.DeserializeAsync<Checkpoint>(input, Json, deadline.Token)
                ?? throw new InvalidDataException("Missing interrupted checkpoint.");
            if (saved.FixtureId != fixture.FixtureId || saved.Request.Command != FluxVaultIpcCommand.RestoreVersionPreview ||
                saved.Request.VaultId != id || saved.Request.OperationId != id.Value || saved.Request.OutputPath != output ||
                saved.Request.ExpectedVaultRevision != revision || string.IsNullOrWhiteSpace(saved.Request.VersionId) || saved.SourceBytes < 0 ||
                saved.Hash.Length != 64 || saved.Hash.Any(character => !Uri.IsHexDigit(character)) || saved.Receipt.Success ||
                saved.Receipt.ErrorCode != FluxVaultIpcErrorCode.OutcomeUnknown || saved.Receipt.VaultId != id ||
                saved.Receipt.OperationId != id.Value || saved.Receipt.VaultRevision != revision || saved.Receipt.OutputPath is not null)
                throw new InvalidDataException("Interrupted checkpoint identity/envelope is invalid.");
            var lookup = new FluxVaultIpcRequest(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null, VaultId: id, OperationId: id.Value);
            Check(HashFile(output) == saved.Hash && new FileInfo(output).Length == saved.SourceBytes,
                "published bytes survive actual native process death and existing-installation reopen");
            Check(Same(await client.SendAsync(lookup, deadline.Token), saved.Receipt), "reopened status retains the exact unknown receipt");
            File.Delete(output); // Only A's fixed owned and independently verified destination.
            Check(!File.Exists(output), "removing only the owned destination makes repeated publication observable");
            Check(Same(await client.SendAsync(saved.Request, deadline.Token), saved.Receipt) && !File.Exists(output),
                "exact original-ID replay remains unknown and cannot recreate the now-eligible output");
            Check(Same(await client.SendAsync(lookup, deadline.Token), saved.Receipt), "replay leaves the durable unknown receipt unchanged");
            var fresh = await Send(saved.Request with { OperationId = Guid.NewGuid() });
            Check(fresh.OutputPath == output && fresh.RestoreResult?.VerifiedLogicalBytes == saved.SourceBytes && HashFile(output) == saved.Hash,
                "an explicitly new operation publishes independently verified recovery after the obstruction is removed");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Actor = actor, Phase = phase, NativeInterruptedEffect = true,
            Passed = checks.Count, Checks = checks, OriginalOperationId = id.Value, ExistingBinding = id }));
        return 0;

        async Task<FluxVaultIpcResponse> Send(FluxVaultIpcRequest request)
        {
            var response = await client.SendAsync(request, deadline.Token);
            if (!response.Success) throw new InvalidOperationException("Interrupted proof command failed: " + request.Command + ": " + response.ErrorMessage);
            return response;
        }
        void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); checks.Add(message); }
    }

    private static async Task<int> Server(WindowsDatabaseProbeConfiguration fixture, string phase, CancellationTokenSource deadline)
    {
        var bootstrap = Path.Combine(fixture.Root, "catalogue", "single", "state", "installation.json");
        using var installation = WindowsVaultInstallation.Open(bootstrap);
        var endpoint = installation.Configuration.Endpoint;
        var operation = Guid.ParseExact(fixture.FixtureId, "N");
        if (installation.Configuration.Binding.Id.Value != operation || endpoint.InstanceId != operation ||
            endpoint.Host != "127.0.0.1" || endpoint.Port != fixture.Port || endpoint.Database != fixture.Database ||
            endpoint.ServiceRole != fixture.Role || installation.Configuration.CreatorSid != fixture.Actors["A"])
            throw new InvalidDataException("Interrupted bootstrap does not belong to this fixture.");
        var builder = WindowsDatabaseProbe.CreateConnectionSettings(fixture, "127.0.0.1", "System", operation);
        await using var source = WindowsDatabaseProbe.CreateDataSource(builder);
        await using var observer = await source.OpenConnectionAsync(deadline.Token);
        await using (var verify = new NpgsqlCommand("SELECT current_setting('fluxvault.test_instance'),current_database(),current_user,inet_server_port()", observer))
        await using (var reader = await verify.ExecuteReaderAsync(deadline.Token))
        {
            if (!await reader.ReadAsync(deadline.Token) || reader.GetString(0) != fixture.FixtureId || reader.GetString(1) != fixture.Database ||
                reader.GetString(2) != fixture.Role || reader.GetInt32(3) != fixture.Port) throw new InvalidDataException("Observer endpoint changed.");
        }
        if (phase == "held-server")
        {
            var intent = new GateJournal(fixture.FixtureId, fixture.Port, fixture.Database, operation, Function, Trigger, "Intent", null, null);
            await Publish(Path.Combine(fixture.Root, "runtime", "effect-gate-intent.json"), intent, deadline.Token);
            await using (var create = new NpgsqlCommand($"""
                CREATE FUNCTION {Function}() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $gate$
                BEGIN
                    IF OLD.operation_id='{operation:D}'::uuid AND OLD.state=0 AND NEW.state=1 THEN
                        PERFORM pg_sleep(10);
                        RAISE EXCEPTION 'Owned fixture completion fault' USING ERRCODE='57014';
                    END IF;
                    RETURN NEW;
                END $gate$;
                REVOKE ALL ON FUNCTION {Function}() FROM PUBLIC;
                CREATE TRIGGER {Trigger} BEFORE UPDATE ON fv_control.operations FOR EACH ROW EXECUTE FUNCTION {Function}();
                """, observer)) await create.ExecuteNonQueryAsync(deadline.Token);
            var hashes = await Definitions(observer, deadline.Token);
            await Publish(Path.Combine(fixture.Root, "runtime", "effect-gate-created.json"),
                intent with { State = "Created", FunctionHash = hashes.Function, TriggerHash = hashes.Trigger }, deadline.Token);
        }
        else
        {
            var gate = Read<GateJournal>(fixture, "effect-gate-created.json");
            var held = Read<HeldCompletion>(fixture, "effect-held.json");
            if (gate.FixtureId != fixture.FixtureId || gate.Port != fixture.Port || gate.Database != fixture.Database || gate.Operation != operation ||
                gate.Function != Function || gate.Trigger != Trigger || gate.State != "Created" ||
                held.FixtureId != fixture.FixtureId || held.Operation != operation || held.ActorSid != fixture.Actors["A"] || held.Revision <= 0 ||
                held.BackendPid <= 0 || held.BackendStartedUtc.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("Completion gate ownership changed.");
            // The killed service's UPDATE transaction must end before reopening/releasing the gate.
            for (;;)
            {
                await using var activity = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid=@pid AND backend_start=@started)", observer);
                activity.Parameters.AddWithValue("pid", held.BackendPid); activity.Parameters.AddWithValue("started", held.BackendStartedUtc);
                if (await activity.ExecuteScalarAsync(deadline.Token) is false) break;
                await Task.Delay(50, deadline.Token);
            }
            await using (var admitted = new NpgsqlCommand("SELECT count(*) FROM fv_control.operations WHERE operation_id=@op AND vault_id=@op AND actor_sid=@actor AND command=@command AND revision=@revision AND state=0 AND response IS NULL", observer))
            {
                admitted.Parameters.AddWithValue("op", operation); admitted.Parameters.AddWithValue("actor", fixture.Actors["A"]);
                admitted.Parameters.AddWithValue("command", (int)FluxVaultIpcCommand.RestoreVersionPreview); admitted.Parameters.AddWithValue("revision", held.Revision);
                if (await admitted.ExecuteScalarAsync(deadline.Token) is not long count || count != 1) throw new InvalidDataException("Interrupted completion committed or admission changed.");
            }
            var definitions = await Definitions(observer, deadline.Token);
            if (definitions.Function != gate.FunctionHash || definitions.Trigger != gate.TriggerHash) throw new InvalidDataException("Owned completion objects changed.");
            await using (var drop = new NpgsqlCommand($"DROP TRIGGER {Trigger} ON fv_control.operations; DROP FUNCTION {Function}();", observer))
                await drop.ExecuteNonQueryAsync(deadline.Token);
            await using (var absence = new NpgsqlCommand($"SELECT to_regprocedure('{Function}()') IS NULL AND NOT EXISTS(SELECT FROM pg_trigger WHERE tgrelid='fv_control.operations'::regclass AND tgname='{Trigger}')", observer))
                if (await absence.ExecuteScalarAsync(deadline.Token) is not true) throw new InvalidDataException("Completion gate remains.");
            await Publish(Path.Combine(fixture.Root, "runtime", "effect-gate-removed.json"), gate with { State = "Removed" }, deadline.Token);
        }
        await using var service = await WindowsSingleVaultService.OpenAsync(bootstrap, deadline.Token);
        var server = new NamedPipeFluxVaultServer(service, WindowsFluxVaultPipeServerFactory.ForPrivateFixture("FluxVault.Tests." + fixture.FixtureId),
            new WindowsFluxVaultCallerContextProvider());
        var serving = server.RunAsync(deadline.Token);
        try
        {
            using var process = Process.GetCurrentProcess();
            await Publish(Path.Combine(fixture.Root, "runtime", "effect-" + phase + "-ready.json"), new
            { FixtureId = fixture.FixtureId, Phase = phase, NativeInterruptedServer = true, ProcessId = process.Id,
                StartedUtc = process.StartTime.ToUniversalTime().ToString("O"), Executable = process.MainModule!.FileName }, deadline.Token);
            if (phase == "held-server")
            {
                HeldCompletion? held;
                do { held = await ObserveHeld(observer, fixture, operation, deadline.Token); if (held is null) await Task.Delay(20, deadline.Token); } while (held is null);
                await Publish(Path.Combine(fixture.Root, "runtime", "effect-held.json"), held, deadline.Token);
            }
            while (!File.Exists(Path.Combine(fixture.Root, "runtime", "effect-stop")))
            {
                if (serving.IsCompleted) { await serving; throw new IOException("Interrupted server stopped before its owned stop/death action."); }
                await Task.Delay(20, deadline.Token);
            }
        }
        finally { await deadline.CancelAsync(); await serving; }
        Console.WriteLine(JsonSerializer.Serialize(new { Actor = "System", Phase = phase, NativeInterruptedServer = true,
            ExistingBootstrapOpened = true, CompletionGateRemoved = phase == "reopened-server", CompletionBackendAbsent = phase == "reopened-server", RequestsJoined = true }));
        return 0;
    }

    private static async Task<HeldCompletion?> ObserveHeld(NpgsqlConnection observer, WindowsDatabaseProbeConfiguration fixture, Guid operation, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT o.actor_sid,o.revision,a.pid,a.backend_start,a.query_start,clock_timestamp()
            FROM fv_control.operations o CROSS JOIN pg_stat_activity a
            WHERE o.operation_id=@op AND o.vault_id=@op AND o.state=0 AND o.response IS NULL AND o.command=@command
                AND a.datname=@database AND a.usename=@role AND a.wait_event_type='Timeout' AND a.wait_event='PgSleep'
                AND a.query LIKE 'UPDATE fv_control.operations SET state=1,response=%'
            """, observer);
        command.Parameters.AddWithValue("op", operation); command.Parameters.AddWithValue("command", (int)FluxVaultIpcCommand.RestoreVersionPreview);
        command.Parameters.AddWithValue("database", fixture.Database); command.Parameters.AddWithValue("role", fixture.Role);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var actor = reader.GetString(0); var revision = reader.GetInt64(1); var pid = reader.GetInt32(2);
        var started = reader.GetDateTime(3); var queryStarted = reader.GetDateTime(4); var observed = reader.GetDateTime(5);
        if (actor != fixture.Actors["A"] || revision <= 0 || observed - queryStarted > TimeSpan.FromSeconds(4) || await reader.ReadAsync(token))
            throw new InvalidDataException("Held completion is ambiguous or its termination window was missed.");
        return new(fixture.FixtureId, operation, actor, revision, pid, started, queryStarted, observed, queryStarted.AddSeconds(8));
    }

    private static async Task<(string Function, string Trigger)> Definitions(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT pg_get_functiondef('{Function}()'::regprocedure),pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid='fv_control.operations'::regclass AND tgname='{Trigger}'", connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("Completion gate is missing.");
        var result = (HashText(reader.GetString(0)), HashText(reader.GetString(1)));
        if (await reader.ReadAsync(token)) throw new InvalidDataException("Completion gate is ambiguous.");
        return result;
    }
    private static T Read<T>(WindowsDatabaseProbeConfiguration fixture, string name)
    {
        var path = Path.Combine(fixture.Root, "runtime", name);
        WindowsDatabaseProbeConfiguration.RejectReparseComponents(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is 0 or > 16384) throw new InvalidDataException("Protected completion document exceeds its bound.");
        return JsonSerializer.Deserialize<T>(input, Json) ?? throw new InvalidDataException("Missing completion document.");
    }
    private static async Task Publish<T>(string path, T value, CancellationToken token)
    {
        WindowsDatabaseProbeConfiguration.RejectReparseComponents(path);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > 65536) throw new InvalidDataException("Completion document exceeds its bound.");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(bytes, token); output.Flush(flushToDisk: true); }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string HashFile(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json);
    private sealed record Checkpoint(string FixtureId, FluxVaultIpcRequest Request, FluxVaultIpcResponse Receipt, string Hash, long SourceBytes);
    private sealed record GateJournal(string FixtureId, int Port, string Database, Guid Operation, string Function, string Trigger,
        string State, string? FunctionHash, string? TriggerHash);
    private sealed record HeldCompletion(string FixtureId, Guid Operation, string ActorSid, long Revision, int BackendPid,
        DateTime BackendStartedUtc, DateTime QueryStartedUtc, DateTime ObservedUtc, DateTime LatestKillUtc);
}
