using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.ChangeTracking;

public sealed class UsnCatchUpService(
    IUsnChangeJournalReader reader,
    IUsnJournalCheckpointStore checkpointStore)
{
    public async Task<UsnCatchUpResult> CatchUpAsync(
        FluxVaultConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var checkpoints = await checkpointStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var enabledFolders = configuration.WatchedFolders
            .Where(folder => folder.IsEnabled)
            .ToArray();
        var scopes = enabledFolders
            .Where(folder => Directory.Exists(folder.Path))
            .Select(folder => new UsnWatchedFolderScope(folder.Id, Path.GetFullPath(folder.Path), folder.Recursive))
            .ToArray();

        if (scopes.Length == 0)
        {
            var requiresFullScan = enabledFolders.Length > 0;
            return new UsnCatchUpResult(
                RequiresFullScan: requiresFullScan,
                ChangedFiles: [],
                Checkpoints: checkpoints,
                Status: new DurableChangeRuntimeStatus(
                    DateTimeOffset.UtcNow,
                    requiresFullScan ? "Using reconciliation scan." : "No watched folders available for USN.",
                    requiresFullScan ? "No enabled watched folders currently exist on disk." : null,
                    checkpoints));
        }

        var result = await reader.ReadChangesAsync(scopes, checkpoints, cancellationToken).ConfigureAwait(false);
        if (result.Checkpoints.Count > 0)
        {
            await checkpointStore.SaveAsync(result.Checkpoints, cancellationToken).ConfigureAwait(false);
        }

        var changedFiles = result.ChangedFiles
            .Where(file => !file.IsDirectory)
            .Select(file => Path.GetFullPath(file.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var status = new DurableChangeRuntimeStatus(
            DateTimeOffset.UtcNow,
            result.Status,
            result.FallbackReason,
            result.Checkpoints);

        return new UsnCatchUpResult(result.RequiresFullScan, changedFiles, result.Checkpoints, status);
    }
}
