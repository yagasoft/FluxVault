using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Capture;
using FluxVault.Core.ChangeTracking;
using FluxVault.Core.Chunking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;
using Microsoft.Extensions.Logging;

namespace FluxVault.Integration.Tests;

public sealed class ProtectionLoopUsnTests
{
    [Fact]
    public async Task Usn_cycle_backs_up_only_changed_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var changed = Path.Combine(watched, "changed.txt");
        var unchanged = Path.Combine(watched, "unchanged.txt");
        await File.WriteAllTextAsync(changed, "changed content");
        await File.WriteAllTextAsync(unchanged, "unchanged content");
        var configuration = NewConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        var loop = CreateLoop(
            workspace,
            operations,
            UsnChangeJournalReadResult.Active(
                "USN active.",
                [new UsnChangedFile("docs", changed, 1, false)],
                [new UsnJournalCheckpoint("docs", Path.GetPathRoot(watched)!, 1, 200, 1, DateTimeOffset.UtcNow)]));

        await loop.RunCatchUpCycleAsync();

        var version = Assert.Single(await operations.ListVersionsAsync());
        Assert.Equal(changed, version.SourcePath);
        var status = await operations.GetStatusAsync();
        Assert.NotNull(status.DurableChange);
        Assert.Equal("USN active.", status.DurableChange.Status);
    }

    [Fact]
    public async Task Usn_unavailable_cycle_falls_back_to_full_scan()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        await File.WriteAllTextAsync(Path.Combine(watched, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(watched, "second.txt"), "second");
        var configuration = NewConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        var loop = CreateLoop(
            workspace,
            operations,
            UsnChangeJournalReadResult.Unavailable("USN journal cannot be queried."));

        await loop.RunCatchUpCycleAsync();

        var versions = await operations.ListVersionsAsync();
        Assert.Equal(2, versions.Count);
        var status = await operations.GetStatusAsync();
        Assert.NotNull(status.DurableChange);
        Assert.Contains("unavailable", status.DurableChange.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USN journal cannot be queried", status.DurableChange.FallbackReason);
    }

    [Fact]
    public async Task Usn_exception_cycle_logs_warning_and_falls_back_to_full_scan()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        await File.WriteAllTextAsync(Path.Combine(watched, "first.txt"), "first");
        var configuration = NewConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        var logger = new CapturingLogger<FileSystemProtectionLoop>();
        var loop = CreateLoop(workspace, operations, new ThrowingUsnChangeJournalReader(new InvalidDataException("USN failed.")), logger);

        await loop.RunCatchUpCycleAsync();

        Assert.Single(await operations.ListVersionsAsync());
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("USN catch-up cycle failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Repeated_usn_full_scan_requirement_is_cooled_down_after_initial_fallback()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        await File.WriteAllTextAsync(Path.Combine(watched, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(watched, "second.txt"), "second");
        var configuration = NewFastConfiguration(workspace, watched) with
        {
            CaptureCadencePolicy = NewFastConfiguration(workspace, watched).CaptureCadencePolicy with
            {
                UsnFallbackFullScanCooldown = TimeSpan.FromHours(1)
            }
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var captureProvider = new CountingCaptureProvider();
        var operations = CreateOperations(workspace, configuration, store, captureProvider);
        await operations.SaveConfigurationAsync(configuration);
        var reader = new SequencedUsnChangeJournalReader(
            UsnChangeJournalReadResult.FullScanRequired("USN journal reset.", [Checkpoint(watched, nextUsn: 200)]),
            UsnChangeJournalReadResult.FullScanRequired("USN journal reset.", [Checkpoint(watched, nextUsn: 300)]),
            UsnChangeJournalReadResult.FullScanRequired("USN journal reset.", [Checkpoint(watched, nextUsn: 400)]));
        var loop = CreateLoop(workspace, operations, reader);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var loopTask = loop.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(_ => Task.FromResult(reader.ReadCount >= 1), cancellation.Token);
            loop.RecordFileSystemEventForTesting(configuration.WatchedFolders[0], Path.Combine(watched, "first.txt"));
            await WaitUntilAsync(_ => Task.FromResult(reader.ReadCount >= 2), cancellation.Token);
            await Task.Delay(150, cancellation.Token);
        }
        finally
        {
            await StopLoopAsync(loopTask, cancellation);
        }

        Assert.Equal(2, captureProvider.TotalCaptures);
        var status = await operations.GetStatusAsync();
        Assert.NotNull(status.BackupRuntime);
        Assert.True(status.BackupRuntime.SuppressedFullScanCount >= 1);
        Assert.NotNull(status.BackupRuntime.NextFallbackScanUtc);
    }

    [Fact]
    public async Task Watcher_due_change_runs_usn_catch_up_then_backs_up_watcher_path_when_usn_is_empty()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var changed = Path.Combine(watched, "changed.txt");
        var configuration = NewFastConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        var reader = new SequencedUsnChangeJournalReader(
            NoChangedFiles(watched),
            NoChangedFiles(watched),
            NoChangedFiles(watched));
        var loop = CreateLoop(workspace, operations, reader);

        await RunLoopUntilAsync(
            loop,
            reader,
            async token =>
            {
                await File.WriteAllTextAsync(changed, "watcher fallback", token);
            },
            async token => reader.ReadCount >= 3 && (await operations.ListVersionsAsync(token)).Count == 1);

        var version = Assert.Single(await operations.ListVersionsAsync());
        Assert.Equal(changed, version.SourcePath);
        Assert.True(reader.ReadCount >= 3);
    }

    [Fact]
    public async Task Watcher_due_change_reported_by_usn_is_backed_up_once()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var changed = Path.Combine(watched, "changed.txt");
        var configuration = NewFastConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        var reader = new SequencedUsnChangeJournalReader(
            NoChangedFiles(watched),
            NoChangedFiles(watched),
            UsnChangeJournalReadResult.Active(
                "USN active. Found 1 changed file(s).",
                [new UsnChangedFile("docs", changed, 1, false)],
                [Checkpoint(watched, nextUsn: 300)]));
        var loop = CreateLoop(workspace, operations, reader);

        await RunLoopUntilAsync(
            loop,
            reader,
            async token =>
            {
                await File.WriteAllTextAsync(changed, "usn version", token);
            },
            async token => reader.ReadCount >= 3 && (await operations.ListVersionsAsync(token)).Count >= 1);

        var versions = await operations.ListVersionsAsync();
        Assert.Single(versions);
        Assert.Equal(changed, versions[0].SourcePath);
        Assert.True(reader.ReadCount >= 3);
    }

    [Fact]
    public async Task Watcher_due_change_with_usn_unavailable_runs_full_scan_without_duplicate_targeted_backup()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var existing = Path.Combine(watched, "existing.txt");
        var changed = Path.Combine(watched, "changed.txt");
        await File.WriteAllTextAsync(existing, "already there");
        var configuration = NewFastConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        var reader = new SequencedUsnChangeJournalReader(
            NoChangedFiles(watched),
            NoChangedFiles(watched),
            UsnChangeJournalReadResult.Unavailable("USN journal cannot be queried."));
        var loop = CreateLoop(workspace, operations, reader);

        await RunLoopUntilAsync(
            loop,
            reader,
            async token =>
            {
                await File.WriteAllTextAsync(changed, "requires scan", token);
            },
            async token => reader.ReadCount >= 3 && (await operations.ListVersionsAsync(token)).Count >= 2);

        var versions = await operations.ListVersionsAsync();
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, version => version.SourcePath == existing);
        Assert.Contains(versions, version => version.SourcePath == changed);
    }

    [Fact]
    public async Task Noisy_watcher_events_collapse_to_one_reconciliation_scan()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var files = Enumerable.Range(0, 30)
            .Select(index => Path.Combine(watched, $"file-{index:00}.txt"))
            .ToArray();
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(file, Path.GetFileName(file));
        }

        var configuration = NewFastConfiguration(workspace, watched);
        configuration = configuration with
        {
            CaptureCadencePolicy = configuration.CaptureCadencePolicy with
            {
                WatcherEventBacklogLimit = 16
            }
        };
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        var reader = new SequencedUsnChangeJournalReader(
            NoChangedFiles(watched),
            NoChangedFiles(watched),
            NoChangedFiles(watched));
        var loop = CreateLoop(workspace, operations, reader);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var loopTask = loop.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(_ => Task.FromResult(reader.ReadCount >= 2), cancellation.Token);
            var folder = Assert.Single(configuration.WatchedFolders);
            foreach (var file in files)
            {
                loop.RecordFileSystemEventForTesting(folder, file);
            }

            await WaitUntilAsync(async token => (await operations.ListVersionsAsync(token)).Count == files.Length, cancellation.Token);
        }
        finally
        {
            await StopLoopAsync(loopTask, cancellation);
        }

        var versions = await operations.ListVersionsAsync();
        Assert.Equal(files.Length, versions.Count);
        var status = await operations.GetStatusAsync();
        var watcher = Assert.Single(status.Watchers!);
        Assert.Equal("Reconciliation scan", watcher.LastCatchUpSource);
        Assert.Equal(0, watcher.BacklogCount);
        Assert.True(watcher.EventsPerMinute >= 16);
    }

    private static FileSystemProtectionLoop CreateLoop(
        TemporaryWorkspace workspace,
        FluxVaultOperations operations,
        UsnChangeJournalReadResult result)
    {
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var checkpointStore = new FileUsnJournalCheckpointStore(Path.Combine(workspace.RootPath, "state", "usn-checkpoints.json"));
        return new FileSystemProtectionLoop(
            operations,
            store,
            new UsnCatchUpService(new FakeUsnChangeJournalReader(result), checkpointStore));
    }

    private static FileSystemProtectionLoop CreateLoop(
        TemporaryWorkspace workspace,
        FluxVaultOperations operations,
        IUsnChangeJournalReader reader)
    {
        return CreateLoop(workspace, operations, reader, logger: null);
    }

    private static FileSystemProtectionLoop CreateLoop(
        TemporaryWorkspace workspace,
        FluxVaultOperations operations,
        IUsnChangeJournalReader reader,
        ILogger<FileSystemProtectionLoop>? logger)
    {
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var checkpointStore = new FileUsnJournalCheckpointStore(Path.Combine(workspace.RootPath, "state", "usn-checkpoints.json"));
        return new FileSystemProtectionLoop(
            operations,
            store,
            new UsnCatchUpService(reader, checkpointStore),
            logger);
    }

    private static FluxVaultOperations CreateOperations(
        TemporaryWorkspace workspace,
        FluxVaultConfiguration configuration)
    {
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        return CreateOperations(
            workspace,
            configuration,
            store,
            new FallbackFileCaptureProvider(new NormalFileCaptureProvider(), new UnavailableVssCaptureProvider()));
    }

    private static FluxVaultOperations CreateOperations(
        TemporaryWorkspace workspace,
        FluxVaultConfiguration configuration,
        FileFluxVaultConfigurationStore store,
        IFileCaptureProvider captureProvider)
    {
        var metadataStore = new InMemoryRepositoryMetadataStore();
        return new FluxVaultOperations(
            store,
            captureProvider,
            metadataStoreFactory: _ => metadataStore,
            repositoryFactory: repositoryConfiguration => CreateRepository(repositoryConfiguration, metadataStore));
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

    private static FluxVaultConfiguration NewConfiguration(TemporaryWorkspace workspace, string watched)
    {
        return new FluxVaultConfiguration(
            RepositoryPath: workspace.RepositoryPath,
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders:
            [
                new WatchedFolderConfiguration(
                    Id: "docs",
                    Path: watched,
                    Recursive: true,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    Compression: CompressionPreference.Zstd,
                    ResourceProfile: ResourceProfile.Fast,
                    IsEnabled: true)
            ]);
    }

    private static FluxVaultConfiguration NewFastConfiguration(TemporaryWorkspace workspace, string watched)
    {
        return NewConfiguration(workspace, watched) with
        {
            CaptureCadencePolicy = new CaptureCadencePolicy(
                WatcherPollInterval: TimeSpan.FromMilliseconds(25),
                PeriodicReconciliationInterval: TimeSpan.FromMinutes(30),
                FastDebounce: TimeSpan.Zero,
                BalancedDebounce: TimeSpan.Zero,
                QuietDebounce: TimeSpan.Zero,
                FastMaxHotFileDelay: TimeSpan.FromSeconds(1),
                BalancedMaxHotFileDelay: TimeSpan.FromSeconds(1),
                QuietMaxHotFileDelay: TimeSpan.FromSeconds(1),
                MinimumSameFileCaptureInterval: TimeSpan.Zero,
                MaximumConcurrentCaptures: 1)
        };
    }

    private static UsnChangeJournalReadResult NoChangedFiles(string watched)
    {
        return UsnChangeJournalReadResult.Active(
            "USN active. Found 0 changed file(s).",
            [],
            [Checkpoint(watched, nextUsn: 200)]);
    }

    private static UsnJournalCheckpoint Checkpoint(string watched, long nextUsn)
    {
        return new UsnJournalCheckpoint("docs", Path.GetPathRoot(watched)!, 1, nextUsn, 1, DateTimeOffset.UtcNow);
    }

    private static async Task RunLoopUntilAsync(
        FileSystemProtectionLoop loop,
        SequencedUsnChangeJournalReader reader,
        Func<CancellationToken, Task> arrangeAfterInitialCatchUp,
        Func<CancellationToken, Task<bool>> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var loopTask = loop.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(_ => Task.FromResult(reader.ReadCount >= 2), cancellation.Token);
            await arrangeAfterInitialCatchUp(cancellation.Token);
            await WaitUntilAsync(condition, cancellation.Token);
        }
        finally
        {
            await StopLoopAsync(loopTask, cancellation);
        }
    }

    private static async Task WaitUntilAsync(Func<CancellationToken, Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition(cancellationToken).ConfigureAwait(false))
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task StopLoopAsync(Task loopTask, CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class FakeUsnChangeJournalReader(UsnChangeJournalReadResult result) : IUsnChangeJournalReader
    {
        public Task<UsnChangeJournalReadResult> ReadChangesAsync(
            IReadOnlyList<UsnWatchedFolderScope> watchedFolders,
            IReadOnlyList<UsnJournalCheckpoint> checkpoints,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }

    private sealed class SequencedUsnChangeJournalReader(params UsnChangeJournalReadResult[] results) : IUsnChangeJournalReader
    {
        private readonly Queue<UsnChangeJournalReadResult> results = new(results);
        private readonly Lock gate = new();

        public int ReadCount { get; private set; }

        public Task<UsnChangeJournalReadResult> ReadChangesAsync(
            IReadOnlyList<UsnWatchedFolderScope> watchedFolders,
            IReadOnlyList<UsnJournalCheckpoint> checkpoints,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                ReadCount++;
                return Task.FromResult(results.Count == 0
                    ? UsnChangeJournalReadResult.Active("USN active. Found 0 changed file(s).", [], checkpoints)
                    : results.Dequeue());
            }
        }
    }

    private sealed class ThrowingUsnChangeJournalReader(Exception exception) : IUsnChangeJournalReader
    {
        public Task<UsnChangeJournalReadResult> ReadChangesAsync(
            IReadOnlyList<UsnWatchedFolderScope> watchedFolders,
            IReadOnlyList<UsnJournalCheckpoint> checkpoints,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<UsnChangeJournalReadResult>(exception);
        }
    }

    private sealed class CountingCaptureProvider : IFileCaptureProvider
    {
        private int totalCaptures;

        public int TotalCaptures => totalCaptures;

        public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref totalCaptures);
            var payload = File.ReadAllBytes(request.SourcePath);
            return Task.FromResult(FileCaptureResult.Captured(
                new MemoryStream(payload),
                FluxVault.Abstractions.Storage.CaptureConsistency.BestEffort,
                "Captured."));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
