using System.Text;
using System.IO;
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
        var id = VaultId.New();
        var parent = Path.Combine(Path.GetDirectoryName(template.RepositoryPath)!, "capture-" + id.Value.ToString("N"));
        var binding = new VaultBinding(id, Path.Combine(parent, "repository"), Path.Combine(parent, "state"), template.MetadataStore);
        await using var store = new PostgreSqlRepositoryMetadataStore(binding);
        await store.ProvisionVaultAsync();
        var repository = Repository(binding, store);
        await repository.ProvisionVaultStorageAsync();
        var work = Path.Combine(parent, "working");
        var path = Path.Combine(work, "document.txt");
        var content = Encoding.UTF8.GetBytes("single repository working file bytes");
        var capture = await repository.CommitAsync(Request());
        Check(capture.Manifest.VaultId == binding.Id, "captured manifest carries installed repository identity");
        Check((await store.ListManifestsAsync()).All(m => m.VaultId == binding.Id), "file and folder manifests remain bound");
        await using var reopenedStore = new PostgreSqlRepositoryMetadataStore(binding);
        var reopened = Repository(binding, reopenedStore);
        var recovered = Path.Combine(work, "recovered-file.txt");
        var result = await reopened.RestoreAsync(capture.Manifest.VersionId, recovered);
        Check(result.VerifiedLogicalBytes == content.Length && (await File.ReadAllBytesAsync(recovered)).SequenceEqual(content), "reopened repository returns verified file bytes");
        var folder = (await reopened.ListVersionsAsync()).Single(item => item.EntryKind == RepositoryEntryKind.Folder);
        var folderOutput = Path.Combine(work, "recovered-folder");
        await reopened.RestoreAsync(folder.VersionId, folderOutput);
        Check((await File.ReadAllBytesAsync(Path.Combine(folderOutput, "document.txt"))).SequenceEqual(content), "reopened repository returns verified folder bytes");
        try { _ = Repository(binding with { RepositoryPath = binding.RepositoryPath + "-wrong" }, reopenedStore); throw new InvalidOperationException("Mismatched metadata root was accepted."); }
        catch (ArgumentException) { checks.Add("metadata root mismatch rejects before storage access"); }
        await Rejected(() => repository.ProvisionVaultStorageAsync(), RepositoryIntegrityFailure.OwnershipMismatch, "reprovisioning existing storage is rejected");
        await reopened.PurgeAsync(new([new(work, RepositoryPurgeScopeKind.RecursiveFolder)]));
        Check((await reopened.ListVersionsAsync()).Count == 0, "authorised history purge removes requested inventory");
        capture = await repository.CommitAsync(Request());
        Check(capture.Manifest.VaultId == binding.Id && (await repository.ListVersionsAsync()).Count == 2, "repository remains usable after authorised purge");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var poison = new NpgsqlCommand($"UPDATE \"{binding.MetadataNamespace}\".vault_binding SET primary_root='changed'", connection))
            await poison.ExecuteNonQueryAsync();
        var filesBefore = Files(binding.RepositoryPath);
        await Rejected(() => repository.CommitAsync(Request()), RepositoryIntegrityFailure.OwnershipMismatch, "changed metadata binding rejects before chunk publication");
        Check(filesBefore == Files(binding.RepositoryPath), "failed binding capture preserves every existing storage byte");
        return new { Passed = checks.Count, Checks = checks, NativeCallerFileAccess = false };

        static FileSystemChunkRepository Repository(VaultBinding value, PostgreSqlRepositoryMetadataStore metadata) =>
            new(value, new FastCdcChunker(new(128, 256, 512)), new(), new(), null, metadata);
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
    }
}
