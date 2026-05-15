using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Storage.Metadata;

public sealed record MetadataPathRow(
    string SourcePath,
    RepositoryEntryKind EntryKind);

public sealed record MetadataVersionRow(
    string VersionId,
    string SourcePath,
    RepositoryEntryKind EntryKind,
    string WatchedFolderId,
    DateTimeOffset CapturedAtUtc,
    CaptureConsistency Consistency,
    long LogicalLength,
    VersionOperationType OperationType,
    bool IsDeleted,
    string? ContentSignature,
    string? RestoredFromVersionId,
    string? ForkOriginVersionId,
    string? InheritedFromVersionId,
    string? InheritedFromSourcePath,
    string? DeletedFromVersionId,
    DateTimeOffset? SourceLastWriteUtc);

public sealed record MetadataChunkRow(
    string Digest,
    int StoredLength,
    ChunkEncoding Encoding);

public sealed record MetadataVersionChunkRow(
    string VersionId,
    int ChunkOrdinal,
    string Digest,
    long Offset,
    int Length,
    int StoredLength,
    ChunkEncoding Encoding);

public sealed record MetadataLineageEdgeRow(
    string VersionId,
    string ParentVersionId);

public sealed record MetadataFolderEntryRow(
    string VersionId,
    string Name,
    string SourcePath,
    RepositoryEntryKind EntryKind,
    string ChildVersionId,
    bool IsDeleted,
    long LogicalLength,
    DateTimeOffset CapturedAtUtc);

public sealed record MetadataManifestProjection(
    MetadataPathRow Path,
    MetadataVersionRow Version,
    IReadOnlyList<MetadataChunkRow> Chunks,
    IReadOnlyList<MetadataVersionChunkRow> VersionChunks,
    IReadOnlyList<MetadataLineageEdgeRow> LineageEdges,
    IReadOnlyList<MetadataFolderEntryRow> FolderEntries)
{
    public static MetadataManifestProjection FromManifest(FileVersionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new MetadataManifestProjection(
            new MetadataPathRow(manifest.SourcePath, manifest.EntryKind),
            new MetadataVersionRow(
                manifest.VersionId,
                manifest.SourcePath,
                manifest.EntryKind,
                manifest.WatchedFolderId,
                manifest.CapturedAtUtc,
                manifest.Consistency,
                manifest.LogicalLength,
                manifest.OperationType,
                manifest.IsDeleted,
                manifest.ContentSignature,
                manifest.RestoredFromVersionId,
                manifest.ForkOriginVersionId,
                manifest.InheritedFromVersionId,
                manifest.InheritedFromSourcePath,
                manifest.DeletedFromVersionId,
                manifest.SourceLastWriteUtc),
            manifest.Chunks
                .GroupBy(chunk => chunk.Digest, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var chunk = group.First();
                    return new MetadataChunkRow(chunk.Digest, chunk.StoredLength, chunk.Encoding);
                })
                .ToArray(),
            manifest.Chunks
                .Select((chunk, index) => new MetadataVersionChunkRow(
                    manifest.VersionId,
                    index,
                    chunk.Digest,
                    chunk.Offset,
                    chunk.Length,
                    chunk.StoredLength,
                    chunk.Encoding))
                .ToArray(),
            (manifest.ParentVersionIds ?? [])
                .Select(parent => new MetadataLineageEdgeRow(manifest.VersionId, parent))
                .ToArray(),
            (manifest.FolderEntries ?? [])
                .Select(entry => new MetadataFolderEntryRow(
                    manifest.VersionId,
                    entry.Name,
                    entry.SourcePath,
                    entry.EntryKind,
                    entry.VersionId,
                    entry.IsDeleted,
                    entry.LogicalLength,
                    entry.CapturedAtUtc))
                .ToArray());
    }
}

public static class ManifestMetadataImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<IReadOnlyList<MetadataManifestProjection>> ImportAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var manifestsPath = Path.Combine(repositoryPath, "manifests");
        if (!Directory.Exists(manifestsPath))
        {
            return [];
        }

        var projections = new List<MetadataManifestProjection>();
        foreach (var path in Directory.EnumerateFiles(manifestsPath, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<FileVersionManifest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException($"Manifest could not be read: {path}");
            projections.Add(MetadataManifestProjection.FromManifest(manifest));
        }

        return projections;
    }
}
