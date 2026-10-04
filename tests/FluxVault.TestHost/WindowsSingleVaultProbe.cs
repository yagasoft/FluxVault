using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Testing;
using FluxVault.Windows.Security;
using Npgsql;

namespace FluxVault.TestHost;

/// <summary>Native single-installation command flow in the explicitly owned Windows/SSPI fixture.</summary>
internal static class WindowsSingleVaultProbe
{
    internal static async Task<int> RunAsync(string configurationPath, string actor)
    {
        var fixture = JsonSerializer.Deserialize<WindowsDatabaseProbeConfiguration>(await File.ReadAllTextAsync(configurationPath))!;
        fixture.Validate();
        using var identity = WindowsIdentity.GetCurrent();
        if (!fixture.Actors.TryGetValue(actor, out var expected) || identity.User?.Value != expected) throw new UnauthorizedAccessException();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var pipe = "FluxVault.Tests." + fixture.FixtureId;
        if (actor == "System") return await RunServerAsync(fixture, pipe, deadline);
        var client = new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(pipe, fixture.Actors["System"]));
        if (actor == "B")
        {
            var denied = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), deadline.Token);
            if (denied.Success || denied.ErrorCode != FluxVaultIpcErrorCode.Denied || denied.VaultId is not null || denied.Status is not null)
                throw new InvalidOperationException("Ungrant user received installed identity or data.");
            Console.WriteLine(JsonSerializer.Serialize(new { Actor = actor, Denied = true, NoIdentityOrHistory = true }));
            return 0;
        }
        var checks = new List<string>();
        var output = Path.Combine(fixture.Root, "output-A");
        var source = Path.Combine(output, "office"); var cad = Path.Combine(output, "cad");
        var destination = Path.Combine(output, "destination");
        Directory.CreateDirectory(source); Directory.CreateDirectory(cad); Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var file = Path.Combine(source, "document.docx");
        var drawing = Path.Combine(cad, "drawing.dwg");
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 251)).ToArray(), deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "notes.docx"), "generated nested working bytes", deadline.Token);
        await File.WriteAllBytesAsync(drawing, Enumerable.Range(0, 512 * 1024).Select(index => (byte)(index % 239)).ToArray(), deadline.Token);
        var initial = await Send(FluxVaultIpcRequest.GetStatus());
        if (initial.VaultId is not { IsValid: true } id || initial.VaultRevision is not > 0 || initial.Status is null)
            throw new InvalidOperationException("Initial authorised status did not return the installed binding.");
        var revision = initial.VaultRevision.Value;
        Check(initial.Status.LastMessage.Contains("Automatic protection unavailable; manual backup available", StringComparison.Ordinal),
            "product composition truthfully reports blocked automatic protection");
        checks.Add("native creator receives the installed identity and configuration");
        var baseline = initial.Status.Configuration;
        var draft = baseline with
        {
            WatchedFolders = [new("office", source, true, ["*.docx"], [], CompressionPreference.Off, ResourceProfile.Balanced, true),
                new("cad", cad, true, ["*.dwg"], [], CompressionPreference.Off, ResourceProfile.Balanced, true)]
        };
        var save = Bind(FluxVaultIpcRequest.SaveConfiguration(draft));
        var saved = await Send(save); revision = saved.VaultRevision!.Value;
        var accepted = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
        Check(JsonSerializer.Serialize(accepted.Status!.Configuration with { WatchedFolders = baseline.WatchedFolders }) == JsonSerializer.Serialize(baseline),
            "real PostgreSQL save preserves every untouched configuration property");
        Check(accepted.Status.Configuration.WatchedFolders.Count == 2, "multiple protected folders and file types persist in the single vault");
        var stale = await client.SendAsync(save with { OperationId = Guid.NewGuid() }, deadline.Token);
        Check(!stale.Success && stale.ErrorCode == FluxVaultIpcErrorCode.StaleRevision, "stale save cannot replace accepted configuration");
        var wrong = await client.SendAsync(Bind(FluxVaultIpcRequest.ListVersions()) with { VaultId = VaultId.New() }, deadline.Token);
        Check(!wrong.Success && wrong.ErrorCode == FluxVaultIpcErrorCode.Denied && wrong.Versions is null, "wrong repository identity cannot read history");
        var backupRequest = Bind(FluxVaultIpcRequest.RunBackupNow());
        var backedUp = await Send(backupRequest);
        Check(backedUp.Backup is { Success: true, CapturedFileCount: 3 }, "native caller files are captured through the authenticated executor");
        var repeated = await Send(backupRequest);
        Check(repeated.OperationId == backedUp.OperationId && JsonSerializer.Serialize(repeated.Backup) == JsonSerializer.Serialize(backedUp.Backup),
            "duplicate backup returns its durable receipt without repeated execution");
        var history = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        var fileVersion = history.Versions!.Single(item => item.SourcePath == file && item.EntryKind == RepositoryEntryKind.File);
        var folderVersion = history.Versions!.Where(item => item.SourcePath == source && item.EntryKind == RepositoryEntryKind.Folder)
            .OrderByDescending(item => item.CapturedAtUtc).First();
        Check(history.Versions!.Count(item => item.EntryKind == RepositoryEntryKind.File) == 3, "history contains both source folders' captured files");
        var inspection = await Send(Bind(FluxVaultIpcRequest.InspectVersion(fileVersion.VersionId)));
        Check(inspection.Inspection is not null, "authorised history inspection succeeds");
        var recoveredFile = Path.Combine(destination, "recovered.docx");
        var restored = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(fileVersion.VersionId, recoveredFile)));
        Check(restored.RestoreResult?.VerifiedLogicalBytes == new FileInfo(file).Length && Hash(file) == Hash(recoveredFile),
            "standalone recovery publishes independently verified bytes to the caller");
        await File.AppendAllTextAsync(recoveredFile, " edited", deadline.Token);
        var recoveredFolder = Path.Combine(destination, "recovered-folder");
        var folderRestore = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(folderVersion.VersionId, recoveredFolder)));
        Check(folderRestore.RestoreResult is { RestoredFileCount: 2 } && Hash(file) == Hash(Path.Combine(recoveredFolder, "document.docx")) &&
            Hash(Path.Combine(source, "nested", "notes.docx")) == Hash(Path.Combine(recoveredFolder, "nested", "notes.docx")),
            "nested folder recovery preserves independently verified bytes");
        var after = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
        Check(after.Status?.LastCaptureUtc is not null && after.Status.BackupRuntime?.CapturedFileCount == 3, "new status requests retain completed backup state");
        Console.WriteLine(JsonSerializer.Serialize(new { Actor = actor, Passed = checks.Count, Checks = checks, SingleVault = true, VaultId = id,
            CallerCanEditPublished = true, IndependentSha256 = true, ActualCatalogueAndExecutor = true }));
        return 0;

        FluxVaultIpcRequest Bind(FluxVaultIpcRequest request) => request with { VaultId = id,
            ExpectedVaultRevision = PostgreSqlVaultCatalogue.IsMutation(request.Command) ? revision : null,
            OperationId = PostgreSqlVaultCatalogue.IsMutation(request.Command) ? Guid.NewGuid() : null };
        async Task<FluxVaultIpcResponse> Send(FluxVaultIpcRequest request)
        {
            var response = await client.SendAsync(request, deadline.Token);
            if (!response.Success) throw new InvalidOperationException($"{request.Command} failed: {response.ErrorCode}: {response.ErrorMessage}");
            return response;
        }
        void Check(bool result, string name) { if (!result) throw new InvalidOperationException("Single-vault contract failed: " + name); checks.Add(name); }
        static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    }

    private static async Task<int> RunServerAsync(WindowsDatabaseProbeConfiguration fixture, string pipe, CancellationTokenSource deadline)
    {
        var endpoint = new VaultCatalogueEndpoint(Guid.ParseExact(fixture.FixtureId, "N"), "127.0.0.1", fixture.Port, fixture.Database, fixture.Role);
        await using (var source = WindowsDatabaseProbe.CreateDataSource(WindowsDatabaseProbe.CreateConnectionSettings(fixture, "127.0.0.1", "System", Guid.NewGuid())))
        await using (var connection = await source.OpenConnectionAsync(deadline.Token))
        await using (var query = new NpgsqlCommand("SELECT current_setting('fluxvault.test_instance'), current_database(), current_user", connection))
        await using (var result = await query.ExecuteReaderAsync(deadline.Token))
            if (!await result.ReadAsync(deadline.Token) || result.GetString(0) != fixture.FixtureId || result.GetString(1) != fixture.Database || result.GetString(2) != fixture.Role)
                throw new InvalidOperationException("Single-vault pipeline does not own this database.");
        var parent = Path.Combine(fixture.Root, "catalogue", "single"); CreateFreshPrivateRoot(parent);
        var repositoryPath = Path.Combine(parent, "repository"); var statePath = Path.Combine(parent, "state");
        var mirrorOne = Path.Combine(parent, "mirror-one"); var mirrorTwo = Path.Combine(parent, "mirror-two");
        foreach (var path in new[] { repositoryPath, statePath, mirrorOne, mirrorTwo }) CreateFreshPrivateRoot(path);
        var metadata = MetadataStoreConfiguration.CreateDefault(statePath) with
        { Host = endpoint.Host, Port = endpoint.Port, DatabaseName = endpoint.Database, Username = endpoint.ServiceRole };
        var binding = new VaultBinding(new(endpoint.InstanceId), repositoryPath, statePath, metadata);
        var configuration = FluxVaultConfiguration.CreateDefault(statePath) with { RepositoryPath = repositoryPath, MetadataStore = metadata,
            RetentionPolicy = RetentionPolicy.CreateDefault() with { IsEnabled = false },
            MirrorSet = new([new("first", "First", mirrorOne, true), new("second", "Second", mirrorTwo, true)]) };
        await using var catalogue = new PostgreSqlVaultCatalogue(endpoint);
        await catalogue.ProvisionAsync(deadline.Token);
        await using (var store = new PostgreSqlRepositoryMetadataStore(binding))
        {
            await store.ProvisionVaultAsync(deadline.Token);
            var repository = new FileSystemChunkRepository(binding, new FastCdcChunker(new()), new Blake3ContentHasher(), new ZstdChunkCodec(), configuration.MirrorSet, store);
            await repository.ProvisionVaultStorageAsync(deadline.Token);
        }
        await using var handler = new ProvisionedFixtureHandler(fixture, endpoint, binding, configuration, catalogue);
        var server = new NamedPipeFluxVaultServer(handler, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
        var serving = server.RunAsync(deadline.Token);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "runtime", "caller-files-ready.json"), JsonSerializer.Serialize(new { Pipe = pipe }), deadline.Token);
            while (!File.Exists(Path.Combine(fixture.Root, "runtime", "caller-files-stop"))) await Task.Delay(50, deadline.Token);
        }
        finally { await deadline.CancelAsync(); await serving; }
        if (!handler.CreatorVerified || !handler.UngrantDenied || handler.Completed < 8) throw new InvalidOperationException("Native command pipeline evidence is incomplete.");
        Console.WriteLine(JsonSerializer.Serialize(new { Passed = handler.Completed, NativeCallerTokens = true, SingleVault = true, ProtectedProductComposition = true,
            ActualCatalogueAndExecutor = true, handler.CreatorVerified, handler.UngrantDenied, ExactInstalledBinding = binding.Id }));
        return 0;
    }

    // Explicitly owned fixture provisioning, not a product request/lifecycle API. The
    // trusted mission names the creator; owner SID is taken from the native pipe token.
    // All commands after that one-time provision use the actual catalogue dispatcher.
    private sealed class ProvisionedFixtureHandler(WindowsDatabaseProbeConfiguration fixture, VaultCatalogueEndpoint endpoint, VaultBinding binding,
        FluxVaultConfiguration configuration, PostgreSqlVaultCatalogue catalogue)
        : IAuthenticatedFluxVaultRequestHandler, IAsyncDisposable
    {
        private readonly SemaphoreSlim initialisation = new(1, 1);
        internal bool CreatorVerified; internal bool UngrantDenied; internal int Completed;
        private WindowsSingleVaultService? service;
        public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken token)
        {
            await initialisation.WaitAsync(token);
            try
            {
                if (!CreatorVerified && caller.UserSid == fixture.Actors["A"] && caller.ImpersonationPermitted && request.Command == FluxVaultIpcCommand.GetStatus && request.VaultId is null)
                {
                    var installed = await catalogue.InitializeAsync(caller, binding, "FluxVault", configuration, token);
                    CreatorVerified = installed.Access.OwnerSid == caller.UserSid;
                    var installation = new FluxVaultInstallation(endpoint, binding, caller.UserSid);
                    try
                    {
                        await catalogue.VerifyInstallationAsync(installation with { CreatorSid = fixture.Actors["B"] }, token);
                        throw new InvalidOperationException("A different bootstrap creator was accepted.");
                    }
                    catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.IdentityMismatch) { }
                    var bootstrap = Path.Combine(binding.StateRoot, "installation.json");
                    var temp = bootstrap + ".tmp";
                    await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(installation), token);
                    var fileAcl = new FileInfo(temp).GetAccessControl(); fileAcl.SetOwner(new SecurityIdentifier("S-1-5-18"));
                    new FileInfo(temp).SetAccessControl(fileAcl);
                    File.Move(temp, bootstrap, overwrite: false);
                    service = await WindowsSingleVaultService.OpenAsync(bootstrap, token);
                }
            }
            finally { initialisation.Release(); }
            var response = service is null
                ? FluxVaultIpcResponse.Failure("The installation is not initialised.") with { ErrorCode = FluxVaultIpcErrorCode.Denied }
                : await service.HandleAsync(caller, request, token);
            if (response.Success) Interlocked.Increment(ref Completed);
            if (caller.UserSid == fixture.Actors["B"] && response.ErrorCode == FluxVaultIpcErrorCode.Denied && response.Status is null && response.VaultId is null)
                UngrantDenied = true;
            return response;
        }
        public async ValueTask DisposeAsync()
        { try { if (service is not null) await service.DisposeAsync(); } finally { initialisation.Dispose(); } }
    }

    private static void CreateFreshPrivateRoot(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Fixture provisioning refuses existing storage.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(new SecurityIdentifier("S-1-5-18"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);
    }
}
