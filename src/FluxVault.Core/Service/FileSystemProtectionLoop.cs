using FluxVault.Core.Configuration;
using FluxVault.Core.Policies;

namespace FluxVault.Core.Service;

public sealed class FileSystemProtectionLoop(FluxVaultOperations operations, IFluxVaultConfigurationStore configurationStore)
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

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RebuildWatchersAsync(cancellationToken).ConfigureAwait(false);
            var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var debounce = configuration.WatchedFolders.Count == 0
                ? TimeSpan.FromSeconds(8)
                : configuration.WatchedFolders.Min(folder => ResourceProfileScheduler.GetDebounceDelay(folder.ResourceProfile));
            var now = DateTimeOffset.UtcNow;
            var shouldRun = pendingChanges && now - lastChangeUtc >= debounce;
            shouldRun |= now - lastFullScanUtc >= TimeSpan.FromMinutes(10);
            if (!shouldRun)
            {
                continue;
            }

            await operations.RunBackupNowAsync(cancellationToken).ConfigureAwait(false);
            pendingChanges = false;
            lastFullScanUtc = now;
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
