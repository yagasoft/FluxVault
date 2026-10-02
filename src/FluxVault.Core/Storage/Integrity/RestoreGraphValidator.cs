using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Storage.Integrity;

internal sealed record PlannedRestoreFile(FileVersionManifest Manifest, string RelativePath);
internal sealed record ValidatedRestorePlan(RepositoryEntryKind Kind, IReadOnlyList<string> Directories,
    IReadOnlyList<PlannedRestoreFile> Files, long LogicalBytes);

internal sealed class RestoreGraphValidator(VerifiedChunkReader reader, RepositoryIntegrityLimits limits,
    Func<string, CancellationToken, Task<FileVersionManifest>> readManifest)
{
    private readonly Dictionary<string, (FileVersionManifest Manifest, int Bytes)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ancestors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChunkDescriptor> descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> directories = [];
    private readonly List<PlannedRestoreFile> files = [];
    private long metadataBytes;
    private long chunkReferences;
    private int expandedEntries;

    internal async Task<ValidatedRestorePlan> BuildAsync(FileVersionManifest root, string destination, CancellationToken cancellationToken)
    {
        Cache(root);
        var resolved = await VisitAsync(root.VersionId, "", destination, 1, null, cancellationToken).ConfigureAwait(false);
        return new ValidatedRestorePlan(resolved.EntryKind, directories, files, resolved.LogicalLength);
    }

    private void Cache(FileVersionManifest manifest)
    {
        VerifiedChunkReader.ValidateHex(manifest.VersionId, 32, "version id");
        var bytes = RepositoryManifestSize.Measure(manifest);
        if (bytes > limits.MaxManifestBytes || cache.Count >= limits.MaxGraphNodes) Limit();
        cache.Add(manifest.VersionId, (manifest, bytes));
    }

    private async Task<FileVersionManifest> VisitAsync(string versionId, string relativePath, string destination, int depth,
        FolderVersionEntry? expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifiedChunkReader.ValidateHex(versionId, 32, "version id");
        if (depth > limits.MaxGraphDepth || ++expandedEntries > limits.MaxExpandedEntries) Limit();
        if (!ancestors.Add(versionId)) Invalid("Manifest graph contains a cycle.");
        try
        {
            if (!cache.TryGetValue(versionId, out var item))
            {
                if (cache.Count >= limits.MaxGraphNodes) Limit();
                var loaded = await readManifest(versionId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(loaded.VersionId, versionId, StringComparison.OrdinalIgnoreCase)) Invalid("Referenced manifest identity mismatch.");
                Cache(loaded);
                item = cache[versionId];
            }
            // Charge every expansion, including reused DAG nodes, before retaining paths or descending.
            Charge(item.Bytes + 2L * Path.Combine(destination, relativePath).Length);
            var manifest = item.Manifest;
            if (!Enum.IsDefined(manifest.EntryKind) || manifest.LogicalLength < 0 || manifest.Chunks is null)
                Invalid("Invalid manifest kind or logical length.");
            if (expected is not null && (expected.EntryKind != manifest.EntryKind || expected.LogicalLength != manifest.LogicalLength || manifest.IsDeleted))
                Invalid("Folder entry disagrees with its referenced manifest.");
            if (manifest.IsDeleted)
            {
                if (manifest.Chunks.Count != 0) Invalid("Tombstone contains chunk data.");
                VerifiedChunkReader.ValidateHex(manifest.DeletedFromVersionId, 32, "deleted version id");
                var restored = await VisitAsync(manifest.DeletedFromVersionId!, relativePath, destination, depth + 1, null, cancellationToken).ConfigureAwait(false);
                if (manifest.EntryKind != restored.EntryKind || manifest.LogicalLength != restored.LogicalLength)
                    Invalid("Tombstone disagrees with its recovery target.");
                return restored;
            }
            if (manifest.EntryKind == RepositoryEntryKind.File)
            {
                if (manifest.FolderEntries is { Count: > 0 }) Invalid("File manifest contains folder entries.");
                RestoreManifestValidator.ValidateFile(manifest, reader);
                chunkReferences += manifest.Chunks.Count;
                if (chunkReferences > limits.MaxExpandedChunkReferences) Limit();
                foreach (var chunk in manifest.Chunks)
                {
                    var descriptor = ChunkDescriptor.FromChunk(chunk);
                    if (descriptors.TryGetValue(descriptor.Digest, out var existing) && existing != descriptor)
                        throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, "Restore graph contains conflicting chunk representations.");
                    descriptors[descriptor.Digest] = descriptor;
                }
                files.Add(new PlannedRestoreFile(manifest, relativePath));
                return manifest;
            }
            if (manifest.Chunks.Count != 0) Invalid("Folder manifest contains chunk data.");
            if (manifest.ContentSignature is not null && !string.Equals(manifest.ContentSignature,
                RepositoryMetadataStoreHelpers.ComputeFolderContentSignature(manifest.FolderEntries ?? []), StringComparison.OrdinalIgnoreCase))
                Invalid("Folder signature disagrees with its child sequence.");
            directories.Add(relativePath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in manifest.FolderEntries ?? [])
            {
                ValidateChildName(entry.Name);
                VerifiedChunkReader.ValidateHex(entry.VersionId, 32, "child version id");
                if (!Enum.IsDefined(entry.EntryKind) || entry.LogicalLength < 0) Invalid("Invalid folder entry.");
                if (entry.IsDeleted) continue;
                if (!names.Add(entry.Name)) Invalid("Folder entries collide under Windows name comparison.");
                var child = await VisitAsync(entry.VersionId, Path.Combine(relativePath, entry.Name), destination, depth + 1, entry, cancellationToken).ConfigureAwait(false);
                if (child.LogicalLength > long.MaxValue - total) Invalid("Folder length overflows.");
                total += child.LogicalLength;
            }
            if (total != manifest.LogicalLength) Invalid("Folder logical length disagrees with its children.");
            return manifest;
        }
        finally { ancestors.Remove(versionId); }
    }

    internal static void ValidateChildName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character))) Invalid("Folder contains an invalid Windows child name.");
        var basename = name!.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (basename is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
            (basename.Length == 4 && (basename.StartsWith("COM", StringComparison.Ordinal) || basename.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(basename[3]))) Invalid("Folder contains a reserved Windows device name.");
    }

    private void Charge(long bytes)
    {
        if (bytes > limits.MaxRestoreMetadataBytes - metadataBytes) Limit();
        metadataBytes += bytes;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid(string message) => throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, message);
    private static void Limit() => throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "Restore graph exceeds the supported integrity limit.");
}
