using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Capture;
using FluxVault.Core.Chunking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class OperationsRuntimeStateTests
{
    [Fact]
    public async Task Another_request_observes_running_backup_and_its_joined_result()
    {
        using var fixture = await Fixture.CreateAsync();
        var capture = new BlockingCapture();
        var first = fixture.Operations(capture, fixture.State);
        var second = fixture.Operations(new NormalFileCaptureProvider(), fixture.State);
        var backup = first.RunBackupNowAsync();
        await capture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var running = await second.GetStatusAsync(FluxVaultStatusDetailLevel.Fast);
            Assert.True(running.BackupRuntime!.IsRunning);
            Assert.Equal(1, running.BackupRuntime.ActiveWorkers);
        }
        finally { capture.Release.TrySetResult(); await backup; }
        var completed = await second.GetStatusAsync(FluxVaultStatusDetailLevel.Fast);
        Assert.False(completed.BackupRuntime!.IsRunning);
        Assert.Equal(1, completed.BackupRuntime.CapturedFileCount);
        Assert.NotNull(completed.LastCaptureUtc);
        Assert.Contains("Captured", completed.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Joined_result_and_activity_survive_requests_while_a_fresh_service_runtime_starts_empty()
    {
        using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Operations(new NormalFileCaptureProvider(), fixture.State).RunBackupNowAsync();
        Assert.True(result.Success);
        var next = fixture.Operations(new NormalFileCaptureProvider(), fixture.State);
        var status = await next.GetStatusAsync(FluxVaultStatusDetailLevel.Fast);
        Assert.Equal(1, status.BackupRuntime!.CapturedFileCount);
        Assert.Contains(next.GetActivity(), activity => activity.SourcePath == fixture.SourceFile);
        var unrelated = fixture.Operations(new NormalFileCaptureProvider(), new());
        Assert.Equal(0, (await unrelated.GetStatusAsync(FluxVaultStatusDetailLevel.Fast)).BackupRuntime!.CapturedFileCount);
        Assert.DoesNotContain(unrelated.GetActivity(), activity => activity.SourcePath == fixture.SourceFile);
        Assert.DoesNotContain(unrelated.GetActivity(), activity => activity.Kind == FluxVaultActivityKind.Captured);
    }

    [Fact]
    public async Task A_new_request_rechecks_source_availability_with_its_own_adapter()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = fixture.Operations(new NormalFileCaptureProvider(), fixture.State);
        await first.RunBackupNowAsync();
        var second = fixture.Operations(new NormalFileCaptureProvider(), fixture.State, new DeniedSource());
        var status = await second.GetStatusAsync(FluxVaultStatusDetailLevel.Fast);
        Assert.False(status.WatchedFolders.Single().Exists);
        Assert.Contains("Source unavailable", status.WatchedFolders.Single().Status);
        Assert.Equal(1, status.BackupRuntime!.CapturedFileCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        internal FluxVaultOperationsRuntimeState State { get; } = new();
        internal string SourceFile { get; private set; } = "";
        private FileFluxVaultConfigurationStore store = null!;
        private FileSystemChunkRepository repository = null!;
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                var source = Path.Combine(fixture.workspace.RootPath, "work"); Directory.CreateDirectory(source);
                fixture.SourceFile = Path.Combine(source, "document.txt"); await File.WriteAllTextAsync(fixture.SourceFile, "working document");
                fixture.store = new(Path.Combine(fixture.workspace.RootPath, "configuration.json"), fixture.workspace.RootPath);
                var configuration = FluxVaultConfiguration.CreateDefault(fixture.workspace.RootPath);
                await fixture.store.SaveAsync(configuration with { RepositoryPath = fixture.workspace.RepositoryPath,
                    RetentionPolicy = configuration.RetentionPolicy with { IsEnabled = false },
                    WatchedFolders = [new("work", source, true, ["*.txt"], [], FluxVault.Abstractions.Policies.CompressionPreference.Off,
                        FluxVault.Abstractions.Policies.ResourceProfile.Balanced, true)] });
                fixture.repository = new(fixture.workspace.RepositoryPath, new FastCdcChunker(new(128, 256, 512)), new Blake3ContentHasher(), new ZstdChunkCodec());
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal FluxVaultOperations Operations(IFileCaptureProvider capture, FluxVaultOperationsRuntimeState state, IProtectionSourceAccess? source = null) =>
            new(store, capture, metadataStoreFactory: _ => new InMemoryRepositoryMetadataStore(), repositoryFactory: _ => repository,
                sourceAccess: source, runtimeState: state);
        public void Dispose() => workspace.Dispose();
    }
    private sealed class BlockingCapture : IFileCaptureProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return FileCaptureResult.Captured(File.OpenRead(request.SourcePath), CaptureConsistency.BestEffort, "test"); }
    }
    private sealed class DeniedSource : IProtectionSourceAccess
    {
        public ProtectionSourceInspection Inspect(string root, string path, RepositoryEntryKind kind, CancellationToken cancellationToken = default) =>
            new(ProtectionSourceAvailability.Unavailable, FailureReason: "this caller cannot read the root");
        public IEnumerable<ProtectionSourceCandidate> EnumerateDirectory(string root, string directory, CancellationToken cancellationToken = default) => throw new UnauthorizedAccessException();
    }
}
