using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Chunking;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Testing;
using FluxVault.Windows.Security;
using Npgsql;
using FluxVault.Service;

namespace FluxVault.TestHost;

/// <summary>Real current WTS logon + actual service composition, confined to the existing owned SSPI fixture.</summary>
internal static class WindowsAutomaticRuntimeProbe
{
    internal static async Task<object> RunAsync(NpgsqlDataSource dataSource, WindowsDatabaseProbeConfiguration fixture)
    {
        using var processIdentity = WindowsIdentity.GetCurrent();
        if (processIdentity.User?.Value != "S-1-5-18" || !fixture.RunAutomaticTests) throw new UnauthorizedAccessException();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(
            fixture.AutomaticSessionTransition == "None" ? 240 : fixture.SessionTransitionTimeoutSeconds + 90));
        var ct = deadline.Token;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using (var verify = new NpgsqlCommand("SELECT current_setting('fluxvault.test_instance'),current_database(),current_user", connection))
        await using (var reader = await verify.ExecuteReaderAsync(ct))
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != fixture.FixtureId || reader.GetString(1) != fixture.Database || reader.GetString(2) != fixture.Role)
                throw new UnauthorizedAccessException("Automatic runtime proof does not own this endpoint.");
        var host = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Host!;
        await using var relay = fixture.RunStartupRecoveryTests ? new OwnedLoopbackRelay(host, fixture.Port) : null;
        var servicePort = relay?.Port ?? fixture.Port;
        var checks = new List<string>();
        var acquisition = new List<string>();
        await using var creator = await new WindowsAutomaticProtectionSessionProvider(acquisition.Add).AcquireAsync(fixture.AutomaticCreatorSid!, ct)
            ?? throw new InvalidOperationException("No ordinary current creator WTS token could be established. " + string.Join(" ", acquisition));
        await creator.VerifyAsync(ct);
        Check(creator.Caller.UserSid == fixture.AutomaticCreatorSid && !creator.Caller.IsElevated && creator.SessionId > 0,
            "real WTS creator SID/session/AuthenticationId and ordinary effective token are verified");
        var parent = Path.Combine(fixture.Root, "automatic-" + Guid.NewGuid().ToString("N"));
        PrivateDirectory(parent, traversalSid: creator.Caller.UserSid);
        var stateRoot = Path.Combine(parent, "state"); PrivateDirectory(stateRoot);
        var sourceRoot = Path.Combine(parent, "working"); PrivateDirectory(sourceRoot, creator.Caller.UserSid);
        var mirrorRoot=Path.Combine(parent,"mirror");PrivateDirectory(mirrorRoot);
        var id = VaultId.New();
        var metadataConfiguration = MetadataStoreConfiguration.CreateDefault(stateRoot) with
        { Host = host, Port = servicePort, DatabaseName = fixture.Database, Username = fixture.Role };
        var binding = new VaultBinding(id, Path.Combine(parent, "repository"), stateRoot, metadataConfiguration);
        var endpoint = new VaultCatalogueEndpoint(Guid.ParseExact(fixture.FixtureId, "N"), host, servicePort, fixture.Database, fixture.Role);
        var configuration = FluxVaultConfiguration.CreateDefault(stateRoot) with
        {
            RepositoryPath = binding.RepositoryPath, MetadataStore = metadataConfiguration,
            MirrorSet=MirrorSetConfiguration.FromLegacyPath(mirrorRoot),
            RetentionPolicy = RetentionPolicy.CreateDefault() with { IsEnabled = false },
            WatchedFolders = [new("automatic", sourceRoot, true, ["*.txt"], [], CompressionPreference.Off, ResourceProfile.Fast, true)]
        };
        var bootstrap = Path.Combine(stateRoot, "installation.json");
        try
        {
            await using (var catalogue = new PostgreSqlVaultCatalogue(endpoint))
            {
                await catalogue.ProvisionAsync(ct);
                await catalogue.InitializeAsync(creator.Caller, binding, "FluxVault automatic staging fixture", configuration, ct);
                await using var metadata = new PostgreSqlRepositoryMetadataStore(binding);
                await metadata.ProvisionVaultAsync(ct);
                var repository = new FileSystemChunkRepository(binding, new FastCdcChunker(new()), new(), new(), configuration.MirrorSet, metadata);
                await repository.ProvisionVaultStorageAsync(ct);
                await File.WriteAllBytesAsync(bootstrap, JsonSerializer.SerializeToUtf8Bytes(new FluxVaultInstallation(endpoint, binding, creator.Caller.UserSid)), ct);
            }
            var pipe = "FluxVault.Tests." + Guid.NewGuid().ToString("N");
            await AutomaticRollbackSqlProbe.VerifyUninitialisedAsync(connection,binding,creator.Caller.UserSid,checks,ct);
            var client = new CreatorClient(creator, pipe);
            var file = Path.Combine(sourceRoot, "saved.txt");
            var expected = Encoding.UTF8.GetBytes("automatic creator save; no manual backup; " + id);
            string firstVersion;
            var initiallyOffline=mirrorRoot+"-default-off";Directory.Move(mirrorRoot,initiallyOffline);
            await using (var service = fixture.RunStartupRecoveryTests ? await OpenAfterDependencyDelayAsync(pipe) : await WindowsSingleVaultService.OpenAsync(bootstrap, ct))
            {
                using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var server = new NamedPipeFluxVaultServer(service, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
                var running = SupervisedRuntime.RunAsync(server.RunAsync, service.RunAutomaticProtectionAsync, run.Token);
                try
                {
                    var initial = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), ct);
                    Check(initial.Success && initial.Status?.AutomaticProtection?.Policy.Enabled == false, "automatic extension is default off in actual service composition");
                    Check(initial.Status?.MirrorWarnings?.Any(warning=>warning.Contains("unavailable",StringComparison.OrdinalIgnoreCase) &&
                        warning.Contains(configuration.MirrorSet.EnabledNodes.Single().Label,StringComparison.Ordinal))==true,
                        "default-off startup and status remain available and explain an offline configured mirror before any mirror cursor exists");
                    Directory.Move(initiallyOffline,mirrorRoot);
                    var options = new OptionsViewModel(client);
                    await options.InitialiseAsync(ct); options.AutomaticProtectionEnabled = true;
                    await options.ApplyAutomaticProtectionAsync(ct);
                    Check(!options.HasUnconfirmedSave, "actual Options view-model enable has a confirmed catalogue receipt");
                    var enabled = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), ct);
                    Check(enabled.Status?.AutomaticProtection?.Policy.Enabled == true && !enabled.Status.AutomaticProtection.RequiresCreatorApproval,
                        "saved creator preference is enabled and approved through the actual pipe");
                    await creator.Caller.RunAsCallerAsync(async () => { await File.WriteAllBytesAsync(file, expected, ct); return true; });
                    Check(new WindowsProtectionSourceAccess(creator.Caller).EnumerateDirectory(sourceRoot, sourceRoot, ct).Any(entry => entry.Path == file),
                        "ordinary creator can enumerate the owned source through validated native handles");
                    firstVersion = await WaitForAutomaticVersionAsync(client, id, ct);
                    Check(firstVersion.Length == 32, "ordinary creator save reaches an automatic version without RunBackupNow");
                }
                finally { await run.CancelAsync(); await running; }
            }
            Check(true, "automatic, authority-observer and accepted pipe requests join before service disposal");
            await using (var service = await WindowsSingleVaultService.OpenAsync(bootstrap, ct))
            {
                using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var server = new NamedPipeFluxVaultServer(service, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
                var running = SupervisedRuntime.RunAsync(server.RunAsync, service.RunAutomaticProtectionAsync, run.Token);
                try
                {
                    var saved = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), ct);
                    Check(saved.Status?.AutomaticProtection?.Policy.Enabled == true && saved.Status.AutomaticProtection.LastSuccessfulVersionId == firstVersion,
                        "restart retains automatic preference and acknowledged version identity");
                    var history = await client.SendAsync(FluxVaultIpcRequest.ListVersions() with { VaultId = id }, ct);
                    Check(history.Success && history.Versions!.Any(item => item.VersionId == firstVersion && item.SourcePath == file),
                        "automatic version is findable through authenticated history after restart");
                    var output = Path.Combine(sourceRoot, "recovery.bin");
                    var recovered = await client.SendAsync(FluxVaultIpcRequest.RestoreVersion(firstVersion, output) with
                    { VaultId = id, ExpectedVaultRevision = saved.VaultRevision, OperationId = Guid.NewGuid() }, ct);
                    var actual = await creator.Caller.RunAsCallerAsync(() => File.ReadAllBytesAsync(output, ct));
                    Check(recovered.Success && recovered.RestoreResult?.VerifiedLogicalBytes == expected.Length &&
                        SHA256.HashData(actual).SequenceEqual(SHA256.HashData(expected)), "restarted service recovery matches independently hashed original bytes");
                }
                finally { await run.CancelAsync(); await running; }
            }
            if (fixture.RunStartupRecoveryTests)
            {
                await VerifyStorageWriteFailureAsync(bootstrap, pipe, client, creator, binding, sourceRoot, ct, Check);
                return new { Passed = checks.Count, Checks = checks, NativeWts = true, RestartRecoveryVerified = true,
                    StartupRecoveryVerified = true, StorageFailureRecoveryVerified = true, SameEndpointAndBinding = true, StoreHost = host };
            }
            if (fixture.AutomaticSessionTransition != "None")
            {
                var nativeSignOut = await VerifyNativeSessionTransitionAsync(bootstrap, pipe, client, creator,
                    connection, endpoint, binding, sourceRoot, fixture, ct, Check);
                return new { Passed = checks.Count, Checks = checks, NativeWts = true, RestartRecoveryVerified = true,
                    NativeSignOutVerified = nativeSignOut, AbortRehearsal = !nativeSignOut, StoreHost = host };
            }
            await VerifySourceQuiescenceAsync(bootstrap, pipe, client, id, ct, Check);
            await VerifyCapturePauseAsync(bootstrap,pipe,client,creator,id,sourceRoot,ct,Check);
            await VerifyOwnerInterruptionAsync(bootstrap, pipe, client, creator, connection, endpoint, binding, file, ct, Check);
            await VerifyRetainedDeletionAsync(bootstrap,pipe,client,creator,id,sourceRoot,ct,Check);
            await VerifyUnavailableRootAsync(bootstrap,pipe,client,creator,id,sourceRoot,ct,Check);
            await VerifyManualFallbackAsync(bootstrap,pipe,client,creator,connection,id,firstVersion,expected,sourceRoot,ct,Check);
            await VerifyUncertainPublicationAsync(bootstrap,pipe,client,creator,id,binding,firstVersion,expected,file,ct,Check);
            await VerifyMirrorRetryAsync(bootstrap,pipe,client,creator,dataSource,binding,sourceRoot,mirrorRoot,ct,Check);
            await VerifyRollbackAsync(bootstrap,pipe,client,creator,connection,endpoint,binding,sourceRoot,firstVersion,expected,ct,Check);
            return new { Passed = checks.Count, Checks = checks, NativeWts = true, CreatorSid = creator.Caller.UserSid,
                creator.SessionId, creator.AuthenticationId, TokenIsElevated = creator.Caller.IsElevated,
                RestartRecoveryVerified = true, NoManualBackup = true, StoreHost = host, RecoverySha256 = Convert.ToHexString(SHA256.HashData(expected)) };
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS fv_control CASCADE; DROP SCHEMA IF EXISTS \"{binding.MetadataNamespace}\" CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Automatic native contract failed: " + name); checks.Add(name); }
        async Task<WindowsSingleVaultService> OpenAfterDependencyDelayAsync(string delayedPipe)
        {
            var bootstrapHash = SHA256.HashData(await File.ReadAllBytesAsync(bootstrap, ct));
            await relay!.StopAsync();
            var failures = 0;
            var result = await ServiceDependencyStartup.OpenAsync(token => WindowsSingleVaultService.OpenAsync(bootstrap, token),
                exception =>
                {
                    Check(exception is NpgsqlException, "actual private PostgreSQL transport unavailable before service admission, attempt " + ++failures);
                    using var transport = new System.IO.Pipes.NamedPipeClientStream(".", delayedPipe, System.IO.Pipes.PipeDirection.InOut);
                    try { transport.Connect(20); throw new InvalidOperationException("Service pipe admitted before dependency verification."); }
                    catch (TimeoutException) { Check(true, "no service pipe is admitted during dependency wait, attempt " + failures); }
                    if (failures == 4) relay.Start();
                }, new() { RetryDelay = TimeSpan.FromMilliseconds(100) }, ct);
            try
            {
                Check(failures == 4, "same service initialisation survives more than the configured SCM restart attempts");
                Check(SHA256.HashData(await File.ReadAllBytesAsync(bootstrap, ct)).SequenceEqual(bootstrapHash),
                    "protected bootstrap and binding remain unchanged while the same private endpoint recovers");
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }
    }

    private static async Task<bool> VerifyNativeSessionTransitionAsync(string bootstrap, string pipe, CreatorClient client,
        AutomaticProtectionSession creator, NpgsqlConnection connection, VaultCatalogueEndpoint endpoint, VaultBinding binding,
        string sourceRoot, WindowsDatabaseProbeConfiguration fixture, CancellationToken ct, Action<bool, string> check)
    {
        var abort = fixture.AutomaticSessionTransition == "AbortRehearsal";
        var creatorSid = creator.Caller.UserSid;
        var oldAuthenticationId = creator.AuthenticationId;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeReaderOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeReaderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? probeReader = null;
        Task? retiringProbe = null;
        Task<IAsyncDisposable>? suspending = null;
        IAsyncDisposable? suspended = null;
        var events = Path.Combine(fixture.Root, "runtime", "automatic-session-transition.jsonl");
        var abortMarker = Path.Combine(fixture.Root, "runtime", "native-session-abort");
        var file = Path.Combine(sourceRoot, "native-session-held.txt");
        var bytes = Encoding.UTF8.GetBytes("one save retained across a real native session boundary; " + fixture.FixtureId);
        await using var service = await WindowsSingleVaultService.OpenAsync(bootstrap, ct);
        service.AutomaticRuntimeForFixture.CaptureOpened = async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested();
        };
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var server = new NamedPipeFluxVaultServer(service, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
        var running = SupervisedRuntime.RunAsync(server.RunAsync, service.RunAutomaticProtectionAsync, run.Token);
        try
        {
            await WaitForStateAsync(client, AutomaticProtectionState.WaitingForChanges, ct);
            await creator.Caller.RunAsCallerAsync(async () => { await File.WriteAllBytesAsync(file, bytes, ct); return true; });
            probeReader = creator.Caller.RunAsCallerAsync(async () =>
            {
                await using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                probeReaderOpened.TrySetResult(); await probeReaderRelease.Task;
                return reader.Length == bytes.Length;
            });
            await probeReaderOpened.Task.WaitAsync(ct);
            await entered.Task.WaitAsync(ct);
            await using var claim = new NpgsqlCommand("SELECT reserved_version_id FROM fv_control.automatic_work WHERE source_path=@path AND reserved_version_id IS NOT NULL", connection);
            claim.Parameters.AddWithValue("path", file);
            var reserved = await claim.ExecuteScalarAsync(ct) as string ?? throw new InvalidOperationException("Held native capture has no durable reserved identity.");
            if (File.Exists(abortMarker)) throw new OperationCanceledException("Owned native observer abort requested.");
            Record("Ready", new { creator.SessionId, AuthenticationId = oldAuthenticationId, ReservedVersionId = reserved, AbortRehearsal = abort });
            if (abort)
            {
                // Rehearses only fixture cancellation, never labels it a WTS transition.
                await Task.Delay(1000, ct);
                await run.CancelAsync();
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                check(!running.IsCompleted, "abort cancellation waits for the held runtime source reader");
            }
            else
            {
                using var observation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                observation.CancelAfter(TimeSpan.FromSeconds(fixture.SessionTransitionTimeoutSeconds));
                while (true)
                {
                    if (File.Exists(abortMarker)) throw new OperationCanceledException("Owned native observer abort requested.");
                    try { await creator.VerifyAsync(observation.Token); }
                    catch (UnauthorizedAccessException) { break; }
                    await Task.Delay(100, observation.Token);
                }
                Record("NativeIdentityLost", new { creator.SessionId, AuthenticationId = oldAuthenticationId });
                // Do not cancel the fixture or source gate until the real runtime observer cancels capture.
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                check(!run.IsCancellationRequested && !running.IsCompleted,
                    "real WTS identity loss cancels the runtime-held capture before fixture cancellation and reader release");
                var staleExecuted = false;
                try
                {
                    await creator.Caller.RunAsCallerAsync(() => { staleExecuted = true; return Task.FromResult(true); });
                    throw new InvalidOperationException("Stale creator authority executed after real sign-out.");
                }
                catch (UnauthorizedAccessException) { }
                check(!staleExecuted, "the retained old creator token refuses source access after actual sign-out before fixture disposal");
                retiringProbe = creator.DisposeAsync().AsTask();
                check(!retiringProbe.IsCompleted && !probeReader.IsCompleted,
                    "the retained probe token cannot retire before its own held native reader joins");
                suspending = service.AutomaticRuntimeForFixture.SuspendSourceForFixtureAsync(ct).AsTask();
                check(!suspending.IsCompleted, "source suspension cannot acknowledge while the old runtime reader is held");
            }
            service.AutomaticRuntimeForFixture.CaptureOpened = null;
            release.TrySetResult();
            probeReaderRelease.TrySetResult();
            check(await probeReader, "the retained probe's native source reader joins");
            if (retiringProbe is not null) await retiringProbe;
            if (abort) await running;
            else suspended = await suspending!;
            await using var catalogue = new PostgreSqlVaultCatalogue(endpoint);
            var pending = await catalogue.ReadAutomaticStateAsync(binding, ct);
            await using var metadata = new PostgreSqlRepositoryMetadataStore(binding);
            var versions = await metadata.ListManifestsAsync(ct);
            check(pending.PendingCount > 0 && pending.UncertainCount > 0 && versions.All(item => item.VersionId != reserved && item.SourcePath != file),
                "SYSTEM inspection proves the held reserved result was not published and durable pending work remains");
            Record(abort ? "AbortJoined" : "OldReadersJoined", new { ReservedVersionId = reserved, pending.PendingCount, pending.UncertainCount });
            if (abort) return false;
            await suspended!.DisposeAsync(); suspended = null;
            var provider = new WindowsAutomaticProtectionSessionProvider();
            AutomaticProtectionSession? replacement = null;
            while (replacement is null)
            {
                if (File.Exists(abortMarker)) throw new OperationCanceledException("Owned native observer abort requested.");
                replacement = await provider.AcquireAsync(creatorSid, ct);
                if (replacement is null) await Task.Delay(200, ct);
            }
            await using (replacement)
            {
                check(replacement.AuthenticationId != oldAuthenticationId,
                    "native creator re-entry has a fresh AuthenticationId after the old source generation joined");
                var freshClient = new CreatorClient(replacement, pipe);
                var version = await WaitForHistoryAsync(freshClient, binding.Id, file, false, ct);
                var output = Path.Combine(sourceRoot, "native-session-recovered.bin");
                var restored = await MutateAsync(freshClient, binding.Id, FluxVaultIpcRequest.RestoreVersion(version.VersionId, output), ct);
                var actual = await replacement.Caller.RunAsCallerAsync(() => File.ReadAllBytesAsync(output, ct));
                check(restored.Success && restored.RestoreResult?.VerifiedLogicalBytes == bytes.Length && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(bytes)),
                    "native re-entry captures the retained save without another write and independently verifies recovery");
                Record("NativeReentryRecovered", new { replacement.SessionId, replacement.AuthenticationId, version.VersionId, Sha256 = Convert.ToHexString(SHA256.HashData(actual)) });
            }
            return true;
        }
        finally
        {
            release.TrySetResult(); probeReaderRelease.TrySetResult();
            try
            {
                try { if (probeReader is not null) await probeReader; }
                finally { if (retiringProbe is not null) await retiringProbe; }
            }
            finally
            {
                try
                {
                    if (suspending is not null) suspended ??= await suspending;
                    if (suspended is not null) await suspended.DisposeAsync();
                }
                finally { await run.CancelAsync(); await running; }
            }
        }
        void Record(string stage, object detail)
        {
            using var output = new FileStream(events, FileMode.Append, FileAccess.Write, FileShare.Read);
            var record = JsonSerializer.SerializeToUtf8Bytes(new { fixture.FixtureId, ObservedUtc = DateTimeOffset.UtcNow, Stage = stage, Detail = detail });
            output.Write(record); output.WriteByte((byte)'\n'); output.Flush(flushToDisk: true);
        }
    }

    private static async Task VerifySourceQuiescenceAsync(string bootstrap, string pipe, CreatorClient client, VaultId id,
        CancellationToken ct, Action<bool, string> check)
    {
        await using var service = await WindowsSingleVaultService.OpenAsync(bootstrap, ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AutomaticRuntimeForFixture.SourceEntryOpened = async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested();
        };
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var server = new NamedPipeFluxVaultServer(service, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
        var running = SupervisedRuntime.RunAsync(server.RunAsync, service.RunAutomaticProtectionAsync, run.Token);
        try
        {
            await entered.Task.WaitAsync(ct);
            var saved = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), ct);
            var request = FluxVaultIpcRequest.SetProtectionPaused(true) with { VaultId = id,
                ExpectedVaultRevision = saved.VaultRevision, OperationId = Guid.NewGuid() };
            var paused = client.SendAsync(request, ct);
            await cancelled.Task.WaitAsync(ct);
            check(!paused.IsCompleted, "Pause cancels a real native enumeration and waits for its held reader before acknowledgement");
            release.TrySetResult();
            var response = await paused;
            check(response.Success, "Pause acknowledges after native discovery joins");
            service.AutomaticRuntimeForFixture.SourceEntryOpened = null;
            var resume = await client.SendAsync(FluxVaultIpcRequest.SetProtectionPaused(false) with
                { VaultId = id, ExpectedVaultRevision = response.VaultRevision, OperationId = Guid.NewGuid() }, ct);
            check(resume.Success, "Resume retains automatic preference and the same source contract");
            var replay = await client.SendAsync(request, ct);
            check(replay.Success && replay.VaultRevision == response.VaultRevision, "original Pause replay returns its historical receipt after Resume");
            await Task.Delay(100, ct);
            check(!running.IsCompleted, "source-cycle cancellation leaves the healthy runtime owner and supervisor running");
        }
        finally { release.TrySetResult(); await run.CancelAsync(); await running; }
    }

    private static async Task VerifyCapturePauseAsync(string bootstrap,string pipe,CreatorClient client,
        AutomaticProtectionSession creator,VaultId id,string sourceRoot,CancellationToken ct,Action<bool,string> check)
    {
        await using var service=await WindowsSingleVaultService.OpenAsync(bootstrap,ct);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AutomaticRuntimeForFixture.CaptureOpened=async token=>
        {
            using var registration=token.Register(()=>cancelled.TrySetResult());
            entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested();
        };
        using var run=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var server=new NamedPipeFluxVaultServer(service,WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe),new WindowsFluxVaultCallerContextProvider());
        var running=SupervisedRuntime.RunAsync(server.RunAsync,service.RunAutomaticProtectionAsync,run.Token);
        try
        {
            var file=Path.Combine(sourceRoot,"pause-held.txt");
            await creator.Caller.RunAsCallerAsync(async()=>{await File.WriteAllTextAsync(file,"save retained across a held capture pause",ct);return true;});
            await entered.Task.WaitAsync(ct);
            var paused=MutateAsync(client,id,FluxVaultIpcRequest.SetProtectionPaused(true),ct);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);
            check(!paused.IsCompleted,"Pause cancels a held native capture before waiting for its mutation gate and does not acknowledge an open reader");
            // Hold dispatch across both mutations, so neither can rely on a later
            // polling cycle to replace the preceding Capturing observation.
            var holding=service.AutomaticRuntimeForFixture.SuspendSourceForFixtureAsync(ct).AsTask();
            release.TrySetResult();
            await using(var held=await holding)
            {
                var response=await paused;
                check(response.Success,"Pause reconciles the cancelled reserved identity and acknowledges after the capture reader joins");
                var stopped=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
                check(stopped.Status?.Configuration.IsEnabled==false && stopped.Status.AutomaticProtection?.State==AutomaticProtectionState.Paused,
                    "acknowledged Pause projects saved Paused immediately while dispatch remains held");
                service.AutomaticRuntimeForFixture.CaptureOpened=null;
                var resumed=await MutateAsync(client,id,FluxVaultIpcRequest.SetProtectionPaused(false),ct);
                check(resumed.Success,"Resume after a held capture pause preserves pending work");
                var current=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
                check(current.VaultRevision==resumed.VaultRevision && current.Status?.Configuration.IsEnabled==true &&
                    current.Status.AutomaticProtection?.State==AutomaticProtectionState.Reconciling,
                    "rapid Resume cannot reuse stale Capturing or Pending before a fresh runtime cycle observes its controls");
            }
            var version=await WaitForHistoryAsync(client,id,file,false,ct);
            check(version.SourcePath==file,"the held capture's saved bytes reach history after Resume without another save");
        }
        finally {release.TrySetResult();await run.CancelAsync();await running;}
    }

    private static async Task VerifyOwnerInterruptionAsync(string bootstrap, string pipe, CreatorClient client,
        AutomaticProtectionSession creator, NpgsqlConnection ownedConnection, VaultCatalogueEndpoint endpoint, VaultBinding binding, string file,
        CancellationToken ct, Action<bool, string> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string before;
        await using (var service = await WindowsSingleVaultService.OpenAsync(bootstrap, ct))
        {
            service.AutomaticRuntimeForFixture.CaptureOpened = async token =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested();
            };
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var server = new NamedPipeFluxVaultServer(service, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
            var running = SupervisedRuntime.RunAsync(server.RunAsync, service.RunAutomaticProtectionAsync, run.Token);
            try
            {
                var saved = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), ct);
                before = saved.Status!.AutomaticProtection!.LastSuccessfulVersionId!;
                await creator.Caller.RunAsCallerAsync(async () => { await File.WriteAllTextAsync(file, "owned backend interruption bytes", ct); return true; });
                await entered.Task.WaitAsync(ct);
                await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", ownedConnection);
                terminate.Parameters.AddWithValue("pid", service.AutomaticRuntimeForFixture.OwnerForFixture.BackendProcessId);
                check(await terminate.ExecuteScalarAsync(ct) is true, "only the verified private fixture's dedicated owner backend is interrupted");
                await cancelled.Task.WaitAsync(ct);
                check(!running.IsCompleted, "owner-loss observation cancels capture independently and waits for its held native reader");
            }
            finally { release.TrySetResult(); await run.CancelAsync(); }
            try { await running; throw new InvalidOperationException("Lost ownership did not fail the supervised generation."); }
            catch (AutomaticProtectionUnavailableException) { }
            check(service.AutomaticRuntimeForFixture.Status.State == AutomaticProtectionState.Failed,
                "joined lost-owner generation reports failure before replacement");
        }
        await using (var catalogue = new PostgreSqlVaultCatalogue(endpoint))
        {
            var retained = await catalogue.ReadAutomaticStateAsync(binding, ct);
            check(retained.UncertainCount == 1 && retained.PendingCount > 0 && retained.LastSuccessfulVersionId == before,
                "owner interruption retains the unresolved reserved result without a new acknowledged publication");
        }
        await using (var service = await WindowsSingleVaultService.OpenAsync(bootstrap, ct))
        {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var server = new NamedPipeFluxVaultServer(service, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
            var running = SupervisedRuntime.RunAsync(server.RunAsync, service.RunAutomaticProtectionAsync, run.Token);
            try
            {
                var after = await WaitForAutomaticVersionAsync(client, binding.Id, ct, before);
                check(after != before, "replacement verifies absence and captures retained pending source work without another save");
            }
            finally { await run.CancelAsync(); await running; }
        }
    }

    private static async Task VerifyRetainedDeletionAsync(string bootstrap,string pipe,CreatorClient client,AutomaticProtectionSession creator,
        VaultId id,string sourceRoot,CancellationToken ct,Action<bool,string> check)
    {
        var path=Path.Combine(sourceRoot,"manual-before-automatic.txt");
        var bytes=Encoding.UTF8.GetBytes("manual history must participate in automatic deletion reconciliation");
        await RunServiceAsync(bootstrap,pipe,ct,async _=>
        {
            check((await MutateAsync(client,id,FluxVaultIpcRequest.SetAutomaticProtection(false),ct)).Success,"automatic preference can be disabled without deleting history");
            await creator.Caller.RunAsCallerAsync(async()=>{await File.WriteAllBytesAsync(path,bytes,ct);return true;});
            check((await MutateAsync(client,id,FluxVaultIpcRequest.RunBackupNow(),ct)).Success,"existing manual backup creates the pre-enrolment file version");
            var original=await WaitForHistoryAsync(client,id,path,false,ct);
            await creator.Caller.RunAsCallerAsync(()=>{File.Delete(path);return Task.FromResult(true);});
            check((await MutateAsync(client,id,FluxVaultIpcRequest.SetAutomaticProtection(true),ct)).Success,"creator re-enables automatic protection after the manual source disappears");
            var deleted=await WaitForHistoryAsync(client,id,path,true,ct);
            check(deleted.DeletedFromVersionId==original.VersionId,"bounded repository reconciliation records a tombstone for manually retained history missing before first automatic observation");
            var output=Path.Combine(sourceRoot,"retained-manual-recovery.bin");
            var restored=await MutateAsync(client,id,FluxVaultIpcRequest.RestoreVersion(original.VersionId,output),ct);
            var actual=await creator.Caller.RunAsCallerAsync(()=>File.ReadAllBytesAsync(output,ct));
            check(restored.Success && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(bytes)),"confirmed deletion retains independently recoverable original manual bytes");
        });
    }

    private static async Task VerifyUnavailableRootAsync(string bootstrap,string pipe,CreatorClient client,AutomaticProtectionSession creator,
        VaultId id,string sourceRoot,CancellationToken ct,Action<bool,string> check)
    {
        var offline=sourceRoot+"-owned-offline";
        Directory.Move(sourceRoot,offline); // Owned fixture preparation; product source inspection remains caller-bound.
        try
        {
            await creator.Caller.RunAsCallerAsync(async()=>{await File.WriteAllTextAsync(Path.Combine(offline,"reappeared.txt"),"saved while owned source root was unavailable",ct);return true;});
            await RunServiceAsync(bootstrap,pipe,ct,async _=>
            {
                var before=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id},ct);
                await WaitForStateAsync(client,AutomaticProtectionState.Blocked,ct);
                var missing=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id},ct);
                check(before.Versions!.Select(item=>item.VersionId).SequenceEqual(missing.Versions!.Select(item=>item.VersionId)),
                    "unavailable source root stays blocked and produces no deletion or captured version");
                Directory.Move(offline,sourceRoot);
                await WaitForHistoryAsync(client,id,Path.Combine(sourceRoot,"reappeared.txt"),false,ct);
                check(true,"root reappearance resumes retained reconciliation and captures its saved file without another save");
            });
        }
        finally { if(Directory.Exists(offline) && !Directory.Exists(sourceRoot))Directory.Move(offline,sourceRoot); }
    }

    private static async Task VerifyManualFallbackAsync(string bootstrap,string pipe,CreatorClient client,AutomaticProtectionSession creator,
        NpgsqlConnection connection,VaultId id,string healthyVersion,byte[] expected,string sourceRoot,CancellationToken ct,Action<bool,string> check)
    {
        await using var version=new NpgsqlCommand("UPDATE fv_control.automatic_identity SET schema_version=99 WHERE singleton",connection);
        await version.ExecuteNonQueryAsync(ct);
        try
        {
            await RunServiceAsync(bootstrap,pipe,ct,async _=>
            {
                var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
                var history=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id},ct);
                check(status.Success && status.Status?.AutomaticProtection?.State==AutomaticProtectionState.Blocked &&
                    history.Success && history.Versions!.Any(item=>item.VersionId==healthyVersion),"unsupported automatic extension keeps authenticated status and history available");
                var denied=await MutateAsync(client,id,FluxVaultIpcRequest.SetAutomaticProtection(false),ct);
                check(!denied.Success && denied.ErrorCode==FluxVaultIpcErrorCode.Unavailable,"unsupported automatic extension blocks fresh unsafe changes");
                var output=Path.Combine(sourceRoot,"extension-fallback-recovery.bin");
                var restored=await MutateAsync(client,id,FluxVaultIpcRequest.RestoreVersion(healthyVersion,output),ct);
                var actual=await creator.Caller.RunAsCallerAsync(()=>File.ReadAllBytesAsync(output,ct));
                check(restored.Success && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(expected)),"manual recovery remains independently verified while the automatic extension is unavailable");
            });
        }
        finally {version.CommandText="UPDATE fv_control.automatic_identity SET schema_version=2 WHERE singleton";await version.ExecuteNonQueryAsync(CancellationToken.None);}
    }

    private static async Task VerifyUncertainPublicationAsync(string bootstrap,string pipe,CreatorClient client,AutomaticProtectionSession creator,
        VaultId id,VaultBinding binding,string healthyVersion,byte[] expected,string file,CancellationToken ct,Action<bool,string> check)
    {
        FileVersionManifest? published=null;
        string? chunkPath=null;byte[]? originalChunk=null;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceBytes=Encoding.UTF8.GetBytes("committed automatic bytes whose completion acknowledgement is interrupted");
        int previousCount=0;
        try
        {
            await RunServiceAsync(bootstrap,pipe,ct,async service=>
            {
                service.AutomaticRuntimeForFixture.PublicationCommitted=async (manifest,token)=>
                {
                    if(manifest.SourcePath!=file || published is not null)return;
                    published=manifest;
                    var chunk=manifest.Chunks.Single();
                    chunkPath=Path.Combine(binding.RepositoryPath,"chunks",chunk.Digest[..2],chunk.Digest+".chunk");
                    originalChunk=await File.ReadAllBytesAsync(chunkPath,token);
                    await File.WriteAllBytesAsync(chunkPath,[1,2,3],token);
                    entered.TrySetResult();
                    throw new IOException("Owned post-publication corruption before work completion acknowledgement.");
                };
                var before=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id},ct);
                previousCount=before.Versions!.Count(item=>item.SourcePath==file && !item.IsDeleted);
                await creator.Caller.RunAsCallerAsync(async()=>{await File.WriteAllBytesAsync(file,sourceBytes,ct);return true;});
                await entered.Task.WaitAsync(ct);
                await WaitForStateAsync(client,AutomaticProtectionState.OutcomeUncertain,ct);
                var refused=await MutateAsync(client,id,FluxVaultIpcRequest.SetProtectionPaused(true),ct);
                check(!refused.Success && refused.ErrorCode==FluxVaultIpcErrorCode.Unavailable,"uncertainty created during capture blocks a fresh mutation before releasing safe admission");
                var output=Path.Combine(Path.GetDirectoryName(file)!,"uncertain-healthy-recovery.bin");
                var restored=await MutateAsync(client,id,FluxVaultIpcRequest.RestoreVersion(healthyVersion,output),ct);
                var actual=await creator.Caller.RunAsCallerAsync(()=>File.ReadAllBytesAsync(output,ct));
                check(restored.Success && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(expected)),"healthy manual recovery remains verified while an automatic publication is uncertain");
            });
            await RunServiceAsync(bootstrap,pipe,ct,async _=>
            {
                var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
                check(status.Success && status.Status?.AutomaticProtection?.State==AutomaticProtectionState.OutcomeUncertain &&
                    status.Status.AutomaticProtection.UncertainCount==1,"restart preserves corrupt reserved publication as uncertain rather than recapturing or acknowledging it");
            });
            await File.WriteAllBytesAsync(chunkPath!,originalChunk!,ct); // Repair only the exact synthetic corruption before reconciliation.
            await RunServiceAsync(bootstrap,pipe,ct,async _=>
            {
                var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
                var history=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id},ct);
                check(status.Status?.AutomaticProtection?.LastSuccessfulVersionId==published!.VersionId &&
                    history.Versions!.Count(item=>item.SourcePath==file && !item.IsDeleted)==previousCount+1,
                    "verified restart reconciles the original committed identity exactly once without another save");
                var output=Path.Combine(Path.GetDirectoryName(file)!,"reconciled-recovery.bin");
                var restored=await MutateAsync(client,id,FluxVaultIpcRequest.RestoreVersion(published.VersionId,output),ct);
                var actual=await creator.Caller.RunAsCallerAsync(()=>File.ReadAllBytesAsync(output,ct));
                check(restored.Success && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(sourceBytes)),"reconciled original publication recovers independently verified new bytes");
            });
        }
        finally {if(chunkPath is not null && originalChunk is not null)await File.WriteAllBytesAsync(chunkPath,originalChunk,CancellationToken.None);}
    }

    private static async Task VerifyMirrorRetryAsync(string bootstrap,string pipe,CreatorClient client,
        AutomaticProtectionSession creator,NpgsqlDataSource source,VaultBinding binding,string sourceRoot,string mirrorRoot,
        CancellationToken ct,Action<bool,string> check)
    {
        await RunServiceAsync(bootstrap,pipe,ct,async _=>await WaitForMirrorsAsync(client,0,ct));
        var offline=mirrorRoot+"-offline";Directory.Move(mirrorRoot,offline);
        var path=Path.Combine(sourceRoot,"mirror-retry.txt");
        var firstBytes=Encoding.UTF8.GetBytes("older primary while the mirror is offline");
        var secondBytes=Encoding.UTF8.GetBytes("newer primary must continue while the mirror is offline");
        string first="",second="";
        try
        {
            await RunServiceAsync(bootstrap,pipe,ct,async _=>
            {
                await WaitForStateAsync(client,AutomaticProtectionState.WaitingForChanges,ct);
                var before=(await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct)).Status!.AutomaticProtection!.LastSuccessfulVersionId;
                await creator.Caller.RunAsCallerAsync(async()=>{await File.WriteAllBytesAsync(path,firstBytes,ct);return true;});
                first=await WaitForAutomaticVersionAsync(client,binding.Id,ct,before);
                await creator.Caller.RunAsCallerAsync(async()=>{await File.WriteAllBytesAsync(path,secondBytes,ct);return true;});
                second=await WaitForAutomaticVersionAsync(client,binding.Id,ct,first);
                var saved=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
                check(first!=second && saved.Status?.AutomaticProtection?.MirrorPendingCount>0,
                    "an offline mirror leaves durable lag while two successive primary captures of the same file continue");
            });
            await RunServiceAsync(bootstrap,pipe,ct,async _=>
            {
                var saved=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
                var history=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=binding.Id},ct);
                check(saved.Status?.AutomaticProtection?.MirrorPendingCount>0 && history.Success &&
                    history.Versions!.Any(item=>item.VersionId==first) && history.Versions!.Any(item=>item.VersionId==second),
                    "restart preserves the older and newer primary history together with unresolved mirror coverage");
                foreach(var item in new[]{(Version:first,Bytes:firstBytes),(Version:second,Bytes:secondBytes)})
                {
                    var output=Path.Combine(sourceRoot,"offline-recovery-"+item.Version+".bin");
                    var restored=await MutateAsync(client,binding.Id,FluxVaultIpcRequest.RestoreVersion(item.Version,output),ct);
                    var actual=await creator.Caller.RunAsCallerAsync(()=>File.ReadAllBytesAsync(output,ct));
                    check(restored.Success && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(item.Bytes)),
                        "healthy primary recovery remains independently verified while its mirror is offline: "+item.Version);
                }
            });
        }
        finally {if(Directory.Exists(offline))Directory.Move(offline,mirrorRoot);}
        await RunServiceAsync(bootstrap,pipe,ct,async _=>
        {
            await WaitForMirrorsAsync(client,0,ct);
            await using var metadata=new PostgreSqlRepositoryMetadataStore(binding);
            foreach(var item in new[]{(Version:first,Bytes:firstBytes),(Version:second,Bytes:secondBytes)})
            {
                var manifest=await metadata.ReadManifestAsync(item.Version,ct);
                var chunk=manifest.Chunks.Single();
                var mirrorBytes=await File.ReadAllBytesAsync(Path.Combine(mirrorRoot,"chunks",chunk.Digest[..2],chunk.Digest+".chunk"),ct);
                check(SHA256.HashData(mirrorBytes).SequenceEqual(SHA256.HashData(item.Bytes)),
                    "restored mirror receives independently verified retained primary bytes without another source save: "+item.Version);
            }
        });
    }

    private static async Task VerifyRollbackAsync(string bootstrap,string pipe,CreatorClient client,AutomaticProtectionSession creator,
        NpgsqlConnection connection,VaultCatalogueEndpoint endpoint,VaultBinding binding,string sourceRoot,string healthyVersion,byte[] healthyBytes,
        CancellationToken ct,Action<bool,string> check)
    {
        var oldPending=Path.Combine(sourceRoot,"rollback-pending.txt");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await RunServiceAsync(bootstrap,pipe,ct,async service=>
        {
            service.AutomaticRuntimeForFixture.CaptureOpened=async token=>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite,token); };
            await creator.Caller.RunAsCallerAsync(async()=>{await File.WriteAllTextAsync(oldPending,"pending before rollback",ct);return true;});
            await entered.Task.WaitAsync(ct);
        }); // Cancel and join the actual reader and owner before publishing the rollback marker.
        await using var catalogue=new PostgreSqlVaultCatalogue(endpoint);
        var beforeMarker=await ExtensionSnapshotAsync(connection,ct);
        await using(var transaction=await connection.BeginTransactionAsync(ct))
        {
            await using var held=new NpgsqlCommand("LOCK TABLE fv_control.automatic_policy IN ACCESS EXCLUSIVE MODE",connection,transaction);
            await held.ExecuteNonQueryAsync(ct);
            using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct); cancel.CancelAfter(250);
            try {await catalogue.RequireAutomaticApprovalAsync(binding,cancel.Token);throw new InvalidOperationException("Interrupted rollback marker unexpectedly committed.");}
            catch(OperationCanceledException) when(cancel.IsCancellationRequested && !ct.IsCancellationRequested) { }
            await transaction.RollbackAsync(ct);
        }
        check(beforeMarker==await ExtensionSnapshotAsync(connection,ct),"interrupted rollback-marker transaction preserves policy and pending work");
        var operationalMarker=await AutomaticRollbackSqlProbe.ApplyAsync(connection,binding,creator.Caller.UserSid,ct);
        check(operationalMarker.GetProperty("AutomaticApprovalRequired").GetBoolean(),"actual operational rollback SQL invalidates consent after joined source shutdown");
        var marked=await catalogue.ReadAutomaticStateAsync(binding,ct);
        check(marked.Policy.Enabled && marked.RequiresCreatorApproval && marked.UncertainCount>0,
            "joined new runtime publishes durable renewed-approval marker while retaining preference and reserved pending identity");
        var snapshot=await ExtensionSnapshotAsync(connection,ct);
        var receipts=await ReceiptSnapshotAsync(connection,ct);
        var newRoot=sourceRoot+"-after-rollback";PrivateDirectory(newRoot,creator.Caller.UserSid);
        var newFile=Path.Combine(newRoot,"fresh-scope.txt");
        var newBytes=Encoding.UTF8.GetBytes("saved under the actual predecessor's changed source scope");
        await creator.Caller.RunAsCallerAsync(async()=>{await File.WriteAllBytesAsync(newFile,newBytes,ct);return true;});
        await PredecessorAutomaticRollbackProbe.RunAsync(bootstrap,pipe,ct,async()=>
        {
            var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
            var history=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=binding.Id},ct);
            check(status.Success && history.Success && history.Versions!.Any(item=>item.VersionId==healthyVersion),
                "actual accepted 1.0.9 service reads forward base configuration and retained history");
            var output=Path.Combine(sourceRoot,"old-binary-recovery.bin");
            var restored=await MutateAsync(client,binding.Id,FluxVaultIpcRequest.RestoreVersion(healthyVersion,output),ct);
            var actual=await creator.Caller.RunAsCallerAsync(()=>File.ReadAllBytesAsync(output,ct));
            check(restored.Success && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(healthyBytes)),
                "actual accepted 1.0.9 recovery independently verifies new-store retained bytes");
            var changed=status.Status!.Configuration with {WatchedFolders=[status.Status.Configuration.WatchedFolders[0] with {Path=newRoot}]};
            var saved=await MutateAsync(client,binding.Id,FluxVaultIpcRequest.SaveConfiguration(changed),ct);
            check(saved.Success,"actual accepted 1.0.9 saves a changed source scope without purging history");
        });
        check(snapshot==await ExtensionSnapshotAsync(connection,ct),"actual predecessor read, recovery and save leave automatic policy, work and scope records byte-for-byte unchanged");
        var afterReceipts=await ReceiptSnapshotAsync(connection,ct);
        check(receipts.All(item=>afterReceipts.TryGetValue(item.Key,out var value) && value==item.Value),
            "actual predecessor preserves every original operation receipt while recording its own manual operations");
        await RunServiceAsync(bootstrap,pipe,ct,async _=>
        {
            await Task.Delay(TimeSpan.FromSeconds(5),ct);
            var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
            var history=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=binding.Id},ct);
            check(status.Status?.AutomaticProtection?.RequiresCreatorApproval==true &&
                !history.Versions!.Any(item=>item.SourcePath==newFile || item.SourcePath==oldPending),
                "re-upgrade retains pending work but captures neither old nor changed scope before renewed creator consent");
            check((await MutateAsync(client,binding.Id,FluxVaultIpcRequest.SetAutomaticProtection(true),ct)).Success,
                "fresh creator approval accepts the predecessor's current source scope after rollback");
            var version=await WaitForHistoryAsync(client,binding.Id,newFile,false,ct);
            history=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=binding.Id},ct);
            check(history.Versions!.Count(item=>item.SourcePath==newFile)==1 && !history.Versions!.Any(item=>item.SourcePath==oldPending),
                "fresh consent captures the current saved source without another save and supersedes obsolete queued scope without duplicate completion");
            foreach(var item in new[]{(Version:healthyVersion,Bytes:healthyBytes),(Version:version.VersionId,Bytes:newBytes)})
            {
                var output=Path.Combine(newRoot,"re-upgrade-"+item.Version+".bin");
                var restored=await MutateAsync(client,binding.Id,FluxVaultIpcRequest.RestoreVersion(item.Version,output),ct);
                var actual=await creator.Caller.RunAsCallerAsync(()=>File.ReadAllBytesAsync(output,ct));
                check(restored.Success && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(item.Bytes)),
                    "re-upgraded service independently recovers preserved and current-scope history: "+item.Version);
            }
        });
    }

    private static async Task<string> ExtensionSnapshotAsync(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("""
            SELECT jsonb_build_array(
                (SELECT jsonb_agg(to_jsonb(p) ORDER BY singleton) FROM fv_control.automatic_policy p),
                (SELECT jsonb_agg(to_jsonb(w) ORDER BY path_key) FROM fv_control.automatic_work w),
                (SELECT jsonb_agg(to_jsonb(s) ORDER BY scope_id) FROM fv_control.automatic_scopes s),
                (SELECT jsonb_agg(to_jsonb(i) ORDER BY singleton) FROM fv_control.automatic_identity i),
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY singleton) FROM fv_control.automatic_runtime r))::text
            """,connection);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<Dictionary<Guid,string>> ReceiptSnapshotAsync(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("SELECT operation_id,to_jsonb(o)::text FROM fv_control.operations o",connection);
        await using var reader=await command.ExecuteReaderAsync(ct);
        var result=new Dictionary<Guid,string>();
        while(await reader.ReadAsync(ct))result.Add(reader.GetGuid(0),reader.GetString(1));
        return result;
    }

    private static async Task WaitForMirrorsAsync(CreatorClient client,int count,CancellationToken ct)
    {
        while(true)
        {
            var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
            if(!status.Success)throw new InvalidOperationException("Mirror status is unavailable: "+status.ErrorMessage);
            if(status.Status?.AutomaticProtection?.MirrorPendingCount==count)return;
            await Task.Delay(100,ct);
        }
    }

    private static async Task RunServiceAsync(string bootstrap,string pipe,CancellationToken ct,Func<WindowsSingleVaultService,Task> assertion)
    {
        await using var service=await WindowsSingleVaultService.OpenAsync(bootstrap,ct);
        using var run=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var server=new NamedPipeFluxVaultServer(service,WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe),new WindowsFluxVaultCallerContextProvider());
        var running=SupervisedRuntime.RunAsync(server.RunAsync,service.RunAutomaticProtectionAsync,run.Token);
        try {await assertion(service);}
        finally {await run.CancelAsync();await running;}
    }

    private static async Task VerifyStorageWriteFailureAsync(string bootstrap, string pipe, CreatorClient client,
        AutomaticProtectionSession creator, VaultBinding binding, string sourceRoot, CancellationToken ct, Action<bool, string> check)
    {
        var repository = Path.GetFullPath(binding.RepositoryPath);
        var fixtureParent = Path.GetFullPath(WindowsDatabaseProbeConfiguration.AllowedParent) + Path.DirectorySeparatorChar;
        if (!repository.StartsWith(fixtureParent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(repository) != "repository")
            throw new UnauthorizedAccessException("Storage fault is confined to the owned private fixture repository.");
        var chunks = Path.Combine(repository, "chunks");
        var retained = Path.Combine(repository, "chunks-owned-unavailable");
        var blocker = Encoding.UTF8.GetBytes("FluxVault owned storage-unavailable fixture");
        var file = Path.Combine(sourceRoot, "storage-failure.txt");
        var expected = Encoding.UTF8.GetBytes("Saved while private primary chunk storage is unavailable " + Guid.NewGuid());
        await RunServiceAsync(bootstrap, pipe, ct, async _ =>
        {
            await creator.Caller.RunAsCallerAsync(async () => { await File.WriteAllBytesAsync(file, expected, ct); return true; });
            while (true)
            {
                var pending = (await client.SendAsync(FluxVaultIpcRequest.GetStatus(), ct)).Status!.AutomaticProtection!;
                if (pending.PendingCount > 0)
                {
                    var history = await client.SendAsync(FluxVaultIpcRequest.ListVersions() with { VaultId = binding.Id }, ct);
                    check(history.Success && !history.Versions!.Any(item => item.SourcePath == file),
                        "source save is durably pending before the private storage outage and joined restart");
                    break;
                }
                await Task.Delay(50, ct);
            }
        });
        if (!Directory.Exists(chunks) || File.Exists(retained) || Directory.Exists(retained)) throw new InvalidDataException("Unexpected owned storage state.");
        FluxVault.Testing.WindowsDatabaseProbeConfiguration.RejectReparseComponents(chunks);
        Directory.Move(chunks, retained);
        try
        {
            await File.WriteAllBytesAsync(chunks, blocker, ct);
            await RunServiceAsync(bootstrap, pipe, ct, async _ =>
            {
                var before = (await client.SendAsync(FluxVaultIpcRequest.GetStatus(), ct)).Status!.AutomaticProtection!.LastSuccessfulVersionId;
                while (true)
                {
                    var status = (await client.SendAsync(FluxVaultIpcRequest.GetStatus(), ct)).Status!.AutomaticProtection!;
                    if (status.State is AutomaticProtectionState.Blocked or AutomaticProtectionState.OutcomeUncertain)
                    {
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { StorageFailureStatus = status }));
                        check(status.PendingCount + status.UncertainCount > 0 && !string.IsNullOrWhiteSpace(status.Reason),
                            "primary storage write failure is visible and its work remains durable");
                        check(status.LastSuccessfulVersionId == before, "unavailable primary storage cannot acknowledge a new capture");
                        break;
                    }
                    await Task.Delay(100, ct);
                }
                RestoreChunkDirectory();
                var version = await WaitForHistoryAsync(client, binding.Id, file, false, ct);
                var output = Path.Combine(sourceRoot, "storage-failure-recovery.bin");
                var restored = await MutateAsync(client, binding.Id, FluxVaultIpcRequest.RestoreVersion(version.VersionId, output), ct);
                var actual = await creator.Caller.RunAsCallerAsync(() => File.ReadAllBytesAsync(output, ct));
                check(restored.Success && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(expected)),
                    "restored primary storage captures retained work without another save and recovers independently verified bytes");
                var sentinel = Encoding.UTF8.GetBytes("Existing destination must survive an unconfirmed restore.");
                await creator.Caller.RunAsCallerAsync(async () => { await File.WriteAllBytesAsync(output, sentinel, ct); return true; });
                var overwrite = await MutateAsync(client, binding.Id, FluxVaultIpcRequest.RestoreVersion(version.VersionId, output), ct);
                var preserved = await creator.Caller.RunAsCallerAsync(() => File.ReadAllBytesAsync(output, ct));
                check(!overwrite.Success && preserved.SequenceEqual(sentinel), "unconfirmed recovery refuses an existing destination and preserves its distinct contents");
                check(!Directory.EnumerateFileSystemEntries(sourceRoot, ".FluxVault-recovery-*").Any(),
                    "refused recovery leaves no owned staging entries");
                var confirmed = await MutateAsync(client, binding.Id, FluxVaultIpcRequest.RestoreVersion(version.VersionId, output)
                    with { OverwriteConfirmed = true }, ct);
                var replaced = await creator.Caller.RunAsCallerAsync(() => File.ReadAllBytesAsync(output, ct));
                check(confirmed.Success && SHA256.HashData(replaced).SequenceEqual(SHA256.HashData(expected)),
                    "separately confirmed overwrite restores and independently verifies the expected bytes");
            });
        }
        finally { if (Directory.Exists(retained)) RestoreChunkDirectory(); }

        void RestoreChunkDirectory()
        {
            if (!File.Exists(chunks) || !File.ReadAllBytes(chunks).SequenceEqual(blocker) || !Directory.Exists(retained))
                throw new InvalidDataException("Owned storage fault target changed; preserve it for inspection.");
            File.Delete(chunks);
            Directory.Move(retained, chunks);
        }
    }
    private static async Task<FluxVaultIpcResponse> MutateAsync(CreatorClient client,VaultId id,FluxVaultIpcRequest request,CancellationToken ct)
    {
        var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
        return await client.SendAsync(request with {VaultId=id,ExpectedVaultRevision=status.VaultRevision,OperationId=Guid.NewGuid()},ct);
    }
    private static async Task WaitForStateAsync(CreatorClient client,AutomaticProtectionState state,CancellationToken ct)
    {
        while(true)
        {
            var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),ct);
            if(status.Status?.AutomaticProtection?.State==state)return;
            await Task.Delay(100,ct);
        }
    }
    private static async Task<RepositoryVersionSummary> WaitForHistoryAsync(CreatorClient client,VaultId id,string path,bool deleted,CancellationToken ct)
    {
        while(true)
        {
            var response=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id},ct);
            if(!response.Success)throw new InvalidOperationException("History query failed: "+response.ErrorMessage);
            var version=response.Versions?.FirstOrDefault(item=>item.SourcePath==path && item.IsDeleted==deleted);
            if(version is not null)return version;
            await Task.Delay(100,ct);
        }
    }

    private static async Task<string> WaitForAutomaticVersionAsync(CreatorClient client, VaultId id, CancellationToken ct, string? exceptVersion = null)
    {
        string? previous = null;
        while (true)
        {
            var status = await client.SendAsync(FluxVaultIpcRequest.GetStatus() with { VaultId = id, StatusDetailLevel = FluxVaultStatusDetailLevel.Fast }, ct);
            var diagnostic = JsonSerializer.Serialize(new { status.Success, status.ErrorCode, status.ErrorMessage, status.Status?.AutomaticProtection });
            if (diagnostic != previous) { Console.Error.WriteLine(diagnostic); previous = diagnostic; }
            if (status.Status?.AutomaticProtection?.LastSuccessfulVersionId is { } version && version != exceptVersion) return version;
            if (status.Status?.AutomaticProtection?.State is AutomaticProtectionState.Failed or AutomaticProtectionState.OutcomeUncertain)
                throw new InvalidOperationException(status.Status.AutomaticProtection.Reason);
            await Task.Delay(200, ct);
        }
    }
    private static void PrivateDirectory(string path, string? creatorSid = null, string? traversalSid = null)
    {
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier("S-1-5-18"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544", creatorSid }.OfType<string>().Distinct())
            acl.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (traversalSid is not null) acl.AddAccessRule(new(new SecurityIdentifier(traversalSid),
            FileSystemRights.Traverse | FileSystemRights.Synchronize, AccessControlType.Allow));
        FileSystemAclExtensions.Create(new DirectoryInfo(path), acl);
    }
    private sealed class CreatorClient(AutomaticProtectionSession creator, string pipe) : IFluxVaultServiceClient
    {
        private readonly NamedPipeFluxVaultClient inner = new(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(pipe, "S-1-5-18"));
        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default) =>
            creator.Caller.RunAsCallerAsync(() => inner.SendAsync(request, cancellationToken));
    }
}
