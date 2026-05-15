using System.Text;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Capture;
using FluxVault.Core.Chunking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class FluxVaultOperationsPerformanceTests
{
    [Fact]
    public async Task Backup_now_respects_maximum_concurrent_capture_workers()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        Directory.CreateDirectory(source);
        for (var index = 0; index < 12; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, $"file-{index:00}.txt"), $"content-{index}");
        }

        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders =
            [
                new WatchedFolderConfiguration(
                    "source",
                    source,
                    Recursive: false,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ],
            CaptureCadencePolicy = CaptureCadencePolicy.CreateDefault() with
            {
                MaximumConcurrentCaptures = 2
            }
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        var captureProvider = new CountingCaptureProvider();
        var operations = CreateOperations(store, captureProvider);

        var summary = await operations.RunBackupNowAsync();

        Assert.True(summary.Success);
        Assert.Equal(12, summary.CapturedFileCount);
        Assert.Equal(12, captureProvider.TotalCaptures);
        Assert.InRange(captureProvider.MaximumActiveCaptures, 1, 2);
    }

    [Fact]
    public async Task Backup_now_skips_unchanged_files_after_initial_capture()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        Directory.CreateDirectory(source);
        for (var index = 0; index < 6; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, $"file-{index:00}.txt"), $"content-{index}");
        }

        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders =
            [
                new WatchedFolderConfiguration(
                    "source",
                    source,
                    Recursive: false,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ],
            CaptureCadencePolicy = CaptureCadencePolicy.CreateDefault() with
            {
                MaximumConcurrentCaptures = 2
            }
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        var captureProvider = new CountingCaptureProvider();
        var operations = CreateOperations(store, captureProvider);

        var first = await operations.RunBackupNowAsync();
        var second = await operations.RunBackupNowAsync();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(6, first.CapturedFileCount);
        Assert.Equal(0, second.CapturedFileCount);
        Assert.Equal(6, second.SkippedUnchangedFileCount);
        Assert.Equal(6, captureProvider.TotalCaptures);
    }

    [Fact]
    public async Task Backup_now_rereads_unchanged_files_after_deep_verification_interval()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "file.txt");
        await File.WriteAllTextAsync(file, "content");

        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders =
            [
                new WatchedFolderConfiguration(
                    "source",
                    source,
                    Recursive: false,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ],
            CaptureCadencePolicy = CaptureCadencePolicy.CreateDefault() with
            {
                SourceDeepVerificationInterval = TimeSpan.FromMilliseconds(1)
            }
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        var captureProvider = new CountingCaptureProvider();
        var operations = CreateOperations(store, captureProvider);

        var first = await operations.RunBackupNowAsync();
        await Task.Delay(20);
        var second = await operations.RunBackupNowAsync();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, first.CapturedFileCount);
        Assert.Equal(1, second.CapturedFileCount);
        Assert.Equal(0, second.SkippedUnchangedFileCount);
        Assert.Equal(2, captureProvider.TotalCaptures);
    }

    [Fact]
    public async Task Folder_restore_latest_elsewhere_preserves_relative_paths_and_uses_latest_versions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        var nested = Path.Combine(source, "nested");
        Directory.CreateDirectory(nested);
        var firstFile = Path.Combine(source, "a.txt");
        var nestedFile = Path.Combine(nested, "b.txt");
        await File.WriteAllTextAsync(firstFile, "old");
        await File.WriteAllTextAsync(nestedFile, "nested");
        var operations = await CreateOperationsAsync(workspace, source, recursive: true);

        var firstSummary = await operations.RunBackupNowAsync();
        Assert.True(firstSummary.Success);
        await Task.Delay(20);
        await File.WriteAllTextAsync(firstFile, "new");
        var secondSummary = await operations.RunBackupNowAsync();
        Assert.True(secondSummary.Success);

        var destination = Path.Combine(workspace.RootPath, "restore");
        var preview = await operations.PreviewRestoreSelectionAsync(
            source,
            isDirectory: true,
            RestoreSelectionDestinationMode.Elsewhere,
            destination);
        Assert.Equal(2, preview.FileCount);
        Assert.Equal(0, preview.ConflictCount);
        Assert.Equal(0, preview.RestoredCount);

        var restore = await operations.RunRestoreSelectionAsync(
            source,
            isDirectory: true,
            RestoreSelectionDestinationMode.Elsewhere,
            destination,
            overwriteConfirmed: false);

        Assert.Equal(2, restore.FileCount);
        Assert.Equal(2, restore.RestoredCount);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")));
        Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "b.txt")));
    }

    [Fact]
    public async Task Restore_selection_refuses_conflicts_until_overwrite_is_confirmed()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        Directory.CreateDirectory(source);
        var sourceFile = Path.Combine(source, "a.txt");
        await File.WriteAllTextAsync(sourceFile, "source");
        var operations = await CreateOperationsAsync(workspace, source, recursive: false);
        var backup = await operations.RunBackupNowAsync();
        Assert.True(backup.Success);
        var destinationFile = Path.Combine(workspace.RootPath, "restore", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        await File.WriteAllTextAsync(destinationFile, "existing");

        var previewResponse = await operations.HandleAsync(FluxVaultIpcRequest.PreviewRestoreSelection(
            sourceFile,
            isDirectory: false,
            RestoreSelectionDestinationMode.Elsewhere,
            destinationFile));
        Assert.True(previewResponse.Success);
        Assert.Equal(1, previewResponse.RestoreSelection?.ConflictCount);

        var deniedResponse = await operations.HandleAsync(FluxVaultIpcRequest.RunRestoreSelection(
            sourceFile,
            isDirectory: false,
            RestoreSelectionDestinationMode.Elsewhere,
            destinationFile,
            overwriteConfirmed: false));
        Assert.False(deniedResponse.Success);
        Assert.Contains("Confirm overwrite", deniedResponse.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("existing", await File.ReadAllTextAsync(destinationFile));

        var confirmedResponse = await operations.HandleAsync(FluxVaultIpcRequest.RunRestoreSelection(
            sourceFile,
            isDirectory: false,
            RestoreSelectionDestinationMode.Elsewhere,
            destinationFile,
            overwriteConfirmed: true));
        Assert.True(confirmedResponse.Success);
        Assert.Equal(1, confirmedResponse.RestoreSelection?.RestoredCount);
        Assert.Equal("source", await File.ReadAllTextAsync(destinationFile));
    }

    [Fact]
    public async Task Save_configuration_with_confirmed_removed_selection_purges_repository_history()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        Directory.CreateDirectory(source);
        var removedFile = Path.Combine(source, "remove.txt");
        await File.WriteAllTextAsync(removedFile, "remove");
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders =
            [
                new WatchedFolderConfiguration(
                    "source",
                    source,
                    Recursive: false,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ],
            SelectionRules =
            [
                new ProtectionSelectionRule(
                    "source",
                    source,
                    ProtectionSelectionMode.ImmediateFiles,
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ]
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        var operations = CreateOperations(store, new CountingCaptureProvider());
        var backup = await operations.RunBackupNowAsync();
        Assert.True(backup.Success);
        Assert.NotEmpty(await operations.ListVersionsAsync());

        var response = await operations.HandleAsync(FluxVaultIpcRequest.SaveConfiguration(
            configuration with
            {
                WatchedFolders = [],
                SelectionRules = []
            },
            purgeRemovedSelections: true,
            removedSelections:
            [
                new RepositoryPurgeScope(source, RepositoryPurgeScopeKind.ImmediateFiles)
            ]));

        Assert.True(response.Success);
        Assert.NotNull(response.Purge);
        Assert.True(response.Purge.PurgedVersionCount >= 1);
        Assert.DoesNotContain(await operations.ListVersionsAsync(), version => version.SourcePath == removedFile);
        var status = await operations.GetStatusAsync(FluxVaultStatusDetailLevel.Full);
        Assert.DoesNotContain(status.TrackedEntries ?? [], entry => entry.SourcePath == removedFile);
    }

    [Fact]
    public async Task Save_configuration_reports_failed_purge_after_saving_removed_selection()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        Directory.CreateDirectory(source);
        var baseline = new ProtectionSelectionRule(
            "source",
            source,
            ProtectionSelectionMode.ImmediateFiles,
            CompressionPreference.Off,
            ResourceProfile.Balanced,
            IsEnabled: true);
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders = ProtectionSelectionCompiler.Compile([baseline]),
            SelectionRules = [baseline]
        };
        var updatedConfiguration = configuration with
        {
            WatchedFolders = [],
            SelectionRules = []
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        var operations = new FluxVaultOperations(
            store,
            new CountingCaptureProvider(),
            repositoryFactory: _ => new ThrowingPurgeRepository());

        var response = await operations.HandleAsync(FluxVaultIpcRequest.SaveConfiguration(
            updatedConfiguration,
            purgeRemovedSelections: true,
            removedSelections:
            [
                new RepositoryPurgeScope(source, RepositoryPurgeScopeKind.ImmediateFiles)
            ]));

        Assert.True(response.Success);
        Assert.NotNull(response.Purge);
        Assert.False(response.Purge!.Success);
        Assert.Contains("metadata offline", response.Purge.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var savedConfiguration = await store.LoadAsync();
        Assert.Empty(savedConfiguration.WatchedFolders);
        Assert.Empty(savedConfiguration.SelectionRules);
        var status = await operations.GetStatusAsync(FluxVaultStatusDetailLevel.Fast);
        Assert.Contains("purge failed", status.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_configuration_with_removed_selection_cancels_active_capture_without_purge()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "remove.txt");
        await File.WriteAllTextAsync(file, "remove");
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders =
            [
                new WatchedFolderConfiguration(
                    "source",
                    source,
                    Recursive: false,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ],
            SelectionRules =
            [
                new ProtectionSelectionRule(
                    "source",
                    source,
                    ProtectionSelectionMode.ImmediateFiles,
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ],
            CaptureCadencePolicy = CaptureCadencePolicy.CreateDefault() with
            {
                MaximumConcurrentCaptures = 1
            }
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        var captureProvider = new CancelAwareCaptureProvider();
        var operations = CreateOperations(store, captureProvider);

        var backupTask = operations.RunBackupNowAsync();
        Assert.Equal(file, await captureProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var response = await operations.HandleAsync(FluxVaultIpcRequest.SaveConfiguration(configuration with
        {
            WatchedFolders = [],
            SelectionRules = []
        }));
        var summary = await backupTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.Success);
        Assert.Null(response.Purge);
        Assert.True(captureProvider.Canceled);
        Assert.True(summary.Success);
        Assert.Equal(0, summary.CapturedFileCount);
        Assert.Equal(0, summary.FailedFileCount);
        Assert.Equal(1, summary.SkippedUnchangedFileCount);
        Assert.Empty(await operations.ListVersionsAsync());
    }

    private static async Task<FluxVaultOperations> CreateOperationsAsync(
        TemporaryWorkspace workspace,
        string source,
        bool recursive)
    {
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders =
            [
                new WatchedFolderConfiguration(
                    "source",
                    source,
                    recursive,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ],
            CaptureCadencePolicy = CaptureCadencePolicy.CreateDefault() with
            {
                MaximumConcurrentCaptures = 2
            }
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        return CreateOperations(store, new NormalFileCaptureProvider());
    }

    private static FluxVaultOperations CreateOperations(
        FileFluxVaultConfigurationStore store,
        IFileCaptureProvider captureProvider)
    {
        var metadataStore = new InMemoryRepositoryMetadataStore();
        return new FluxVaultOperations(
            store,
            captureProvider,
            metadataStoreFactory: _ => metadataStore,
            repositoryFactory: configuration => CreateRepository(configuration, metadataStore));
    }

    private static IChunkRepository CreateRepository(
        FluxVaultConfiguration configuration,
        IRepositoryMetadataStore metadataStore)
    {
        return new FileSystemChunkRepository(
            configuration.RepositoryPath,
            new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            configuration.MirrorSet,
            metadataStore);
    }

    private sealed class CountingCaptureProvider : IFileCaptureProvider
    {
        private int activeCaptures;
        private int maximumActiveCaptures;
        private int totalCaptures;

        public int MaximumActiveCaptures => maximumActiveCaptures;

        public int TotalCaptures => totalCaptures;

        public async Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref activeCaptures);
            try
            {
                Interlocked.Increment(ref totalCaptures);
                UpdateMaximumActiveCaptures(active);
                await Task.Delay(20, cancellationToken);
                var payload = await File.ReadAllBytesAsync(request.SourcePath, cancellationToken);
                return FileCaptureResult.Captured(
                    new MemoryStream(payload),
                    CaptureConsistency.BestEffort,
                    "Captured.");
            }
            finally
            {
                Interlocked.Decrement(ref activeCaptures);
            }
        }

        private void UpdateMaximumActiveCaptures(int active)
        {
            while (true)
            {
                var observed = maximumActiveCaptures;
                if (active <= observed)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref maximumActiveCaptures, active, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class CancelAwareCaptureProvider : IFileCaptureProvider
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Canceled { get; private set; }

        public async Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(Path.GetFullPath(request.SourcePath));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                throw;
            }

            var payload = await File.ReadAllBytesAsync(request.SourcePath, cancellationToken);
            return FileCaptureResult.Captured(
                new MemoryStream(payload),
                CaptureConsistency.BestEffort,
                "Captured.");
        }
    }

    private sealed class ThrowingPurgeRepository : IChunkRepository
    {
        public Task<FileCommitResult> CommitAsync(FileCommitRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RepositoryDeletionResult?> RecordDeletionAsync(RepositoryDeletionRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RepositoryPurgeResult> PurgeAsync(RepositoryPurgeRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("metadata offline");
        }

        public Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task RestorePreviewAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RepositoryScrubReport> ScrubAsync(bool autoRepairFromMirror, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MirrorRepairReport> PreviewMirrorRepairAsync(string? mirrorNodeId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MirrorRepairReport> RunMirrorRepairAsync(string? mirrorNodeId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MirrorRebalancePreviewReport> PreviewMirrorRebalanceAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MirrorRebalancePreviewReport> RunMirrorRebalanceAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MirrorRebalancePreviewReport> PreviewMirrorDrainAsync(string mirrorNodeId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MirrorRebalancePreviewReport> RunMirrorDrainAsync(string mirrorNodeId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RestoreRehearsalReport> RunRestoreRehearsalAsync(string tempRoot, int maxVersions, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RepositoryRetentionPreview> PreviewRetentionAsync(
            RetentionPolicy policy,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RepositoryRetentionResult> ApplyRetentionAsync(
            RetentionPolicy policy,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
