using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;
using Npgsql;

namespace FluxVault.TestHost;

internal static class VaultHistoryPagingProbe
{
    internal static async Task<IReadOnlyList<string>> RunAsync(NpgsqlDataSource source, VaultBinding binding,
        IRepositoryMetadataStore store, FileVersionManifest template)
    {
        var checks = new List<string>();
        var root = Path.Combine(Path.GetDirectoryName(template.SourcePath)!, "history_%_\\scope");
        var captured = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var manifests = Enumerable.Range(2000, 19).Select((i, n) => template with
        { VersionId = i.ToString("x32"), SourcePath = Path.Combine(root, "child" + n), CapturedAtUtc = captured.AddTicks(n / 2) }).ToArray();
        var other = template with { VersionId = 3000.ToString("x32"), SourcePath = Path.Combine(root + "sibling", "file.dwg"), CapturedAtUtc = captured.AddTicks(20) };
        var decoy = template with { VersionId = 3001.ToString("x32"), SourcePath = Path.Combine(root.Replace("_", "X").Replace("%", "decoy"), "file.dwg"), CapturedAtUtc = captured.AddTicks(40) };
        var folder = template with { VersionId = 4000.ToString("x32"), SourcePath = root, EntryKind = RepositoryEntryKind.Folder,
            CapturedAtUtc = captured.AddTicks(30), LogicalLength = 0, Chunks = [], FolderEntries = manifests.Take(7).Reverse()
                .Select(m => new FolderVersionEntry(Path.GetFileName(m.SourcePath), m.SourcePath, RepositoryEntryKind.File,
                    m.VersionId, false, m.LogicalLength, m.CapturedAtUtc)).ToArray() };
        var driveRoot = folder with { VersionId = 6000.ToString("x32"), SourcePath = Path.GetPathRoot(template.SourcePath)!, FolderEntries = [] };
        var large = Enumerable.Range(7000, 12).Select((i, n) => template with { VersionId = i.ToString("x32"),
            SourcePath = Path.Combine(root, "budget", "file" + n), CapturedAtUtc = captured.AddTicks(n),
            InheritedFromSourcePath = new string('"', 100_000) }).ToArray();
        var ids = manifests.Concat(large).Select(m => m.VersionId).Append(other.VersionId).Append(decoy.VersionId).Append(folder.VersionId).Append(driveRoot.VersionId).Append(5000.ToString("x32")).ToArray();
        await using var connection = await source.OpenConnectionAsync();
        var schema = '"' + binding.MetadataNamespace + '"';
        try
        {
            await store.RecordVersionsAsync(manifests.Append(other).Append(decoy).Append(folder).Append(driveRoot).ToArray());
            var exactRoot = await store.ListHistoryPageAsync(new(binding.Id, driveRoot.SourcePath, EntryKind: RepositoryEntryKind.Folder));
            Check(exactRoot.Versions.Single().VersionId == driveRoot.VersionId, "exact drive-root scope includes its recorded root snapshot");
            var descendantRoot = await store.ListHistoryPageAsync(new(binding.Id, driveRoot.SourcePath, true, RepositoryEntryKind.Folder));
            Check(descendantRoot.Versions.Any(v => v.VersionId == driveRoot.VersionId) && descendantRoot.Versions.Any(v => v.VersionId == folder.VersionId), "drive-root descendant scope includes self and children");
            var query = new RepositoryHistoryQuery(binding.Id, root, true, RepositoryEntryKind.File, 4);
            var first = await store.ListHistoryPageAsync(query);
            Check(first.Versions.Select(v => v.VersionId).SequenceEqual(manifests.Reverse().Take(4).Select(m => m.VersionId)), "scoped first page retains 100ns and ordinal keys before limit");
            var all = first.Versions.ToList();
            var page = first;
            var pageReads = 0;
            while (page.OlderCursor is { } cursor)
            {
                if (++pageReads > manifests.Length) throw new InvalidOperationException("History continuation did not advance.");
                var previous = page;
                page = await store.ListHistoryPageAsync(query with { Cursor = cursor });
                var back = await store.ListHistoryPageAsync(query with { Cursor = page.NewerCursor });
                Check(JsonSerializer.Serialize(back.Versions) == JsonSerializer.Serialize(previous.Versions), "newer page reverses exact keyset without omissions");
                all.AddRange(page.Versions);
            }
            Check(all.Count == manifests.Length && all.Select(v => v.VersionId).Distinct().Count() == manifests.Length, "all history pages have no holes or duplicates and escape wildcard scope");
            Check(all.All(v => v.FolderEntries is null && v.ParentVersionIds is null), "history headers omit unbounded child and lineage arrays");
            await Refused<ArgumentException>(() => store.ListHistoryPageAsync(query with { SourcePath = root + "sibling", Cursor = first.OlderCursor }), "cursor refuses changed path");
            await Refused<ArgumentException>(() => store.ListHistoryPageAsync(query with { IncludeDescendants = false, Cursor = first.OlderCursor }), "cursor refuses changed scope");
            await Refused<ArgumentException>(() => store.ListHistoryPageAsync(query with { EntryKind = null, Cursor = first.OlderCursor }), "cursor refuses changed kind");
            await Refused<ArgumentException>(() => store.ListHistoryPageAsync(query with { RepositoryId = VaultId.New() }), "query refuses wrong binding");
            var snapshot = await store.GetSnapshotPageAsync(new(binding.Id, folder.VersionId, PageSize: 3));
            Check(snapshot.Entries.Count == 3 && snapshot.NextOffset == 3 && snapshot.Version.FolderEntries is null, "immutable folder detail is independently paged");
            var child = await store.GetSnapshotPageAsync(new(binding.Id, snapshot.Entries[0].VersionId));
            Check(child.Version.VersionId == snapshot.Entries[0].VersionId, "recorded child is fetched even outside visible history page");
            var generation = first.Generation;
            await using (var barrier = await source.OpenConnectionAsync())
            await using (var transaction = await barrier.BeginTransactionAsync())
            {
                await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", barrier, transaction);
                hold.Parameters.AddWithValue("key", ((PostgreSqlRepositoryMetadataStore)store).MutationLockKey);
                await hold.ExecuteNonQueryAsync();
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                try { await store.RecordVersionAsync(template with { VersionId = 5000.ToString("x32") }, cancel.Token); throw new InvalidOperationException("Cancelled mutation completed."); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                await transaction.RollbackAsync();
            }
            Check((await store.ListHistoryPageAsync(query with { Cursor = first.OlderCursor })).Generation == generation, "cancelled mutation changes neither generation nor history");
            await store.DeleteVersionsAsync([manifests[0].VersionId]);
            await Refused<RepositoryHistoryChangedException>(() => store.ListHistoryPageAsync(query with { Cursor = first.OlderCursor }), "committed deletion invalidates stale cursor");
            first = await store.ListHistoryPageAsync(query);
            await store.RecordVersionAsync(manifests[0]);
            await Refused<RepositoryHistoryChangedException>(() => store.ListHistoryPageAsync(query with { Cursor = first.OlderCursor }), "committed insertion invalidates stale cursor");
            await using var poison = new NpgsqlCommand($"UPDATE {schema}.versions SET captured_at_ticks=captured_at_ticks+1 WHERE version_id=@id", connection);
            poison.Parameters.AddWithValue("id", manifests[^1].VersionId);
            await poison.ExecuteNonQueryAsync();
            try { await Refused<RepositoryIntegrityException>(() => store.ListHistoryPageAsync(query), "selected header validates exact immutable timestamp"); }
            finally { poison.CommandText = $"UPDATE {schema}.versions SET captured_at_ticks=captured_at_ticks-1 WHERE version_id=@id"; await poison.ExecuteNonQueryAsync(); }
            await store.RecordVersionsAsync(large);
            var budgetQuery = new RepositoryHistoryQuery(binding.Id, Path.Combine(root, "budget"), true, PageSize: 16);
            var budgetSeen = new List<string>();
            RepositoryHistoryCursor? budgetCursor = null;
            do
            {
                if (budgetSeen.Count > large.Length) throw new InvalidOperationException("Budget continuation did not advance.");
                var requested = budgetQuery with { Cursor = budgetCursor };
                var candidate = await store.ListHistoryPageAsync(requested);
                Check(candidate.Versions.Count < large.Length, "SQL limits aggregate header transfer before IPC materialisation");
                var bounded = RepositoryHistoryPaging.BoundResponse(FluxVault.Abstractions.Ipc.FluxVaultIpcResponse.Ok() with
                    { VaultId = binding.Id, VaultRevision = 1, HistoryPage = candidate });
                Check(bounded.Success && bounded.HistoryPage!.Versions.Count > 0 &&
                    JsonSerializer.SerializeToUtf8Bytes(bounded, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= RepositoryHistoryPaging.MaximumResponseBytes,
                    "actual escaped wire envelope fits the byte budget");
                budgetSeen.AddRange(bounded.HistoryPage!.Versions.Select(v => v.VersionId));
                budgetCursor = bounded.HistoryPage.OlderCursor;
            } while (budgetCursor is not null);
            Check(budgetSeen.Count == large.Length && budgetSeen.Distinct().Count() == large.Length,
                "SQL and IPC shortened pages retain every version exactly once");
        }
        finally
        {
            try { await store.DeleteVersionsAsync(ids); }
            finally
            {
                await using var retire = new NpgsqlCommand($"DELETE FROM {schema}.metadata_outbox WHERE version_id=ANY(@ids)", connection);
                retire.Parameters.AddWithValue("ids", ids); await retire.ExecuteNonQueryAsync();
            }
        }
        return checks;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("History paging contract failed: " + name); checks.Add(name); }
        async Task Refused<T>(Func<Task> operation, string name) where T : Exception
        {
            try { await operation(); } catch (T) { checks.Add(name); return; }
            throw new InvalidOperationException("History paging did not refuse: " + name);
        }
    }
}
