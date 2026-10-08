using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;

namespace FluxVault.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private (VaultId? RepositoryId, string RepositoryPath, MetadataStoreConfiguration MetadataStore)? lastAppliedCurrentInventoryIdentity;
    private Guid? lastAppliedCurrentInventoryEpoch;
    private readonly Lock repositoryReadLifetime = new();
    private readonly CancellationTokenSource repositoryReadsCancellation = new();
    private readonly TaskCompletionSource repositoryReadsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int activeRepositoryRefreshes;
    private bool repositoryReadsStopping;
    private Task? repositoryReadShutdown;

    private CancellationTokenSource? BeginRepositoryRefresh(CancellationToken token)
    {
        lock (repositoryReadLifetime)
        {
            if (repositoryReadsStopping) return null;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, repositoryReadsCancellation.Token);
            activeRepositoryRefreshes++;
            return linked;
        }
    }
    private void EndRepositoryRefresh()
    {
        lock (repositoryReadLifetime)
            if (--activeRepositoryRefreshes == 0 && repositoryReadsStopping) repositoryReadsDrained.TrySetResult();
    }
    public Task StopRepositoryReadsAsync()
    {
        lock (repositoryReadLifetime)
        {
            if (repositoryReadShutdown is not null) return repositoryReadShutdown;
            repositoryReadsStopping = true;
            if (activeRepositoryRefreshes == 0) repositoryReadsDrained.TrySetResult();
            return repositoryReadShutdown = StopRepositoryReadsCoreAsync();
        }
    }
    private async Task StopRepositoryReadsCoreAsync()
    {
        // Leave the lifetime lock before cancellation invokes callbacks.
        await Task.Yield();
        try
        {
            repositoryReadsCancellation.Cancel();
            autoRefreshCancellation?.Cancel();
            if (autoRefreshTask is { } automatic) await automatic.ConfigureAwait(true);
        }
        finally
        {
            await repositoryReadsDrained.Task.ConfigureAwait(true);
            autoRefreshCancellation?.Dispose();
            autoRefreshCancellation = null;
            autoRefreshTask = null;
            repositoryReadsCancellation.Dispose();
        }
    }

    private async Task<IReadOnlyList<RepositoryVersionSummary>> ReadCurrentInventoryAsync(FluxVaultIpcResponse status, CancellationToken token)
    {
        if (status.VaultId is not { IsValid: true } id || status.VaultRevision is not > 0 || status.Status is null)
            throw new InvalidDataException("Current inventory has no verified repository binding.");
        var query = new RepositoryCurrentEntriesQuery(id, (status.Status.Configuration.RepositoryBrowse ?? new()).Normalise().ItemsPerPage);
        var entries = new List<RepositoryVersionSummary>();
        var canonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        long? generation = null;
        long previousPathId = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            var response = await SendBoundAsync(new(FluxVaultIpcCommand.ListCurrentEntriesPage, null, null, null, null,
                VaultId: id, ExpectedVaultRevision: status.VaultRevision, CurrentEntriesQuery: query), token).ConfigureAwait(true);
            if (!response.Success || response.VaultId != id || response.VaultRevision != status.VaultRevision || response.CurrentEntriesPage is not { } page)
                throw new IOException("Current inventory could not be completed: " + (response.ErrorMessage ?? "its binding, revision or page was not confirmed."));
            if (page.Query != query || page.Generation < 0 || generation is { } expected && page.Generation != expected ||
                page.Entries is null || page.Entries.Count > query.PageSize || page.Entries.Count == 0 && (page.NextCursor is not null || query.Cursor is not null))
                throw new InvalidDataException("Current inventory returned an invalid or changed page. Refresh to try again.");
            generation ??= page.Generation;
            foreach (var entry in page.Entries)
            {
                if (entry is null || entry.PathId <= previousPathId || entry.Version is not { } version ||
                    !Guid.TryParseExact(version.VersionId, "N", out _) || string.IsNullOrWhiteSpace(version.SourcePath) || !Path.IsPathFullyQualified(version.SourcePath) ||
                    !Enum.IsDefined(version.EntryKind) || version.LogicalLength < 0 || version.ChunkCount < 0 || version.FolderEntries is not null || version.ParentVersionIds is not null)
                    throw new InvalidDataException("Current inventory returned an invalid entry or non-increasing path identity.");
                var key = Path.GetFullPath(version.SourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant() + "|" + version.EntryKind;
                if (!canonicalPaths.Add(key)) throw new InvalidDataException("Current inventory repeated a canonical path and entry kind.");
                previousPathId = entry.PathId;
                entries.Add(version);
            }
            if (page.NextCursor is { } next && (next.RepositoryId != id || next.PageSize != query.PageSize ||
                next.Generation != generation || next.PathId != previousPathId || next.PathId <= (query.Cursor?.PathId ?? 0)))
                throw new InvalidDataException("Current inventory returned a continuation that does not match its final entry.");
            query = query with { Cursor = page.NextCursor };
        } while (query.Cursor is not null);
        token.ThrowIfCancellationRequested();
        return entries;
    }
}
