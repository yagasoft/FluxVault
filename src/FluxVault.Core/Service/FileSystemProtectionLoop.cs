using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Configuration;
using FluxVault.Core.ChangeTracking;
using FluxVault.Core.Policies;
using Microsoft.Extensions.Logging;

namespace FluxVault.Core.Service;

public sealed class FileSystemProtectionLoop(
    FluxVaultOperations operations,
    IFluxVaultConfigurationStore configurationStore,
    UsnCatchUpService usnCatchUpService,
    ILogger<FileSystemProtectionLoop>? logger = null,
    ProtectionRuntimeCoordinator? runtimeCoordinator = null)
{
    private readonly ProtectionRuntimeCoordinator protectionRuntimeCoordinator = runtimeCoordinator ?? new ProtectionRuntimeCoordinator();
    private readonly Lock gate = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private DateTimeOffset lastFullScanUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastUsnFallbackFullScanUtc = DateTimeOffset.MinValue;
    private string watcherSignature = string.Empty;
    private bool pendingCatchUp = true;
    private bool pendingReconciliationScan;
    private int watcherEventBacklogLimit = CaptureCadencePolicy.CreateDefault().WatcherEventBacklogLimit;
    private readonly Dictionary<string, PendingFileChange> pendingFileChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lastCaptureAttemptByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WatcherEventCounter> watcherEventsByFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> watchedFolderPaths = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await RebuildWatchersAsync(cancellationToken).ConfigureAwait(false);
        var initialCatchUp = await RunCatchUpCycleCoreAsync(cancellationToken).ConfigureAwait(false);
        lastFullScanUtc = DateTimeOffset.UtcNow;
        pendingCatchUp = !initialCatchUp.RanFullScan;

        while (!cancellationToken.IsCancellationRequested)
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var cadence = configuration.CaptureCadencePolicy;
            await protectionRuntimeCoordinator.WaitForConfigurationChangeOrDelayAsync(cadence.WatcherPollInterval, cancellationToken)
                .ConfigureAwait(false);
            ClearRemovedScopes(protectionRuntimeCoordinator.TakeRemovedScopes());
            await RebuildWatchersAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var dueChanges = TakeDueChanges(cadence, now);
            if (TakePendingReconciliationScan())
            {
                RecordCatchUpSource("Reconciliation scan", overflowed: false);
                await operations.RunBackupNowAsync(cancellationToken).ConfigureAwait(false);
                lastFullScanUtc = now;
                pendingCatchUp = false;
                RecordCatchUpSource("Reconciliation scan", overflowed: false);
                continue;
            }

            var shouldCatchUp = dueChanges.Count > 0 || pendingCatchUp;
            var shouldReconcile = now - lastFullScanUtc >= cadence.PeriodicReconciliationInterval;
            if (!shouldCatchUp && !shouldReconcile)
            {
                continue;
            }

            var catchUpOutcome = ProtectionLoopCatchUpOutcome.None;
            if (shouldCatchUp)
            {
                catchUpOutcome = await RunCatchUpCycleCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            if (dueChanges.Count > 0 && !catchUpOutcome.RanFullScan)
            {
                var duePaths = dueChanges
                    .Select(change => change.SourcePath)
                    .Where(path => !catchUpOutcome.BackedUpPaths.Contains(Path.GetFullPath(path)))
                    .ToArray();
                if (duePaths.Length > 0)
                {
                    await operations.RunBackupForFilesAsync(duePaths, cancellationToken)
                        .ConfigureAwait(false);
                    RecordCatchUpSource("Watcher fallback", overflowed: false);
                }

                now = DateTimeOffset.UtcNow;
                foreach (var change in dueChanges)
                {
                    lastCaptureAttemptByPath[change.SourcePath] = now;
                }
            }

            if (shouldReconcile)
            {
                await operations.RunBackupNowAsync(cancellationToken).ConfigureAwait(false);
                lastFullScanUtc = now;
                RecordCatchUpSource("Reconciliation scan", overflowed: false);
            }

            pendingCatchUp = false;
        }
    }

    public async Task RunCatchUpCycleAsync(CancellationToken cancellationToken = default)
    {
        _ = await RunCatchUpCycleCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProtectionLoopCatchUpOutcome> RunCatchUpCycleCoreAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await usnCatchUpService.CatchUpAsync(configuration, cancellationToken).ConfigureAwait(false);
            operations.UpdateDurableChangeStatus(result.Status);
            if (result.RequiresFullScan)
            {
                var now = DateTimeOffset.UtcNow;
                var fallbackReason = result.Status.FallbackReason ?? result.Status.Status;
                var cooldown = configuration.CaptureCadencePolicy.UsnFallbackFullScanCooldown;
                var nextAllowedScanUtc = lastUsnFallbackFullScanUtc == DateTimeOffset.MinValue
                    ? DateTimeOffset.MinValue
                    : lastUsnFallbackFullScanUtc + cooldown;
                if (nextAllowedScanUtc > now)
                {
                    operations.RecordSuppressedFullScan(fallbackReason, nextAllowedScanUtc);
                    RecordCatchUpSource("USN fallback suppressed", overflowed: false);
                    return ProtectionLoopCatchUpOutcome.SuppressedFullScan(nextAllowedScanUtc);
                }

                await operations.RunBackupNowAsync(cancellationToken).ConfigureAwait(false);
                lastUsnFallbackFullScanUtc = DateTimeOffset.UtcNow;
                lastFullScanUtc = lastUsnFallbackFullScanUtc;
                RecordCatchUpSource("Watcher fallback", overflowed: false);
                return ProtectionLoopCatchUpOutcome.FullScan;
            }

            if (result.ChangedFiles.Count > 0)
            {
                await operations.RunBackupForFilesAsync(result.ChangedFiles, cancellationToken).ConfigureAwait(false);
                RecordCatchUpSource("USN", overflowed: false);
                return ProtectionLoopCatchUpOutcome.Targeted(result.ChangedFiles);
            }

            RecordCatchUpSource("USN", overflowed: false, preserveReconciliation: true);
            return ProtectionLoopCatchUpOutcome.None;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = new DurableChangeDetail(
                WatchedFolderId: null,
                Path: null,
                VolumeRoot: null,
                Operation: "USN catch-up cycle",
                Reason: $"USN catch-up cycle failed: {ex.Message}",
                Win32ErrorCode: null);
            logger?.LogWarning(ex, "USN catch-up cycle failed; FluxVault will run a reconciliation scan.");
            operations.UpdateDurableChangeStatus(new DurableChangeRuntimeStatus(
                DateTimeOffset.UtcNow,
                "USN unavailable.",
                detail.Reason,
                []) with
            {
                Details = [detail]
            });
            var now = DateTimeOffset.UtcNow;
            var cooldown = configuration.CaptureCadencePolicy.UsnFallbackFullScanCooldown;
            var nextAllowedScanUtc = lastUsnFallbackFullScanUtc == DateTimeOffset.MinValue
                ? DateTimeOffset.MinValue
                : lastUsnFallbackFullScanUtc + cooldown;
            if (nextAllowedScanUtc > now)
            {
                operations.RecordSuppressedFullScan(detail.Reason, nextAllowedScanUtc);
                RecordCatchUpSource("USN fallback suppressed", overflowed: false);
                return ProtectionLoopCatchUpOutcome.SuppressedFullScan(nextAllowedScanUtc);
            }

            await operations.RunBackupNowAsync(cancellationToken).ConfigureAwait(false);
            lastUsnFallbackFullScanUtc = DateTimeOffset.UtcNow;
            lastFullScanUtc = lastUsnFallbackFullScanUtc;
            RecordCatchUpSource("Watcher fallback", overflowed: false);
            return ProtectionLoopCatchUpOutcome.FullScan;
        }
    }

    private async Task RebuildWatchersAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var signature = string.Join(
            "|",
            configuration.WatchedFolders
                .Where(folder => folder.IsEnabled)
                .Select(folder => $"{folder.Path}:{folder.Recursive}:{folder.ResourceProfile}")
                .Order(StringComparer.OrdinalIgnoreCase));
        if (string.Equals(signature, watcherSignature, StringComparison.Ordinal))
        {
            watcherEventBacklogLimit = Math.Max(16, configuration.CaptureCadencePolicy.WatcherEventBacklogLimit);
            return;
        }

        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        watchers.Clear();
        watcherSignature = signature;
        watcherEventBacklogLimit = Math.Max(16, configuration.CaptureCadencePolicy.WatcherEventBacklogLimit);
        lock (gate)
        {
            watchedFolderPaths.Clear();
            var enabledFolderIds = configuration.WatchedFolders
                .Where(folder => folder.IsEnabled && Directory.Exists(folder.Path))
                .Select(folder => folder.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var staleFolderId in watcherEventsByFolder.Keys
                         .Where(folderId => !enabledFolderIds.Contains(folderId))
                         .ToArray())
            {
                watcherEventsByFolder.Remove(staleFolderId);
            }

            foreach (var folder in configuration.WatchedFolders.Where(folder => folder.IsEnabled && Directory.Exists(folder.Path)))
            {
                watchedFolderPaths[folder.Id] = Path.GetFullPath(folder.Path);
                if (!watcherEventsByFolder.ContainsKey(folder.Id))
                {
                    watcherEventsByFolder[folder.Id] = new WatcherEventCounter("None");
                }
            }
        }

        foreach (var folder in configuration.WatchedFolders.Where(folder => folder.IsEnabled && Directory.Exists(folder.Path)))
        {
            var watcher = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = folder.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, args) => MarkPending(folder, args);
            watcher.Created += (_, args) => MarkPending(folder, args);
            watcher.Renamed += (_, args) => MarkPending(folder, args);
            watcher.Deleted += (_, args) => MarkPending(folder, args);
            watchers.Add(watcher);
        }

        pendingCatchUp = true;
    }

    private void ClearRemovedScopes(IReadOnlyList<RepositoryPurgeScope> removedScopes)
    {
        if (removedScopes.Count == 0)
        {
            return;
        }

        lock (gate)
        {
            foreach (var path in pendingFileChanges.Keys.ToArray())
            {
                if (removedScopes.Any(scope => ScopeMatchesPath(scope, path, isDirectory: false)))
                {
                    pendingFileChanges.Remove(path);
                }
            }

            foreach (var path in lastCaptureAttemptByPath.Keys.ToArray())
            {
                if (removedScopes.Any(scope => ScopeMatchesPath(scope, path, isDirectory: false)))
                {
                    lastCaptureAttemptByPath.Remove(path);
                }
            }

            foreach (var (folderId, folderPath) in watchedFolderPaths.ToArray())
            {
                if (!removedScopes.Any(scope => ScopeMatchesPath(scope, folderPath, isDirectory: true)))
                {
                    continue;
                }

                watchedFolderPaths.Remove(folderId);
                watcherEventsByFolder.Remove(folderId);
            }

            if (pendingFileChanges.Count == 0)
            {
                pendingReconciliationScan = false;
                pendingCatchUp = false;
            }

            PublishAllWatcherStatuses();
        }
    }

    internal void RecordFileSystemEventForTesting(WatchedFolderConfiguration folder, string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Test event path must include a directory and file name.", nameof(fullPath));
        }

        MarkPending(folder, new FileSystemEventArgs(WatcherChangeTypes.Changed, directory, fileName));
    }

    private void MarkPending(WatchedFolderConfiguration folder, FileSystemEventArgs args)
    {
        var sourcePath = Path.GetFullPath(args.FullPath);
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            pendingCatchUp = true;
            watchedFolderPaths[folder.Id] = Path.GetFullPath(folder.Path);
            var counter = GetWatcherCounter(folder.Id);
            counter.Record(now);
            if (pendingReconciliationScan)
            {
                pendingCatchUp = false;
                counter.IsOverflowed = true;
                PublishWatcherStatus(folder.Id, folder.Path, PendingBacklogCount(folder.Id), counter);
                return;
            }

            if (pendingFileChanges.TryGetValue(sourcePath, out var existing))
            {
                pendingFileChanges[sourcePath] = existing with { LatestEventUtc = now };
            }
            else if (pendingFileChanges.Count >= watcherEventBacklogLimit)
            {
                pendingFileChanges.Clear();
                pendingReconciliationScan = true;
                pendingCatchUp = false;
                counter.IsOverflowed = true;
                PublishWatcherStatus(folder.Id, folder.Path, backlogCount: 0, counter);
                logger?.LogWarning(
                    "Watcher event backlog exceeded {Limit}; FluxVault will collapse pending events into one reconciliation scan.",
                    watcherEventBacklogLimit);
            }
            else
            {
                pendingFileChanges[sourcePath] = new PendingFileChange(
                    sourcePath,
                    folder.Id,
                    folder.ResourceProfile,
                    FirstEventUtc: now,
                    LatestEventUtc: now);
            }

            PublishWatcherStatus(folder.Id, folder.Path, PendingBacklogCount(folder.Id), counter);
        }
    }

    private IReadOnlyList<PendingFileChange> TakeDueChanges(CaptureCadencePolicy cadence, DateTimeOffset now)
    {
        var due = new List<PendingFileChange>();
        lock (gate)
        {
            foreach (var change in pendingFileChanges.Values.ToArray())
            {
                lastCaptureAttemptByPath.TryGetValue(change.SourcePath, out var lastCaptureAttempt);
                var decision = CaptureCadenceScheduler.Evaluate(
                    cadence,
                    change.ResourceProfile,
                    change.FirstEventUtc,
                    change.LatestEventUtc,
                    lastCaptureAttempt == default ? null : lastCaptureAttempt,
                    now);
                if (!decision.ShouldCapture)
                {
                    operations.UpdateCaptureRuntimeStatus(
                        change.SourcePath,
                        change.WatchedFolderId,
                        CaptureRuntimeState.WaitingForQuietWindow,
                        change.LatestEventUtc,
                        decision.NextForcedCaptureUtc,
                        delayReason: decision.DelayReason);
                    continue;
                }

                if (decision.IsForcedHotFileSnapshot)
                {
                    operations.UpdateCaptureRuntimeStatus(
                        change.SourcePath,
                        change.WatchedFolderId,
                        CaptureRuntimeState.ForcedHotFileSnapshot,
                        change.LatestEventUtc,
                        decision.NextForcedCaptureUtc);
                }

                due.Add(change);
                pendingFileChanges.Remove(change.SourcePath);
            }

            PublishAllWatcherStatuses();
        }

        return due;
    }

    private bool TakePendingReconciliationScan()
    {
        lock (gate)
        {
            if (!pendingReconciliationScan)
            {
                return false;
            }

            pendingReconciliationScan = false;
            return true;
        }
    }

    private void RecordCatchUpSource(string source, bool overflowed, bool preserveReconciliation = false)
    {
        lock (gate)
        {
            foreach (var (folderId, counter) in watcherEventsByFolder)
            {
                if (preserveReconciliation
                    && string.Equals(counter.LastCatchUpSource, "Reconciliation scan", StringComparison.Ordinal))
                {
                    continue;
                }

                counter.LastCatchUpSource = source;
                counter.IsOverflowed = overflowed;
                if (watchedFolderPaths.TryGetValue(folderId, out var folderPath))
                {
                    PublishWatcherStatus(folderId, folderPath, PendingBacklogCount(folderId), counter);
                }
            }
        }
    }

    private WatcherEventCounter GetWatcherCounter(string watchedFolderId)
    {
        if (!watcherEventsByFolder.TryGetValue(watchedFolderId, out var counter))
        {
            counter = new WatcherEventCounter("None");
            watcherEventsByFolder[watchedFolderId] = counter;
        }

        return counter;
    }

    private int PendingBacklogCount(string watchedFolderId)
    {
        return pendingFileChanges.Values.Count(change => string.Equals(
            change.WatchedFolderId,
            watchedFolderId,
            StringComparison.OrdinalIgnoreCase));
    }

    private void PublishAllWatcherStatuses()
    {
        foreach (var (folderId, counter) in watcherEventsByFolder)
        {
            if (watchedFolderPaths.TryGetValue(folderId, out var folderPath))
            {
                PublishWatcherStatus(folderId, folderPath, PendingBacklogCount(folderId), counter);
            }
        }
    }

    private void PublishWatcherStatus(
        string watchedFolderId,
        string folderPath,
        int backlogCount,
        WatcherEventCounter counter)
    {
        operations.UpdateWatcherRuntimeStatus(
            watchedFolderId,
            folderPath,
            backlogCount,
            counter.EventsPerMinute(DateTimeOffset.UtcNow),
            counter.LastEventUtc,
            counter.LastCatchUpSource,
            counter.IsOverflowed);
    }

    private sealed record PendingFileChange(
        string SourcePath,
        string WatchedFolderId,
        ResourceProfile ResourceProfile,
        DateTimeOffset FirstEventUtc,
        DateTimeOffset LatestEventUtc);

    private sealed class WatcherEventCounter(string lastCatchUpSource)
    {
        private readonly Queue<DateTimeOffset> eventUtc = new();

        public DateTimeOffset? LastEventUtc { get; private set; }

        public string LastCatchUpSource { get; set; } = lastCatchUpSource;

        public bool IsOverflowed { get; set; }

        public void Record(DateTimeOffset now)
        {
            LastEventUtc = now;
            eventUtc.Enqueue(now);
            Prune(now);
        }

        public double EventsPerMinute(DateTimeOffset now)
        {
            Prune(now);
            return eventUtc.Count;
        }

        private void Prune(DateTimeOffset now)
        {
            var cutoff = now.AddMinutes(-1);
            while (eventUtc.TryPeek(out var next) && next < cutoff)
            {
                eventUtc.Dequeue();
            }
        }
    }

    private sealed record ProtectionLoopCatchUpOutcome(bool RanFullScan, IReadOnlySet<string> BackedUpPaths)
    {
        public static ProtectionLoopCatchUpOutcome None { get; } =
            new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public static ProtectionLoopCatchUpOutcome FullScan { get; } =
            new(true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public static ProtectionLoopCatchUpOutcome SuppressedFullScan(DateTimeOffset nextAllowedScanUtc)
        {
            return new ProtectionLoopCatchUpOutcome(
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        public static ProtectionLoopCatchUpOutcome Targeted(IEnumerable<string> paths)
        {
            return new ProtectionLoopCatchUpOutcome(
                false,
                paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static bool ScopeMatchesPath(RepositoryPurgeScope scope, string path, bool isDirectory)
    {
        return scope.Kind switch
        {
            RepositoryPurgeScopeKind.File => !isDirectory && PathEquals(path, scope.SourcePath),
            RepositoryPurgeScopeKind.ImmediateFiles => isDirectory
                ? PathEquals(path, scope.SourcePath)
                : IsDirectChildFile(path, scope.SourcePath),
            RepositoryPurgeScopeKind.RecursiveFolder => PathEqualsOrUnder(path, scope.SourcePath),
            _ => false
        };
    }

    private static bool PathEqualsOrUnder(string path, string root)
    {
        var trimmedPath = TrimPath(path);
        var trimmedRoot = TrimPath(root);
        return string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase)
               || IsUnderPath(trimmedPath, trimmedRoot);
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(TrimPath(left), TrimPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectChildFile(string filePath, string folderPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
        return !string.IsNullOrWhiteSpace(parent)
               && PathEquals(parent, folderPath);
    }

    private static bool IsUnderPath(string path, string root)
    {
        var rootPrefix = EndsWithDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return trimmed;
    }

    private static bool EndsWithDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
               || path.EndsWith(Path.AltDirectorySeparatorChar);
    }
}
