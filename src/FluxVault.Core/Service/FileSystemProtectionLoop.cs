using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Configuration;
using FluxVault.Core.ChangeTracking;
using FluxVault.Core.Policies;

namespace FluxVault.Core.Service;

public sealed class FileSystemProtectionLoop(
    FluxVaultOperations operations,
    IFluxVaultConfigurationStore configurationStore,
    UsnCatchUpService usnCatchUpService)
{
    private readonly Lock gate = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private DateTimeOffset lastFullScanUtc = DateTimeOffset.MinValue;
    private string watcherSignature = string.Empty;
    private bool pendingCatchUp = true;
    private readonly Dictionary<string, PendingFileChange> pendingFileChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lastCaptureAttemptByPath = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await RebuildWatchersAsync(cancellationToken).ConfigureAwait(false);
        await RunCatchUpCycleAsync(cancellationToken).ConfigureAwait(false);
        lastFullScanUtc = DateTimeOffset.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var cadence = configuration.CaptureCadencePolicy;
            await Task.Delay(cadence.WatcherPollInterval, cancellationToken).ConfigureAwait(false);
            await RebuildWatchersAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var dueChanges = TakeDueChanges(cadence, now);
            var shouldCatchUp = dueChanges.Count > 0 || pendingCatchUp;
            var shouldReconcile = now - lastFullScanUtc >= cadence.PeriodicReconciliationInterval;
            if (!shouldCatchUp && !shouldReconcile)
            {
                continue;
            }

            if (dueChanges.Count > 0)
            {
                await operations.RunBackupForFilesAsync(dueChanges.Select(change => change.SourcePath), cancellationToken)
                    .ConfigureAwait(false);
                now = DateTimeOffset.UtcNow;
                foreach (var change in dueChanges)
                {
                    lastCaptureAttemptByPath[change.SourcePath] = now;
                }
            }

            if (pendingCatchUp)
            {
                await RunCatchUpCycleAsync(cancellationToken).ConfigureAwait(false);
            }

            if (shouldReconcile)
            {
                await operations.RunBackupNowAsync(cancellationToken).ConfigureAwait(false);
                lastFullScanUtc = now;
            }

            pendingCatchUp = false;
        }
    }

    public async Task RunCatchUpCycleAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await usnCatchUpService.CatchUpAsync(configuration, cancellationToken).ConfigureAwait(false);
            operations.UpdateDurableChangeStatus(result.Status);
            if (result.RequiresFullScan)
            {
                await operations.RunBackupNowAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (result.ChangedFiles.Count > 0)
            {
                await operations.RunBackupForFilesAsync(result.ChangedFiles, cancellationToken).ConfigureAwait(false);
            }
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
            operations.UpdateDurableChangeStatus(new DurableChangeRuntimeStatus(
                DateTimeOffset.UtcNow,
                "USN unavailable.",
                detail.Reason,
                []) with
            {
                Details = [detail]
            });
            await operations.RunBackupNowAsync(cancellationToken).ConfigureAwait(false);
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
            return;
        }

        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        watchers.Clear();
        watcherSignature = signature;

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

    private void MarkPending(WatchedFolderConfiguration folder, FileSystemEventArgs args)
    {
        var sourcePath = Path.GetFullPath(args.FullPath);
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (pendingFileChanges.TryGetValue(sourcePath, out var existing))
            {
                pendingFileChanges[sourcePath] = existing with { LatestEventUtc = now };
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
        }

        return due;
    }

    private sealed record PendingFileChange(
        string SourcePath,
        string WatchedFolderId,
        ResourceProfile ResourceProfile,
        DateTimeOffset FirstEventUtc,
        DateTimeOffset LatestEventUtc);
}
