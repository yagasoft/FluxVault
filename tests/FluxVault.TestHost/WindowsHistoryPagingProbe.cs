using System.IO;
using System.Security.Cryptography;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.TestHost;

internal static class WindowsHistoryPagingProbe
{
    internal static async Task RunAsync(IFluxVaultServiceClient client, VaultId id, string source, string destination,
        IReadOnlyList<RepositoryVersionSummary> fullHistory, Func<FluxVaultIpcRequest, FluxVaultIpcRequest> bind,
        Func<FluxVaultIpcRequest, Task<FluxVaultIpcResponse>> send, List<string> checks, CancellationToken token)
    {
        var query = RepositoryHistoryPaging.Validate(new RepositoryHistoryQuery(id, source, true, PageSize: 2));
        var recovered = Path.Combine(destination, "paged-vm-recovery.docx");
        string? restoredId = null;
        await using var inventory = new VersionInventoryViewModel(source, query,
            (q, ct) => client.SendAsync(bind(new(FluxVaultIpcCommand.ListHistoryPage, null, null, null, null, HistoryQuery: q)), ct),
            (q, ct) => client.SendAsync(bind(new(FluxVaultIpcCommand.GetSnapshotPage, null, null, null, null, SnapshotQuery: q)), ct),
            async row =>
            {
                var response = await send(bind(FluxVaultIpcRequest.RestoreVersion(row.VersionId, recovered)));
                if (response.RestoreResult is not { RestoredFileCount: 1 }) throw new InvalidOperationException("Paged view-model recovery was not verified.");
                restoredId = row.VersionId;
            }, _ => throw new InvalidOperationException("No viewer process is authorised in this fixture."));
        await inventory.InitialiseAsync();
        Check(inventory.HasAuthoritativeHistory && inventory.Versions.Count is > 0 and <= 2, "native actual recovery view-model receives a bounded authoritative first page");
        var seen = inventory.Versions.Select(v => v.VersionId).ToList();
        var pageReads = 0;
        while (inventory.CanLoadOlder)
        {
            token.ThrowIfCancellationRequested();
            if (++pageReads > fullHistory.Count + 1) throw new InvalidOperationException("History continuation did not advance.");
            await inventory.LoadOlderCommand.ExecuteAsync(null);
            Check(inventory.Versions.Count <= 2 && !inventory.HistoryStatus.Contains("could not", StringComparison.Ordinal), "actual older command returns a bounded page");
            seen.AddRange(inventory.Versions.Select(v => v.VersionId));
        }
        var expected = fullHistory.Where(v => RepositoryHistoryPaging.Matches(v, query)).Select(v => v.VersionId).Order(StringComparer.Ordinal).ToArray();
        Check(seen.Distinct().Count() == seen.Count && seen.Order(StringComparer.Ordinal).SequenceEqual(expected), "actual native page flow preserves complete scoped history without duplicates");
        await inventory.RefreshHistoryCommand.ExecuteAsync(null);
        var root = fullHistory.First(v => v.EntryKind == RepositoryEntryKind.Folder && string.Equals(v.SourcePath, source, StringComparison.OrdinalIgnoreCase));
        pageReads = 0;
        while (!inventory.Versions.Any(v => v.VersionId == root.VersionId) && inventory.CanLoadOlder)
        {
            token.ThrowIfCancellationRequested();
            if (++pageReads > fullHistory.Count + 1) throw new InvalidOperationException("Folder continuation did not advance.");
            await inventory.LoadOlderCommand.ExecuteAsync(null);
        }
        inventory.SelectedVersion = inventory.Versions.Single(v => v.VersionId == root.VersionId);
        await inventory.SelectedSnapshotCommand.ExecuteAsync(null);
        var nested = inventory.SnapshotEntries.Single(e => e.EntryKind == RepositoryEntryKind.Folder);
        inventory.SelectedSnapshotEntry = nested;
        await inventory.OpenSelectedSnapshotEntryCommand.ExecuteAsync(null);
        Check(inventory.SnapshotPath.Equals(nested.Path, StringComparison.OrdinalIgnoreCase) && inventory.SnapshotEntries.Count > 0,
            "actual native folder navigation opens its recorded child snapshot");
        var file = inventory.SnapshotEntries.Single(e => e.EntryKind == RepositoryEntryKind.File);
        inventory.SelectedSnapshotEntry = file;
        await inventory.RestoreSelectedSnapshotEntryCommand.ExecuteAsync(null);
        Check(restoredId == file.VersionId && Hash(file.Path).SequenceEqual(Hash(recovered)), "actual paged view-model restores the recorded child with independently verified bytes");
        var wrong = await client.SendAsync(bind(new(FluxVaultIpcCommand.ListHistoryPage, null, null, null, null,
            HistoryQuery: query with { RepositoryId = VaultId.New() })), token);
        Check(!wrong.Success && wrong.HistoryPage is null, "native page command refuses query/envelope binding mismatch");
        var current = await send(bind(new(FluxVaultIpcCommand.ListHistoryPage, null, null, null, null, HistoryQuery: query)));
        var currentPage = current.HistoryPage ?? throw new InvalidOperationException("Missing page.");
        var continuation = currentPage.OlderCursor ?? throw new InvalidOperationException("Missing continuation.");
        Check(true, "native stale-page proof obtains a continuation");
        var changed = await client.SendAsync(bind(new(FluxVaultIpcCommand.ListHistoryPage, null, null, null, null,
            HistoryQuery: query with { Cursor = continuation with { Generation = currentPage.Generation - 1 } })), token);
        Check(!changed.Success && changed.ErrorCode == FluxVaultIpcErrorCode.HistoryChanged && changed.HistoryPage is null,
            "native authenticated command returns HistoryChanged without page data");
        return;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Native paging failed: " + name); checks.Add(name); }
        static byte[] Hash(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
    }
}
