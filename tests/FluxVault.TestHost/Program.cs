using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Capture;
using FluxVault.Core.Chunking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Ipc;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Testing;
using FluxVault.Windows.Security;
using FluxVault.Core.Security;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace FluxVault.TestHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        try
        {
            var options = Parse(arguments);
            if (Require(options, "mode") == "windows-db-probe")
                return WindowsDatabaseProbe.RunAsync(Require(options, "configuration"), Require(options, "host"),
                    Require(options, "actor"), Guid.ParseExact(Require(options, "probe-id"), "N"), VaultCatalogueProbe.RunAsync).GetAwaiter().GetResult();
            var scratch = StorageOwnership.Canonical(Require(options, "scratch"));
            var allowedParent = Path.Combine(Path.GetTempPath(), "FluxVault.Integrity");
            if (!string.Equals(Path.GetDirectoryName(scratch), allowedParent, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(scratch), "N", out _))
                throw new ArgumentException("A GUID scratch directory under TEMP/FluxVault.Integrity is required.");
            StorageOwnership.RejectReparseComponents(scratch);
            Directory.CreateDirectory(scratch);
            var mode = Require(options, "mode");
            if (mode == "ui") { ShowUi(options, scratch); return 0; }
            if (mode == "ui-save") { ProtectionSaveUiFixture.Show(options, scratch); return 0; }
            return RunAsync(options, scratch, mode).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = exception.Message,
                code = (exception as RepositoryIntegrityException)?.Code.ToString(), detail = exception.ToString() }));
            return 3;
        }
    }

    private static async Task<int> RunAsync(Dictionary<string, string> options, string scratch, string mode)
    {
        var repositoryName = options.GetValueOrDefault("repository-name", "repository");
        RestoreGraphValidator.ValidateChildName(repositoryName);
        var root = Path.Combine(scratch, repositoryName);
        var mirrors = options.GetValueOrDefault("mirrors") == "2"
            ? new MirrorSetConfiguration([new("first", "First", Path.Combine(scratch, "first"), true),
                new("second", "Second", Path.Combine(scratch, "second"), true)]) : MirrorSetConfiguration.CreateDefault();
        RepositoryFaults? faults = null;
        if (options.TryGetValue("gate", out var gate))
        {
            var point = Enum.Parse<RepositoryFaultPoint>(gate);
            faults = new((hit, _) => { if (hit == point) SignalAndHold(scratch); });
        }
        if (options.GetValueOrDefault("fault") == "hint-failure")
            faults = new((hit, _) => { if (hit == RepositoryFaultPoint.BeforeRestoreHint) throw new IOException("fixture hint failure"); });
        var compression = Enum.Parse<CompressionPreference>(options.GetValueOrDefault("compression", "Off"));
        IRepositoryMetadataStore? metadata = options.ContainsKey("db-port") ? CreateMetadata(options, scratch) : null;
        var repo = new FileSystemChunkRepository(root, new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)),
            new Blake3ContentHasher(), new ZstdChunkCodec(), mirrors, metadata, null, faults);
        switch (mode)
        {
            case "hold":
                await using (await RepositoryLeaseSet.AcquireAsync(root, mirrors.EnabledNodes.Select(node => node.Path).ToArray(),
                    mirrors.EnabledNodes.Count == 0 ? MirrorLeaseMode.None : MirrorLeaseMode.Required, default)) SignalAndHold(scratch);
                break;
            case "capture":
                var source = FixturePath(scratch, Require(options, "source"));
                var result = await repo.CommitAsync(new("fixture", source, DateTimeOffset.UtcNow, CaptureConsistency.BestEffort,
                    compression, 1, File.OpenRead(source)));
                Console.WriteLine(JsonSerializer.Serialize(result));
                break;
            case "restore":
                Console.WriteLine(JsonSerializer.Serialize(await repo.RestoreAsync(Require(options, "version"), FixturePath(scratch, Require(options, "output")))));
                break;
            case "drain":
                Console.WriteLine(JsonSerializer.Serialize(await repo.RunMirrorDrainAsync("first")));
                break;
            case "repair":
                Console.WriteLine(JsonSerializer.Serialize(await repo.RunMirrorRepairAsync()));
                break;
            case "purge":
                Console.WriteLine(JsonSerializer.Serialize(await repo.PurgeAsync(new([new(scratch, RepositoryPurgeScopeKind.RecursiveFolder)]))));
                break;
            case "retention":
                Console.WriteLine(JsonSerializer.Serialize(await repo.ApplyRetentionAsync(new(true, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1), DateTimeOffset.UtcNow)));
                break;
            case "serve":
                var pipe = PrivatePipe(options);
                var store = CreateMetadata(options, scratch);
                await store.InitializeAsync();
                var working = Path.Combine(scratch, "working");
                Directory.CreateDirectory(working);
                var configuration = FluxVaultConfiguration.CreateDefault(scratch) with
                {
                    RepositoryPath = root, MirrorPath = null, MirrorSet = mirrors, IsEnabled = true,
                    WatchedFolders = [new("fixture", working, true, ["*"], [], compression, ResourceProfile.Fast, true)],
                    MetadataStore = MetadataConfiguration(options, scratch)
                };
                var operations = new FluxVaultOperations(new FixtureConfigurationStore(configuration), new NormalFileCaptureProvider(),
                    maintenanceStateRoot: Path.Combine(scratch, "state"), metadataStoreFactory: _ => store,
                    repositoryFactory: _ => new FileSystemChunkRepository(root, new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)),
                        new Blake3ContentHasher(), new ZstdChunkCodec(), mirrors, store, null, faults));
                var server = CreatePrivateServer(pipe, operations, scratch);
                var serving = server.RunAsync(CancellationToken.None);
                Console.WriteLine("READY");
                await serving;
                break;
            default: throw new ArgumentException("Unknown fixture mode.");
        }
        return 0;
    }

    private static void SignalAndHold(string scratch)
    {
        File.WriteAllText(Path.Combine(scratch, "gate.signal"), Environment.ProcessId.ToString());
        Console.WriteLine("GATE");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite); // The process fixture deliberately kills this owner.
    }

    private static PostgreSqlRepositoryMetadataStore CreateMetadata(Dictionary<string, string> options, string scratch)
    {
        var configuration = MetadataConfiguration(options, scratch);
        var owner = OwnedPostgreSqlCluster.Load();
        if (configuration.Port != owner.Port) throw new ArgumentException("Fixture port does not belong to the supplied test cluster.");
        owner.VerifyAsync(configuration.DatabaseName).GetAwaiter().GetResult();
        return new(configuration, "fixture-device");
    }

    private static MetadataStoreConfiguration MetadataConfiguration(Dictionary<string, string> options, string scratch)
    {
        var port = int.Parse(Require(options, "db-port"));
        var database = Require(options, "database");
        if (port <= 1024 || port == 5432 || !database.StartsWith("fv_test_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(database[8..], "N", out _)) throw new ArgumentException("Explicit disposable PostgreSQL endpoint required.");
        return MetadataStoreConfiguration.CreateDefault(scratch) with
        { Host = "127.0.0.1", Port = port, DatabaseName = database, Username = "fv_test", ServiceName = "fixture-only" };
    }

    private static string PrivatePipe(Dictionary<string, string> options)
    {
        var pipe = Require(options, "pipe");
        const string prefix = "FluxVault.Integrity.";
        if (!pipe.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(pipe[prefix.Length..], "N", out _))
            throw new ArgumentException("A private GUID-named fixture pipe is required.");
        return pipe;
    }

    private static string FixturePath(string scratch, string path)
    {
        var full = Path.GetFullPath(Path.Combine(scratch, path));
        if (!StorageOwnership.Contains(scratch, full)) throw new ArgumentException("Fixture path escaped its scratch root.");
        StorageOwnership.RejectReparseComponents(full);
        return full;
    }

    private static void ShowUi(Dictionary<string, string> options, string scratch)
    {
        var client = CreatePrivateClient(PrivatePipe(options));
        var application = new System.Windows.Application();
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromSeconds(2),
            new FileBrowserViewModel(new FixtureFileSystem(scratch)), new FixtureServiceController(),
            new FixtureDestinationPicker(scratch), new FixtureOverwriteConfirmation(), versionPreviewLauncher: new FixturePreviewLauncher(scratch));
        var window = new FluxVault.App.MainWindow(new FileDataGridLayoutStore(Path.Combine(scratch, "ui-layout.json")))
        { DataContext = viewModel, Title = "FluxVault integrity fixture" };
        window.Loaded += async (_, _) => await viewModel.RefreshAsync();
        application.Run(window);
    }

    private static string Require(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"--{name} is required.");

    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Only explicit --name value arguments are accepted.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Named fixture arguments required.");
            options.Add(args[index][2..], args[index + 1]);
        }
        return options;
    }

    private sealed class FixtureConfigurationStore(FluxVaultConfiguration configuration) : IFluxVaultConfigurationStore
    {
        public Task<FluxVaultConfiguration> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(configuration);
        public Task SaveAsync(FluxVaultConfiguration value, CancellationToken cancellationToken = default) => throw new NotSupportedException("Fixture configuration is fixed.");
    }
    [SupportedOSPlatform("windows")]
    private static NamedPipeFluxVaultServer CreatePrivateServer(string pipe, FluxVaultOperations operations, string scratch)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new(new FixtureHandler(operations, scratch, identity.User!.Value),
            WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeFluxVaultClient CreatePrivateClient(string pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(pipe, identity.User!.Value));
    }

    private sealed class FixtureHandler(FluxVaultOperations operations, string scratch, string ownerSid) : IAuthenticatedFluxVaultRequestHandler
    {
        public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            if (caller.UserSid != ownerSid || !caller.ImpersonationPermitted)
                return FluxVaultIpcResponse.Failure("Access to the fixture is denied.");
            if (request.Command is not (FluxVaultIpcCommand.GetStatus or FluxVaultIpcCommand.GetActivity or FluxVaultIpcCommand.GetPerformance or
                FluxVaultIpcCommand.ListBlockedFiles or FluxVaultIpcCommand.GetSyncStatus or FluxVaultIpcCommand.GetRepositoryHealth or
                FluxVaultIpcCommand.RunBackupNow or FluxVaultIpcCommand.ListVersions or FluxVaultIpcCommand.InspectVersion or
                FluxVaultIpcCommand.RestoreVersion or FluxVaultIpcCommand.RestoreVersionPreview or FluxVaultIpcCommand.PreviewRestoreSelection or
                FluxVaultIpcCommand.RunRestoreSelection or FluxVaultIpcCommand.PreviewRetention or FluxVaultIpcCommand.RunRetentionNow or
                FluxVaultIpcCommand.PreviewMirrorRepair or FluxVaultIpcCommand.RunMirrorRepair or FluxVaultIpcCommand.RunRepositoryScrub or
                FluxVaultIpcCommand.PreviewMirrorDrain or FluxVaultIpcCommand.RunMirrorDrain or FluxVaultIpcCommand.PreviewMirrorRebalance or
                FluxVaultIpcCommand.RunMirrorRebalance or FluxVaultIpcCommand.RunRestoreRehearsal))
                return FluxVaultIpcResponse.Failure("Command is outside the isolated fixture scope.");
            if (request.SourcePath is not null) FixturePath(scratch, request.SourcePath);
            if (request.Configuration is not null) return FluxVaultIpcResponse.Failure("Fixture configuration cannot be changed.");
            if (request.OutputPath is not null) FixturePath(scratch, request.OutputPath);
            if (request.DestinationPath is not null) FixturePath(scratch, request.DestinationPath);
            try { return await operations.HandleAsync(request, cancellationToken); }
            catch (IOException exception)
            {
                // This explicit same-owner fixture validates all paths above before exposing a domain error.
                return FluxVaultIpcResponse.Failure(exception.Message);
            }
        }
    }
    private sealed class FixturePreviewLauncher(string scratch) : IVersionPreviewLauncher
    {
        public void OpenFile(string filePath)
        {
            var path = FixturePath(scratch, filePath);
            if (!File.Exists(path)) throw new IOException("Preview output is missing.");
            File.WriteAllText(Path.Combine(scratch, "preview-request.json"), JsonSerializer.Serialize(new { path }));
        }
    }
    private sealed class FixtureDestinationPicker(string scratch) : IRestoreDestinationPicker
    {
        public string? PickDestination(VersionRow version) => Path.Combine(scratch, "restored", Path.GetFileName(version.SourcePath));
        public string? PickFolderDestination(string sourcePath) => Path.Combine(scratch, "restored", "recovered-folder");
    }
    private sealed class FixtureOverwriteConfirmation : IRestoreOverwriteConfirmation { public bool ConfirmOverwrite(string destinationPath) => true; }
    private sealed class FixtureServiceController : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FluxVaultWindowsServiceStatus("Private fixture", FluxVaultWindowsServiceState.Unknown, "No installed service is controlled."));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FixtureFileSystem(string scratch) : IFileBrowserFileSystem
    {
        private readonly WindowsFileBrowserFileSystem inner = new();
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots() => [new(scratch, "Disposable fixture", true, null)];
        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path) => inner.GetChildFolders(FixturePath(scratch, path));
        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path) => inner.GetFiles(FixturePath(scratch, path));
    }
}
