using System.Text.Json;

namespace FluxVault.Core.Storage.Integrity;

internal static class StorageOwnership
{
    internal const string MarkerName = ".fluxvault-storage.json";
    internal const string LockName = ".fluxvault.lock";
    private sealed record Marker(int FormatVersion, string StorageId, string Role);
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

    internal static string Ensure(string root, string? storageId, string role)
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
                if (marker is null || marker.FormatVersion != 1 || !Guid.TryParseExact(marker.StorageId, "N", out _) ||
                    marker.Role != role || (storageId is not null && marker.StorageId != storageId))
                    throw new JsonException();
                return marker.StorageId;
            }
            catch (JsonException)
            {
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Storage ownership marker is invalid or belongs to another repository.");
            }
        }

        if (Directory.EnumerateFileSystemEntries(root).Any(entry => Path.GetFileName(entry) != LockName &&
            !(Directory.Exists(entry) && new[] { "chunks", "manifests", "lineage", "journal" }.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase) &&
              !Directory.EnumerateFileSystemEntries(entry).Any())))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipUnknown, "Non-empty storage has no ownership marker. Existing data was preserved; choose a new storage root.");
        var id = storageId ?? Guid.NewGuid().ToString("N");
        using var output = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, new Marker(1, id, role), JsonOptions);
        output.Flush(flushToDisk: true);
        return id;
    }
}
