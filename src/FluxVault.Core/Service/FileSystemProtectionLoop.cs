using FluxVault.Abstractions.ChangeTracking;
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
    private DateTimeOffset lastChangeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastFullScanUtc = DateTimeOffset.MinValue;
    private bool pendingChanges = true;
    private string watcherSignature = string.Empty;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        await RebuildWatchersAsync(cancellationToken).ConfigureAwait(false);
        await RunCatchUpCycleAsync(cancellationToken).ConfigureAwait(false);
        pendingChanges = false;
        lastFullScanUtc = DateTimeOffset.UtcNow;

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RebuildWatchersAsync(cancellationToken).ConfigureAwait(false);
            var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var debounce = configuration.WatchedFolders.Count == 0
                ? TimeSpan.FromSeconds(8)
                : configuration.WatchedFolders.Min(folder => ResourceProfileScheduler.GetDebounceDelay(folder.ResourceProfile));
            var now = DateTimeOffset.UtcNow;
            var shouldCatchUp = pendingChanges && now - lastChangeUtc >= debounce;
            var shouldReconcile = now - lastFullScanUtc >= TimeSpan.FromMinutes(10);
            if (!shouldCatchUp && !shouldReconcile)
            {
                continue;
            }

            if (shouldCatchUp)
            {
                await RunCatchUpCycleAsync(cancellationToken).ConfigureAwait(false);
            }

            if (shouldReconcile)
            {
                await operations.RunBackupNowAsync(cancellationToken).ConfigureAwait(false);
                lastFullScanUtc = now;
            }

            pendingChanges = false;
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
            operations.UpdateDurableChangeStatus(new DurableChangeRuntimeStatus(
                DateTimeOffset.UtcNow,
                "USN unavailable.",
                ex.Message,
                []));
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
            watcher.Changed += MarkPending;
            watcher.Created += MarkPending;
            watcher.Renamed += MarkPending;
            watcher.Deleted += MarkPending;
            watchers.Add(watcher);
        }

        pendingChanges = true;
    }

    private void MarkPending(object sender, FileSystemEventArgs args)
    {
        lock (gate)
        {
            pendingChanges = true;
            lastChangeUtc = DateTimeOffset.UtcNow;
        }
    }
}
