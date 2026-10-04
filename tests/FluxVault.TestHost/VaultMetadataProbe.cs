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
        Check(await store.ExportOutboxAsync(binding.RepositoryPath) == 2, "bound outbox export");
        await store.DeleteVersionsAsync([parentId, versionId]);
        Check((await store.ListVersionsAsync()).Count == 0, "version deletion removes requested history");
        Check((await store.CountChunkReferencesAsync([digest])).GetValueOrDefault(digest) == 0, "deleted history retires chunk references");
        Check((await store.GetRuntimeStatusAsync(TimeSpan.FromMinutes(1))).PendingOutboxCount == 0, "deleted history retires outbox entries");
        await Rejected(() => store.RecordVersionAsync(manifest with { VaultId = VaultId.New() }), RepositoryIntegrityFailure.OwnershipMismatch, "wrong manifest identity is rejected");
        await Rejected(() => store.RecordVersionAsync(manifest with { VaultId = null }), RepositoryIntegrityFailure.OwnershipMismatch, "unbound manifest cannot be adopted");
        await using var missing = new PostgreSqlRepositoryMetadataStore(binding with { Id = VaultId.New() });
        await Rejected(() => missing.InitializeAsync(), RepositoryIntegrityFailure.OwnershipUnknown, "missing namespace does not auto-provision");
        await using var mismatched = new PostgreSqlRepositoryMetadataStore(binding with { RepositoryPath = wrongRoot });
        await Rejected(() => mismatched.InitializeAsync(), RepositoryIntegrityFailure.OwnershipMismatch, "root binding mismatch fails before metadata access");
        await Rejected(() => store.ProvisionVaultAsync(), RepositoryIntegrityFailure.OwnershipMismatch, "existing namespace is never silently adopted");
        await using var connection = await source.OpenConnectionAsync();
        await using var poison = new NpgsqlCommand($"UPDATE \"{binding.MetadataNamespace}\".vault_binding SET primary_root = 'changed'", connection);
        await poison.ExecuteNonQueryAsync();
        await Rejected(() => store.ReadManifestAsync(versionId), RepositoryIntegrityFailure.OwnershipMismatch, "binding is rechecked after cached initialisation");
        return new { Passed = checks.Count, Checks = checks };

        void Check(bool result, string name) { if (!result) throw new InvalidOperationException("Metadata binding contract failed: " + name); checks.Add(name); }
        async Task Rejected(Func<Task> action, RepositoryIntegrityFailure failure, string name)
        {
            try { await action(); } catch (RepositoryIntegrityException exception) when (exception.Code == failure) { checks.Add(name); return; }
            throw new InvalidOperationException("Metadata binding contract did not reject: " + name);
        }
    }
}
