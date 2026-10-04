using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;
using Npgsql;

namespace FluxVault.TestHost;

internal static class VaultMetadataProbe
{
    internal static async Task<object> RunAsync(NpgsqlDataSource source, VaultBinding one, VaultBinding two)
    {
        var checks = new List<string>();
        await using var first = new PostgreSqlRepositoryMetadataStore(one, "same-device");
        await using var second = new PostgreSqlRepositoryMetadataStore(two, "same-device");
        await first.ProvisionVaultAsync();
        await second.ProvisionVaultAsync();
        var captured = DateTimeOffset.UtcNow;
        var path = Path.Combine(Path.GetDirectoryName(one.RepositoryPath)!, "working", "same.txt");
        var digest = new string('a', 64);
        var versionId = new string('1', 32);
        var a = new FileVersionManifest(versionId, "alpha", path, captured, CaptureConsistency.BestEffort, 10,
            [new(digest, 0, 10, 10, ChunkEncoding.Raw)], VaultId: one.Id);
        var b = a with { WatchedFolderId = "beta", LogicalLength = 11, Chunks = [new(digest, 0, 11, 11, ChunkEncoding.Raw)], VaultId = two.Id };
        await first.RecordVersionAsync(a);
        await second.RecordVersionAsync(b);
        Check((await first.ReadManifestAsync(versionId)).LogicalLength == 10 && (await second.ReadManifestAsync(versionId)).LogicalLength == 11, "colliding version/path/digest reads remain independent");
        Check((await first.FindChunkDescriptorAsync(digest))?.LogicalLength == 10 && (await second.FindChunkDescriptorAsync(digest))?.LogicalLength == 11, "descriptor lookups stay in each namespace");
        Check((await first.ListVersionsAsync()).Single().LogicalLength == 10 && (await second.ListVersionsAsync()).Single().LogicalLength == 11, "version inventories stay independent");
        var parentId = new string('2', 32);
        var folderA = a with { VersionId = parentId, EntryKind = RepositoryEntryKind.Folder, LogicalLength = 0, Chunks = [],
            SourcePath = Path.GetDirectoryName(path)!, ParentVersionIds = [versionId], FolderEntries = [new("same.txt", path, RepositoryEntryKind.File, versionId, false, 10, captured)] };
        var folderB = folderA with { VaultId = two.Id, FolderEntries = [new("same.txt", path, RepositoryEntryKind.File, versionId, false, 11, captured)] };
        await first.RecordVersionAsync(folderA);
        await second.RecordVersionAsync(folderB);
        Check((await first.ReadManifestAsync(parentId)).FolderEntries!.Single().LogicalLength == 10 && (await second.ReadManifestAsync(parentId)).FolderEntries!.Single().LogicalLength == 11, "folder and lineage collisions stay independent");
        Check((await first.ListLatestEntriesAsync()).Count == 2 && (await second.ListLatestEntriesAsync()).Count == 2, "current entry inventories stay independent");
        await using (var barrier = await source.OpenConnectionAsync())
        await using (var transaction = await barrier.BeginTransactionAsync())
        {
            await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", barrier, transaction);
            hold.Parameters.AddWithValue("key", first.MutationLockKey);
            await hold.ExecuteNonQueryAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await second.RecordVersionAsync(b, deadline.Token);
            Check(first.MutationLockKey != second.MutationLockKey, "first vault mutation lock does not block second vault writes");
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try { await first.RecordVersionAsync(a, cancelled.Token); throw new InvalidOperationException("The first vault lock was bypassed."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { checks.Add("same vault lock waiter cancels without partial mutation"); }
            await transaction.RollbackAsync();
        }
        await Rejected(() => first.ExportOutboxAsync(two.RepositoryPath), RepositoryIntegrityFailure.OwnershipMismatch, "outbox cannot be redirected to another root");
        Check(!Directory.Exists(two.RepositoryPath), "rejected export does not create another vault root");
        Check(await first.ExportOutboxAsync(one.RepositoryPath) == 2, "authorised outbox export is scoped");
        var before = await Snapshot(two);
        await first.DeleteVersionsAsync([parentId, versionId]);
        Check((await first.ListVersionsAsync()).Count == 0 && (await second.ListVersionsAsync()).Count == 2, "version deletion stays isolated");
        Check((await first.CountChunkReferencesAsync([digest])).GetValueOrDefault(digest) == 0 && (await second.CountChunkReferencesAsync([digest]))[digest] == 1, "chunk retirement cannot affect other vault references");
        Check((await first.GetRuntimeStatusAsync(TimeSpan.FromMinutes(1))).PendingOutboxCount == 0 && (await second.GetRuntimeStatusAsync(TimeSpan.FromMinutes(1))).PendingOutboxCount == 2, "outbox runtime status stays isolated");
        Check(before == await Snapshot(two), "all second vault table rows remain byte-equivalent after first-vault mutations");
        await Rejected(() => first.RecordVersionAsync(a with { VaultId = two.Id }), RepositoryIntegrityFailure.OwnershipMismatch, "wrong manifest vault identity is rejected");
        await Rejected(() => first.RecordVersionAsync(a with { VaultId = null }), RepositoryIntegrityFailure.OwnershipMismatch, "unbound manifest cannot be adopted");
        await using var missing = new PostgreSqlRepositoryMetadataStore(one with { Id = VaultId.New() });
        await Rejected(() => missing.InitializeAsync(), RepositoryIntegrityFailure.OwnershipUnknown, "missing namespace does not auto-provision");
        await using var mismatched = new PostgreSqlRepositoryMetadataStore(two with { RepositoryPath = one.RepositoryPath });
        await Rejected(() => mismatched.InitializeAsync(), RepositoryIntegrityFailure.OwnershipMismatch, "root binding mismatch fails before metadata access");
        await Rejected(() => second.ProvisionVaultAsync(), RepositoryIntegrityFailure.OwnershipMismatch, "existing namespace is never silently adopted");
        await using var connection = await source.OpenConnectionAsync();
        await using var poison = new NpgsqlCommand($"UPDATE \"{two.MetadataNamespace}\".vault_binding SET primary_root = 'changed'", connection);
        await poison.ExecuteNonQueryAsync();
        await Rejected(() => second.ReadManifestAsync(versionId), RepositoryIntegrityFailure.OwnershipMismatch, "binding is rechecked after cached initialisation");
        return new { Passed = checks.Count, Checks = checks };

        void Check(bool result, string name) { if (!result) throw new InvalidOperationException("Metadata isolation contract failed: " + name); checks.Add(name); }
        async Task Rejected(Func<Task> action, RepositoryIntegrityFailure failure, string name)
        {
            try { await action(); } catch (RepositoryIntegrityException exception) when (exception.Code == failure) { checks.Add(name); return; }
            throw new InvalidOperationException("Metadata isolation contract did not reject: " + name);
        }
        async Task<string> Snapshot(VaultBinding binding)
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var tables = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname=@schema ORDER BY tablename", connection);
            tables.Parameters.AddWithValue("schema", binding.MetadataNamespace);
            var names = new List<string>();
            await using (var reader = await tables.ExecuteReaderAsync()) while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            var rows = new SortedDictionary<string, string>();
            foreach (var table in names)
            {
                var identifier = new NpgsqlCommandBuilder().QuoteIdentifier(table);
                await using var read = new NpgsqlCommand($"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text)::text,'[]') FROM \"{binding.MetadataNamespace}\".{identifier} t", connection);
                rows.Add(table, (string)(await read.ExecuteScalarAsync())!);
            }
            return JsonSerializer.Serialize(rows);
        }
    }
}
