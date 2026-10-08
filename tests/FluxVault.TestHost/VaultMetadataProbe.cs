using System.IO;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;
using Npgsql;

namespace FluxVault.TestHost;

internal static class VaultMetadataProbe
{
    internal static async Task<object> RunAsync(NpgsqlDataSource source, VaultBinding binding)
    {
        var checks = new List<string>();
        await using var store = new PostgreSqlRepositoryMetadataStore(binding, "same-device");
        await store.ProvisionVaultAsync();
        var captured = DateTimeOffset.UtcNow;
        var path = Path.Combine(Path.GetDirectoryName(binding.RepositoryPath)!, "working", "document.txt");
        var digest = new string('a', 64);
        var versionId = new string('1', 32);
        var manifest = new FileVersionManifest(versionId, "work", path, captured, CaptureConsistency.BestEffort, 10,
            [new(digest, 0, 10, 10, ChunkEncoding.Raw)], VaultId: binding.Id);
        await store.RecordVersionAsync(manifest);
        Check((await store.ReadManifestAsync(versionId)).LogicalLength == 10, "bound manifest round trip");
        Check((await store.FindChunkDescriptorAsync(digest))?.LogicalLength == 10, "bound descriptor lookup");
        Check((await store.ListVersionsAsync()).Single().VersionId == versionId, "bound version inventory");
        var parentId = new string('2', 32);
        var folder = manifest with { VersionId = parentId, EntryKind = RepositoryEntryKind.Folder, LogicalLength = 0, Chunks = [],
            SourcePath = Path.GetDirectoryName(path)!, ParentVersionIds = [versionId],
            FolderEntries = [new("document.txt", path, RepositoryEntryKind.File, versionId, false, 10, captured)] };
        await store.RecordVersionAsync(folder);
        Check((await store.ReadManifestAsync(parentId)).FolderEntries!.Single().LogicalLength == 10, "folder and lineage round trip");
        Check((await store.ListLatestEntriesAsync()).Count == 2, "current entry inventory");
        checks.AddRange(await VaultCurrentEntriesProbe.RunAsync(source, binding, store, manifest));
        checks.AddRange(await VaultRecentVersionsProbe.RunAsync(source, binding, store, manifest));
        checks.AddRange(await VaultHistoryPagingProbe.RunAsync(source, binding, store, manifest));
        checks.AddRange(await VaultCurrentPagingProbe.RunAsync(source, binding, store, manifest));
        await using (var barrier = await source.OpenConnectionAsync())
        await using (var transaction = await barrier.BeginTransactionAsync())
        {
            await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", barrier, transaction);
            hold.Parameters.AddWithValue("key", store.MutationLockKey);
            await hold.ExecuteNonQueryAsync();
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try { await store.RecordVersionAsync(manifest, cancelled.Token); throw new InvalidOperationException("The repository lock was bypassed."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { checks.Add("mutation lock waiter cancels without partial mutation"); }
            await transaction.RollbackAsync();
        }
        var wrongRoot = binding.RepositoryPath + "-wrong";
        await Rejected(() => store.ExportOutboxAsync(wrongRoot), RepositoryIntegrityFailure.OwnershipMismatch, "outbox refuses wrong repository root");
        Check(!Directory.Exists(wrongRoot), "rejected export creates no storage");
        await using (var exportConnection=await source.OpenConnectionAsync())
        {
            await using var firstOutbox=new NpgsqlCommand($"SELECT outbox_id,device_id,payload_json::text FROM \"{binding.MetadataNamespace}\".metadata_outbox WHERE exported_at_utc IS NULL ORDER BY outbox_id LIMIT 1",exportConnection);
            long outboxId;
            string device;
            string payload;
            await using (var reader=await firstOutbox.ExecuteReaderAsync())
            {
                if(!await reader.ReadAsync())throw new InvalidOperationException("Fixture outbox is absent.");
                outboxId=reader.GetInt64(0);device=reader.GetString(1);payload=reader.GetString(2);
            }
            if(device!="same-device")throw new InvalidOperationException("Fixture export device changed.");
            var journal=Path.Combine(binding.RepositoryPath,"metadata-journal",device);
            Directory.CreateDirectory(journal);
            var export=Path.Combine(journal,$"{outboxId:D20}.fvop");
            await File.WriteAllTextAsync(export,"owned fixture corrupt export");
            try { await store.ExportOutboxAsync(binding.RepositoryPath); throw new InvalidOperationException("Corrupt existing outbox was acknowledged."); }
            catch(InvalidDataException) { checks.Add("bound outbox preserves and refuses mismatched existing export"); }
            Check((await store.GetRuntimeStatusAsync(TimeSpan.FromMinutes(1))).PendingOutboxCount==2,
                "mismatched existing export remains durably pending rather than falsely exported");
            Check(await File.ReadAllTextAsync(export)=="owned fixture corrupt export","failed outbox validation preserves existing export bytes");
            // Explicit repair of this test-owned synthetic corruption, without another capture.
            await File.WriteAllTextAsync(export,payload);
        }
        Check(await store.ExportOutboxAsync(binding.RepositoryPath) == 2, "bound outbox export");
        await store.DeleteVersionsAsync([parentId, versionId]);
        Check((await store.ListVersionsAsync()).Count == 0, "version deletion removes requested history");
        Check((await store.CountChunkReferencesAsync([digest])).GetValueOrDefault(digest) == 0, "deleted history retires chunk references");
        Check((await store.GetRuntimeStatusAsync(TimeSpan.FromMinutes(1))).PendingOutboxCount == 0, "already exported history has no pending outbox entries");
        var deletedId=Guid.NewGuid().ToString("N");
        var retainedId=Guid.NewGuid().ToString("N");
        await store.RecordVersionAsync(manifest with { VersionId=deletedId,SourcePath=path+"-deleted",CapturedAtUtc=captured.AddSeconds(1) });
        await store.RecordVersionAsync(manifest with { VersionId=retainedId,SourcePath=path+"-retained",CapturedAtUtc=captured.AddSeconds(2) });
        await using (var exportConnection=await source.OpenConnectionAsync())
        await using (var deletedOutbox=new NpgsqlCommand($"SELECT outbox_id FROM \"{binding.MetadataNamespace}\".metadata_outbox WHERE version_id=@version AND exported_at_utc IS NULL",exportConnection))
        {
            deletedOutbox.Parameters.AddWithValue("version",deletedId);
            var deletedOutboxId=(long)(await deletedOutbox.ExecuteScalarAsync() ?? throw new InvalidOperationException("Deleted-version outbox is absent."));
            var deletedExport=Path.Combine(binding.RepositoryPath,"metadata-journal","same-device",$"{deletedOutboxId:D20}.fvop");
            await File.WriteAllTextAsync(deletedExport,"owned deletion fixture corrupt export");
            try { await store.ExportOutboxAsync(binding.RepositoryPath); throw new InvalidOperationException("Deletion fixture export unexpectedly succeeded."); }
            catch(InvalidDataException) { checks.Add("deletion fixture starts with a failed durable export and unrelated pending work"); }
            await store.DeleteVersionsAsync([deletedId]);
            Check(await File.ReadAllTextAsync(deletedExport)=="owned deletion fixture corrupt export",
                "history deletion preserves existing journal evidence after an unsuccessful export");
            // Remove only the fixture's synthetic corrupt export to make a stale retry observable.
            File.Delete(deletedExport);
            var drained=await store.ExportOutboxAsync(binding.RepositoryPath);
            Check(drained==1 && !File.Exists(deletedExport),
                "deleting a version atomically retires its pending export without republishing its payload");
            Check((await store.ListVersionsAsync()).Single().VersionId==retainedId &&
                (await store.GetRuntimeStatusAsync(TimeSpan.FromMinutes(1))).PendingOutboxCount==0,
                "deletion-aware outbox drain preserves and exports unrelated pending history");
        }
        await store.DeleteVersionsAsync([retainedId]);
        long orphanOutboxId=0;
        string orphanDevice="";
        await using (var legacyConnection=await source.OpenConnectionAsync())
        await using (var legacy=new NpgsqlCommand($"""
            INSERT INTO "{binding.MetadataNamespace}".metadata_outbox(device_id,operation_id,version_id,payload_json)
            SELECT 'legacy-fixture-' || g.n::text,o.operation_id,o.version_id,o.payload_json
            FROM "{binding.MetadataNamespace}".metadata_outbox o CROSS JOIN generate_series(1,1001) g(n)
            WHERE o.version_id=@version AND o.exported_at_utc IS NOT NULL
            RETURNING outbox_id,device_id
            """,legacyConnection))
        {
            legacy.Parameters.AddWithValue("version",versionId);
            var count=0;
            await using var reader=await legacy.ExecuteReaderAsync();
            while(await reader.ReadAsync())
            {
                count++;
                if(reader.GetInt64(0)>orphanOutboxId) { orphanOutboxId=reader.GetInt64(0);orphanDevice=reader.GetString(1); }
            }
            if(count!=1001)throw new InvalidOperationException("Legacy orphan fixture must exceed one bounded retirement batch.");
        }
        var legacyRetainedId=Guid.NewGuid().ToString("N");
        await store.RecordVersionAsync(manifest with { VersionId=legacyRetainedId,SourcePath=path+"-legacy-retained",CapturedAtUtc=captured.AddSeconds(3) });
        var orphanExport=Path.Combine(binding.RepositoryPath,"metadata-journal",orphanDevice,$"{orphanOutboxId:D20}.fvop");
        Check(await store.ExportOutboxAsync(binding.RepositoryPath)==1 && !File.Exists(orphanExport) &&
            (await store.GetRuntimeStatusAsync(TimeSpan.FromMinutes(1))).PendingOutboxCount==1,
            "bounded orphan retirement exports valid history without publishing an orphan beyond its first batch");
        Check(await store.ExportOutboxAsync(binding.RepositoryPath)==0 &&
            (await store.GetRuntimeStatusAsync(TimeSpan.FromMinutes(1))).PendingOutboxCount==0,
            "repeated deletion-aware draining is harmless and leaves no false orphan lag");
        await store.DeleteVersionsAsync([legacyRetainedId]);
        await Rejected(() => store.RecordVersionAsync(manifest with { VaultId = VaultId.New() }), RepositoryIntegrityFailure.OwnershipMismatch, "wrong manifest identity is rejected");
        await Rejected(() => store.RecordVersionAsync(manifest with { VaultId = null }), RepositoryIntegrityFailure.OwnershipMismatch, "unbound manifest cannot be adopted");
        await using var missing = new PostgreSqlRepositoryMetadataStore(binding with { Id = VaultId.New() });
        await Rejected(() => missing.InitializeAsync(), RepositoryIntegrityFailure.OwnershipUnknown, "missing namespace does not auto-provision");
        await using var mismatched = new PostgreSqlRepositoryMetadataStore(binding with { RepositoryPath = wrongRoot });
        await Rejected(() => mismatched.InitializeAsync(), RepositoryIntegrityFailure.OwnershipMismatch, "root binding mismatch fails before metadata access");
        await Rejected(() => store.ProvisionVaultAsync(), RepositoryIntegrityFailure.OwnershipMismatch, "existing namespace is never silently adopted");
        await using var connection = await source.OpenConnectionAsync();
        await using (var shape = new NpgsqlCommand($"ALTER TABLE \"{binding.MetadataNamespace}\".versions ALTER COLUMN captured_at_ticks DROP NOT NULL", connection))
        {
            await shape.ExecuteNonQueryAsync();
            try { await Rejected(() => store.ListRecentVersionsAsync(1), RepositoryIntegrityFailure.OwnershipMismatch,
                "bound recent read rechecks required ordering-column shape after cached initialisation"); }
            finally
            {
                shape.CommandText = $"ALTER TABLE \"{binding.MetadataNamespace}\".versions ALTER COLUMN captured_at_ticks SET NOT NULL";
                await shape.ExecuteNonQueryAsync();
            }
        }
        await using var poison = new NpgsqlCommand($"UPDATE \"{binding.MetadataNamespace}\".vault_binding SET primary_root = 'changed'", connection);
        await poison.ExecuteNonQueryAsync();
        await Rejected(() => store.ReadManifestAsync(versionId), RepositoryIntegrityFailure.OwnershipMismatch, "binding is rechecked after cached initialisation");
        return new { Passed = checks.Count, Checks = checks, RecentVersionsVerified = true, HistoryPagingVerified = true, CurrentProjectionVerified = true, CurrentPagingVerified = true };

        void Check(bool result, string name) { if (!result) throw new InvalidOperationException("Metadata binding contract failed: " + name); checks.Add(name); }
        async Task Rejected(Func<Task> action, RepositoryIntegrityFailure failure, string name)
        {
            try { await action(); } catch (RepositoryIntegrityException exception) when (exception.Code == failure) { checks.Add(name); return; }
            throw new InvalidOperationException("Metadata binding contract did not reject: " + name);
        }
    }
}
