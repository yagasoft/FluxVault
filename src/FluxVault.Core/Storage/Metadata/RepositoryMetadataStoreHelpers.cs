using System.Text;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Content;

namespace FluxVault.Core.Storage.Metadata;

internal static class RepositoryMetadataStoreHelpers
{
    private static readonly Blake3ContentHasher Hasher = new();

    public static RepositoryVersionSummary ToSummary(FileVersionManifest manifest)
    {
        return new RepositoryVersionSummary(
            manifest.VersionId,
            manifest.SourcePath,
            manifest.CapturedAtUtc,
            manifest.Consistency,
            manifest.LogicalLength,
            manifest.Chunks.Count,
            manifest.OperationType,
            manifest.ParentVersionIds,
            manifest.RestoredFromVersionId,
            manifest.ForkOriginVersionId,
            manifest.InheritedFromVersionId,
            manifest.InheritedFromSourcePath,
            GetContentSignature(manifest),
            manifest.SyncOrigin,
            manifest.EntryKind,
            manifest.IsDeleted,
            manifest.FolderEntries,
            manifest.DeletedFromVersionId,
            manifest.SourceLastWriteUtc);
    }

    public static IReadOnlyList<RepositoryVersionSummary> ToVersionSummaries(
        IEnumerable<FileVersionManifest> manifests)
    {
        return manifests
            .Select(ToSummary)
            .OrderByDescending(version => version.CapturedAtUtc)
            .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<RepositoryVersionSummary> ToLatestEntrySummaries(
        IEnumerable<FileVersionManifest> manifests)
    {
        return manifests
            .GroupBy(manifest => ToEntryKey(manifest.SourcePath, manifest.EntryKind), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(manifest => manifest.CapturedAtUtc)
                .ThenByDescending(manifest => manifest.VersionId, StringComparer.Ordinal)
                .First())
            .Select(ToSummary)
            .OrderBy(version => version.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(version => version.EntryKind)
            .ToArray();
    }

    public static string ToEntryKey(string sourcePath, RepositoryEntryKind entryKind)
    {
        return $"{Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant()}|{entryKind}";
    }

    public static string GetContentSignature(FileVersionManifest manifest)
    {
        return manifest.ContentSignature ?? ComputeContentSignature(manifest.LogicalLength, manifest.Chunks);
    }

    private static string ComputeContentSignature(long logicalLength, IReadOnlyList<ManifestChunk> chunks)
    {
        var builder = new StringBuilder();
        builder.Append("fv-content-v1:").Append(logicalLength);
        foreach (var chunk in chunks.OrderBy(chunk => chunk.Offset))
        {
            builder
                .Append('|')
                .Append(chunk.Offset)
                .Append(':')
                .Append(chunk.Digest)
                .Append(':')
                .Append(chunk.Length);
        }

        return Hasher.Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }
}
