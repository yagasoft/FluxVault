using System.Text.Json;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;

namespace FluxVault.Core.Storage.Integrity;

internal static class StorageOwnership
{
    internal const string MarkerName = ".fluxvault-storage.json";
    internal const string LockName = ".fluxvault.lock";
    private sealed record Marker(int FormatVersion, string StorageId, string Role, VaultId? VaultId = null,
        string? BindingFingerprint = null);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static void RejectReparseComponents(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Reparse paths are unsupported for verified repository operations.");
    }

    internal static bool Contains(string parent, string child) =>
        string.Equals(Canonical(parent), Canonical(child), StringComparison.OrdinalIgnoreCase) ||
        Canonical(child).StartsWith(Canonical(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void AssertFreshVaultRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        if (File.Exists(Path.Combine(root, MarkerName)))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Existing storage is never adopted during vault provisioning.");
        if (Directory.EnumerateFileSystemEntries(root).Any(entry => Path.GetFileName(entry) != LockName))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipUnknown, "Vault provisioning requires fresh empty storage. Existing data was preserved.");
    }

    internal static string Ensure(string root, string? storageId, string role, VaultBinding? binding = null, bool provision = false)
    {
        var markerPath = Path.Combine(root, MarkerName);
        RejectReparseComponents(markerPath);
        if (File.Exists(markerPath))
        {
            try
            {
                using var input = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > 4096) throw new JsonException();
                var marker = JsonSerializer.Deserialize<Marker>(input, JsonOptions);
                if (marker is null || marker.FormatVersion != (binding is null ? 1 : 2) || !Guid.TryParseExact(marker.StorageId, "N", out _) ||
                    marker.Role != role || (storageId is not null && marker.StorageId != storageId))
                    throw new JsonException();
                if (binding is not null && (provision || marker.VaultId != binding.Id ||
                    marker.StorageId != binding.Id.Value.ToString("N") || marker.BindingFingerprint != Fingerprint(binding)))
                    throw new JsonException();
                return marker.StorageId;
            }
            catch (JsonException)
            {
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Storage ownership marker is invalid or belongs to another repository.");
            }
        }

        if (binding is not null && !provision)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipUnknown, "Vault storage has no bound ownership marker. It was not created or adopted.");
        if (binding is not null) AssertFreshVaultRoot(root);

        if (Directory.EnumerateFileSystemEntries(root).Any(entry => Path.GetFileName(entry) != LockName &&
            !(Directory.Exists(entry) && new[] { "chunks", "manifests", "lineage", "journal" }.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase) &&
              !Directory.EnumerateFileSystemEntries(entry).Any())))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipUnknown, "Non-empty storage has no ownership marker. Existing data was preserved; choose a new storage root.");
        var id = binding?.Id.Value.ToString("N") ?? storageId ?? Guid.NewGuid().ToString("N");
        using var output = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, new Marker(binding is null ? 1 : 2, id, role, binding?.Id,
            binding is null ? null : Fingerprint(binding)), JsonOptions);
        output.Flush(flushToDisk: true);
        return id;
    }

    private static string Fingerprint(VaultBinding binding) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            binding.Id, Root = Canonical(binding.RepositoryPath).ToUpperInvariant(), State = Canonical(binding.StateRoot).ToUpperInvariant(),
            binding.MetadataNamespace, Host = binding.MetadataStore.Host.ToUpperInvariant(), binding.MetadataStore.Port,
            binding.MetadataStore.DatabaseName, binding.MetadataStore.Username
        }, JsonOptions)));
}
