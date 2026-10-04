using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Security;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class VaultRepositoryBindingTests
{
    [Fact]
    public async Task Ordinary_bound_access_never_creates_or_adopts_storage()
    {
        using var workspace = TemporaryWorkspace.Create();
        var binding = Binding(workspace);
        var repo = Create(binding);
        await Rejected(() => repo.ListVersionsAsync(), RepositoryIntegrityFailure.OwnershipUnknown);
        Assert.False(Directory.Exists(binding.RepositoryPath));
        Directory.CreateDirectory(binding.RepositoryPath);
        await Rejected(() => repo.ListVersionsAsync(), RepositoryIntegrityFailure.OwnershipUnknown);
        Assert.Empty(Directory.EnumerateFileSystemEntries(binding.RepositoryPath));
    }

    [Fact]
    public async Task File_folder_and_deletion_manifests_keep_identity_across_reopen_and_verified_recovery()
    {
        using var workspace = TemporaryWorkspace.Create();
        var binding = Binding(workspace);
        var repo = Create(binding);
        await repo.ProvisionVaultStorageAsync();
        var content = Encoding.UTF8.GetBytes("protected work");
        var working = Path.Combine(workspace.RootPath, "working");
        var source = Path.Combine(working, "document.txt");
        var captured = await repo.CommitAsync(Request(source, content, working));
        Assert.Equal(binding.Id, captured.Manifest.VaultId);
        var folder = (await repo.ListVersionsAsync()).Single(item => item.EntryKind == RepositoryEntryKind.Folder);
        var reopened = Create(binding);
        var fileOutput = Path.Combine(workspace.RootPath, "recovered.txt");
        await reopened.RestoreAsync(captured.Manifest.VersionId, fileOutput);
        Assert.Equal(content, await File.ReadAllBytesAsync(fileOutput));
        var folderOutput = Path.Combine(workspace.RootPath, "recovered-folder");
        await reopened.RestoreAsync(folder.VersionId, folderOutput);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(folderOutput, "document.txt")));
        var deleted = await reopened.RecordDeletionAsync(new("work", working, source, false, DateTimeOffset.UtcNow));
        Assert.Equal(binding.Id, deleted!.Manifest.VaultId);
        foreach (var version in await reopened.ListVersionsAsync())
            Assert.Equal(binding.Id, (await reopened.InspectAsync(version.VersionId)).Manifest.VaultId);
        await Rejected(() => reopened.ProvisionVaultStorageAsync(), RepositoryIntegrityFailure.OwnershipMismatch);
    }

    [Fact]
    public async Task Bound_repository_preserves_existing_legacy_storage_without_adoption()
    {
        using var workspace = TemporaryWorkspace.Create();
        var legacy = new FileSystemChunkRepository(workspace.RepositoryPath, new FastCdcChunker(new(128, 256, 512)), new(), new());
        await legacy.CommitAsync(Request(Path.Combine(workspace.RootPath, "document.txt"), [1, 2, 3]));
        var before = Snapshot(workspace.RepositoryPath);
        var bound = Create(Binding(workspace));
        await Rejected(() => bound.ListVersionsAsync(), RepositoryIntegrityFailure.OwnershipMismatch);
        await Rejected(() => bound.ProvisionVaultStorageAsync(), RepositoryIntegrityFailure.OwnershipMismatch);
        Assert.Equal(before, Snapshot(workspace.RepositoryPath));
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("root")]
    [InlineData("state")]
    [InlineData("endpoint")]
    public async Task Changed_binding_cannot_read_or_mutate_an_existing_root(string changed)
    {
        using var workspace = TemporaryWorkspace.Create();
        var binding = Binding(workspace);
        var repo = Create(binding);
        await repo.ProvisionVaultStorageAsync();
        await repo.CommitAsync(Request(Path.Combine(workspace.RootPath, "document.txt"), [1, 2, 3]));
        var other = changed switch
        {
            "vault" => binding with { Id = VaultId.New() },
            "state" => binding with { StateRoot = Path.Combine(workspace.RootPath, "other-state") },
            "endpoint" => binding with { MetadataStore = binding.MetadataStore with { DatabaseName = "other" } },
            _ => binding with { RepositoryPath = Path.Combine(workspace.RootPath, "copied-root") }
        };
        if (changed == "root")
        {
            Directory.CreateDirectory(other.RepositoryPath);
            File.Copy(Path.Combine(binding.RepositoryPath, StorageOwnership.MarkerName), Path.Combine(other.RepositoryPath, StorageOwnership.MarkerName));
        }
        var before = Snapshot(binding.RepositoryPath);
        var targetBefore = Snapshot(other.RepositoryPath);
        await Rejected(() => Create(other).ListVersionsAsync(), RepositoryIntegrityFailure.OwnershipMismatch);
        Assert.Equal(before, Snapshot(binding.RepositoryPath));
        Assert.Equal(targetBefore, Snapshot(other.RepositoryPath));
    }

    [Fact]
    public async Task Foreign_mirror_marker_rejects_capture_before_content_publication()
    {
        using var workspace = TemporaryWorkspace.Create();
        var binding = Binding(workspace);
        await Create(binding).ProvisionVaultStorageAsync();
        var other = binding with { Id = VaultId.New(), RepositoryPath = Path.Combine(workspace.RootPath, "other"), StateRoot = Path.Combine(workspace.RootPath, "other-state") };
        var mirrorPath = Path.Combine(workspace.RootPath, "mirror");
        var mirror = new MirrorSetConfiguration([new("mirror", "Mirror", mirrorPath, true)]);
        await Create(other, mirror).ProvisionVaultStorageAsync();
        var before = Snapshot(other.RepositoryPath) + Snapshot(mirrorPath) + Snapshot(binding.RepositoryPath);
        await Rejected(() => Create(binding, mirror).CommitAsync(Request(Path.Combine(workspace.RootPath, "document.txt"), [1, 2, 3])), RepositoryIntegrityFailure.OwnershipMismatch);
        Assert.Equal(before, Snapshot(other.RepositoryPath) + Snapshot(mirrorPath) + Snapshot(binding.RepositoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_foreign_manifest_identity_preserves_recovery_destination(bool foreign)
    {
        using var workspace = TemporaryWorkspace.Create();
        var binding = Binding(workspace);
        var repo = Create(binding);
        await repo.ProvisionVaultStorageAsync();
        var captured = await repo.CommitAsync(Request(Path.Combine(workspace.RootPath, "document.txt"), [1, 2, 3]));
        var manifest = captured.Manifest with { VaultId = foreign ? VaultId.New() : null };
        await File.WriteAllTextAsync(Path.Combine(binding.RepositoryPath, "manifests", manifest.VersionId + ".json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var destination = Path.Combine(workspace.RootPath, "recovered.txt");
        await File.WriteAllTextAsync(destination, "existing work");
        await Rejected(() => repo.RestoreAsync(manifest.VersionId, destination), RepositoryIntegrityFailure.OwnershipMismatch);
        Assert.Equal("existing work", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Public_service_constructor_rejects_unbound_or_different_metadata_store()
    {
        using var workspace = TemporaryWorkspace.Create();
        var binding = Binding(workspace);
        await using var legacy = new PostgreSqlRepositoryMetadataStore(binding.MetadataStore);
        Assert.Throws<ArgumentException>(() => new FileSystemChunkRepository(binding, new(new(128, 256, 512)), new(), new(), null, legacy));
        await using var other = new PostgreSqlRepositoryMetadataStore(binding with { Id = VaultId.New() });
        Assert.Throws<ArgumentException>(() => new FileSystemChunkRepository(binding, new(new(128, 256, 512)), new(), new(), null, other));
        Assert.False(Directory.Exists(binding.RepositoryPath));
    }

    private static VaultBinding Binding(TemporaryWorkspace workspace) => new(VaultId.New(), workspace.RepositoryPath,
        Path.Combine(workspace.RootPath, "state"), MetadataStoreConfiguration.CreateDefault(workspace.RootPath));
    // This test-only internal construction exercises marker/manifest invariants without a database.
    // The public service constructor requires the actual bound PostgreSQL store.
    private static FileSystemChunkRepository Create(VaultBinding binding, MirrorSetConfiguration? mirror = null) =>
        new(binding.RepositoryPath, new FastCdcChunker(new(128, 256, 512)), new(), new(), mirror, null, null, null, binding);
    private static FileCommitRequest Request(string source, byte[] content, string? folder = null) =>
        new("work", source, DateTimeOffset.UtcNow, CaptureConsistency.BestEffort, CompressionPreference.Off, 1024, new MemoryStream(content), WatchedFolderPath: folder);
    private static string Snapshot(string root) => !Directory.Exists(root) ? "missing" : string.Join("\n",
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));
    private static async Task Rejected(Func<Task> action, RepositoryIntegrityFailure expected)
    {
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(action);
        Assert.Equal(expected, error.Code);
    }
}
