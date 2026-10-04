using System.Text;
using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Security;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;
using Npgsql;

namespace FluxVault.TestHost;

internal static class VaultRepositoryProbe
{
    internal static async Task<object> RunAsync(NpgsqlDataSource source, VaultBinding template)
    {
        var checks = new List<string>();
        var first = NewBinding();
        var second = NewBinding();
        await using var aStore = new PostgreSqlRepositoryMetadataStore(first);
        await using var bStore = new PostgreSqlRepositoryMetadataStore(second);
        await aStore.ProvisionVaultAsync();
        await bStore.ProvisionVaultAsync();
        var a = Repository(first, aStore);
        var b = Repository(second, bStore);
        await a.ProvisionVaultStorageAsync();
        await b.ProvisionVaultStorageAsync();
        var work = Path.Combine(Path.GetDirectoryName(template.RepositoryPath)!, "repository-working");
        var path = Path.Combine(work, "document.txt");
        var content = Encoding.UTF8.GetBytes("same working file and digest in two independent vaults");
        var aCapture = await a.CommitAsync(Request());
        var bCapture = await b.CommitAsync(Request());
        Check(aCapture.Manifest.VaultId == first.Id && bCapture.Manifest.VaultId == second.Id, "captured manifests carry exact vault identity");
        Check(aCapture.Manifest.Chunks.Single().Digest == bCapture.Manifest.Chunks.Single().Digest, "two bound repositories exercise colliding source and digest");
        Check((await aStore.ListManifestsAsync()).All(m => m.VaultId == first.Id) && (await bStore.ListManifestsAsync()).All(m => m.VaultId == second.Id), "real store file and folder manifests remain bound");
        await using var reopenedStore = new PostgreSqlRepositoryMetadataStore(second);
        var reopened = Repository(second, reopenedStore);
        var recovered = Path.Combine(work, "recovered-file.txt");
        var result = await reopened.RestoreAsync(bCapture.Manifest.VersionId, recovered);
        Check(result.VerifiedLogicalBytes == content.Length && (await File.ReadAllBytesAsync(recovered)).SequenceEqual(content), "reopened bound repository returns verified file bytes");
        var folder = (await reopened.ListVersionsAsync()).Single(item => item.EntryKind == RepositoryEntryKind.Folder);
        var folderOutput = Path.Combine(work, "recovered-folder");
        await reopened.RestoreAsync(folder.VersionId, folderOutput);
        Check((await File.ReadAllBytesAsync(Path.Combine(folderOutput, "document.txt"))).SequenceEqual(content), "reopened bound repository returns verified folder bytes");
        var rowsBefore = await Rows(second);
        var filesBefore = Files(second.RepositoryPath);
        await a.PurgeAsync(new([new(work, RepositoryPurgeScopeKind.RecursiveFolder)]));
        Check((await a.ListVersionsAsync()).Count == 0 && (await reopened.ListVersionsAsync()).Count == 2, "first-vault purge keeps second-vault inventory");
        Check(rowsBefore == await Rows(second), "first-vault purge preserves every second-vault database row");
        Check(filesBefore == Files(second.RepositoryPath), "first-vault purge preserves every second-vault storage byte");
        var recoveredAfter = Path.Combine(work, "recovered-after-purge.txt");
        await reopened.RestoreAsync(bCapture.Manifest.VersionId, recoveredAfter);
        Check((await File.ReadAllBytesAsync(recoveredAfter)).SequenceEqual(content), "second vault remains recoverable after first-vault purge");
        try { _ = Repository(second with { RepositoryPath = first.RepositoryPath }, reopenedStore); throw new InvalidOperationException("Mismatched metadata root was accepted."); }
        catch (ArgumentException) { checks.Add("metadata root mismatch rejects before storage access"); }
        await Rejected(() => b.ProvisionVaultStorageAsync(), RepositoryIntegrityFailure.OwnershipMismatch, "reprovisioning existing storage is rejected");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var poison = new NpgsqlCommand($"UPDATE \"{second.MetadataNamespace}\".vault_binding SET primary_root='changed'", connection))
            await poison.ExecuteNonQueryAsync();
        var aFiles = Files(first.RepositoryPath);
        filesBefore = Files(second.RepositoryPath);
        await Rejected(() => b.CommitAsync(Request()), RepositoryIntegrityFailure.OwnershipMismatch, "changed metadata binding rejects before chunk publication");
        Check(filesBefore == Files(second.RepositoryPath) && aFiles == Files(first.RepositoryPath), "failed metadata-binding capture preserves both storage roots");
        return new { Passed = checks.Count, Checks = checks, NativeCallerFileAccess = false };

        VaultBinding NewBinding()
        {
            var id = VaultId.New();
            var parent = Path.Combine(Path.GetDirectoryName(template.RepositoryPath)!, "capture-" + id.Value.ToString("N"));
            return new(id, Path.Combine(parent, "repository"), Path.Combine(parent, "state"), template.MetadataStore);
        }
        static FileSystemChunkRepository Repository(VaultBinding binding, PostgreSqlRepositoryMetadataStore metadata) =>
            new(binding, new FastCdcChunker(new(128, 256, 512)), new(), new(), null, metadata);
        FileCommitRequest Request() => new("work", path, DateTimeOffset.UtcNow, CaptureConsistency.BestEffort,
            CompressionPreference.Off, 1024, new MemoryStream(content), WatchedFolderPath: work);
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Repository binding contract failed: " + name); checks.Add(name); }
        async Task Rejected(Func<Task> action, RepositoryIntegrityFailure expected, string name)
        {
            try { await action(); } catch (RepositoryIntegrityException error) when (error.Code == expected) { checks.Add(name); return; }
            throw new InvalidOperationException("Repository binding contract did not reject: " + name);
        }
        static string Files(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))));
        async Task<string> Rows(VaultBinding binding)
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var tableQuery = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname=@schema ORDER BY tablename", connection);
            tableQuery.Parameters.AddWithValue("schema", binding.MetadataNamespace);
            var tables = new List<string>();
            await using (var reader = await tableQuery.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            var snapshot = new List<string>();
            foreach (var table in tables)
            {
                await using var rows = new NpgsqlCommand($"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text)::text,'[]') FROM \"{binding.MetadataNamespace}\".{new NpgsqlCommandBuilder().QuoteIdentifier(table)} t", connection);
                snapshot.Add(table + ":" + (string)(await rows.ExecuteScalarAsync())!);
            }
            return string.Join("\n", snapshot);
        }
    }
}
