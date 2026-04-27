using System.Text.Json;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Retention;

namespace FluxVault.Core.Storage;

public sealed class FileSystemChunkRepository : IChunkRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string rootPath;
    private readonly string? mirrorPath;
    private readonly StreamingFastCdcChunker streamingChunker;
    private readonly Blake3ContentHasher hasher;
    private readonly ZstdChunkCodec codec;

    public FileSystemChunkRepository(
        string rootPath,
        FastCdcChunker chunker,
        Blake3ContentHasher hasher,
        ZstdChunkCodec codec,
        string? mirrorPath = null)
    {
        this.rootPath = rootPath;
        streamingChunker = new StreamingFastCdcChunker(chunker.Options);
        this.hasher = hasher;
        this.codec = codec;
        this.mirrorPath = mirrorPath;
    }

    public async Task<FileCommitResult> CommitAsync(FileCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(ChunksPath(rootPath));
        Directory.CreateDirectory(ManifestsPath(rootPath));

        await using var content = request.Content;
        var chunks = new List<ManifestChunk>();
        var newChunkCount = 0;
        var logicalLength = 0L;

        await foreach (var chunk in streamingChunker.ChunkAsync(content, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = chunk.Payload.AsSpan();
            var digest = hasher.Hash(raw);
            var metadata = ReadChunkMetadata(rootPath, digest);

            if (metadata is null)
            {
                var prepared = PreparePayload(raw, digest, request.Compression, request.MinimumCompressionBytes);
                AtomicWrite(ChunkPath(rootPath, digest), prepared.Payload);
                AtomicWrite(MetadataPath(rootPath, digest), JsonSerializer.SerializeToUtf8Bytes(prepared.Metadata, JsonOptions));
                MirrorChunkIfNeeded(digest, prepared);
                metadata = prepared.Metadata;
                newChunkCount++;
            }

            chunks.Add(new ManifestChunk(digest, chunk.Offset, chunk.Payload.Length, metadata.StoredLength, metadata.Encoding));
            logicalLength += chunk.Payload.Length;
        }

        var manifest = new FileVersionManifest(
            VersionId: Guid.CreateVersion7().ToString("N"),
            WatchedFolderId: request.WatchedFolderId,
            SourcePath: request.SourcePath,
            CapturedAtUtc: request.CapturedAtUtc,
            Consistency: request.Consistency,
            LogicalLength: logicalLength,
            Chunks: chunks);

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        AtomicWrite(ManifestPath(rootPath, manifest.VersionId), manifestBytes);
        MirrorManifestIfNeeded(manifest.VersionId, manifestBytes);

        return new FileCommitResult(manifest, newChunkCount);
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ManifestsPath(rootPath)))
        {
            return [];
        }

        var versions = new List<RepositoryVersionSummary>();
        foreach (var path in Directory.EnumerateFiles(ManifestsPath(rootPath), "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
            versions.Add(new RepositoryVersionSummary(
                manifest.VersionId,
                manifest.SourcePath,
                manifest.CapturedAtUtc,
                manifest.Consistency,
                manifest.LogicalLength,
                manifest.Chunks.Count));
        }

        return versions
            .OrderByDescending(version => version.CapturedAtUtc)
            .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        var manifest = await ReadManifestByVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        return new RepositoryInspection(
            manifest,
            manifest.Chunks.Count,
            manifest.LogicalLength,
            manifest.Chunks.Sum(chunk => (long)chunk.StoredLength));
    }

    public async Task RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var manifest = await ReadManifestByVersionAsync(versionId, cancellationToken).ConfigureAwait(false);

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var tempPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using var output = File.Create(tempPath);
            foreach (var chunk in manifest.Chunks.OrderBy(chunk => chunk.Offset))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = await File.ReadAllBytesAsync(ChunkPath(rootPath, chunk.Digest), cancellationToken);
                var bytes = chunk.Encoding == ChunkEncoding.Zstd
                    ? codec.Decompress(payload, chunk.Length)
                    : payload;

                await output.WriteAsync(bytes, cancellationToken);
            }

            output.Close();
            File.Move(tempPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public async Task<RepositoryRetentionPreview> PreviewRetentionAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var state = await BuildRetentionStateAsync(policy, nowUtc, cancellationToken).ConfigureAwait(false);
        return new RepositoryRetentionPreview(
            state.Decisions,
            state.KeptVersionCount,
            state.PrunableVersionCount,
            state.PrunableStorageBytes,
            GetRepositorySize(rootPath));
    }

    public async Task<RepositoryRetentionResult> ApplyRetentionAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var state = await BuildRetentionStateAsync(policy, nowUtc, cancellationToken).ConfigureAwait(false);
        var mirrorWarnings = new List<string>();
        var reclaimedBytes = 0L;

        foreach (var manifest in state.PrunableManifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reclaimedBytes += DeleteLocalFile(ManifestPath(rootPath, manifest.VersionId));
            DeleteMirrorFile(ManifestPath, manifest.VersionId, mirrorWarnings);
        }

        var deletedChunkCount = 0;
        foreach (var digest in state.PrunableChunkDigests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkBytes = DeleteLocalFile(ChunkPath(rootPath, digest));
            var metadataBytes = DeleteLocalFile(MetadataPath(rootPath, digest));
            if (chunkBytes > 0 || metadataBytes > 0)
            {
                deletedChunkCount++;
                reclaimedBytes += chunkBytes + metadataBytes;
            }

            DeleteMirrorFile(ChunkPath, digest, mirrorWarnings);
            DeleteMirrorFile(MetadataPath, digest, mirrorWarnings);
        }

        return new RepositoryRetentionResult(
            state.Decisions,
            state.KeptVersionCount,
            state.PrunableVersionCount,
            deletedChunkCount,
            reclaimedBytes,
            GetRepositorySize(rootPath),
            mirrorWarnings);
    }

    private PreparedChunk PreparePayload(
        ReadOnlySpan<byte> raw,
        string digest,
        CompressionPreference compression,
        int minimumCompressionBytes)
    {
        if (compression == CompressionPreference.Zstd && raw.Length >= minimumCompressionBytes)
        {
            var compressed = codec.Compress(raw, level: 3);
            if (compressed.Length < raw.Length)
            {
                return new PreparedChunk(
                    compressed,
                    new ChunkMetadata(digest, raw.Length, compressed.Length, ChunkEncoding.Zstd));
            }
        }

        return new PreparedChunk(
            raw.ToArray(),
            new ChunkMetadata(digest, raw.Length, raw.Length, ChunkEncoding.Raw));
    }

    private void MirrorChunkIfNeeded(string digest, PreparedChunk prepared)
    {
        if (mirrorPath is null)
        {
            return;
        }

        AtomicWrite(ChunkPath(mirrorPath, digest), prepared.Payload);
        AtomicWrite(MetadataPath(mirrorPath, digest), JsonSerializer.SerializeToUtf8Bytes(prepared.Metadata, JsonOptions));
    }

    private void MirrorManifestIfNeeded(string versionId, byte[] manifestBytes)
    {
        if (mirrorPath is null)
        {
            return;
        }

        AtomicWrite(ManifestPath(mirrorPath, versionId), manifestBytes);
    }

    private static ChunkMetadata? ReadChunkMetadata(string root, string digest)
    {
        var path = MetadataPath(root, digest);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ChunkMetadata>(File.ReadAllBytes(path), JsonOptions);
    }

    private async Task<FileVersionManifest> ReadManifestByVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestPath(rootPath, versionId);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest {versionId} was not found.", manifestPath);
        }

        return await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileVersionManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        await using var manifestStream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<FileVersionManifest>(manifestStream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Manifest {manifestPath} could not be read.");
    }

    private async Task<RetentionState> BuildRetentionStateAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var manifests = await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        var summaries = manifests.Select(manifest => new RepositoryVersionSummary(
            manifest.VersionId,
            manifest.SourcePath,
            manifest.CapturedAtUtc,
            manifest.Consistency,
            manifest.LogicalLength,
            manifest.Chunks.Count)).ToArray();

        var decisions = RetentionPlanner.Decide(summaries, policy, nowUtc).ToArray();
        var prunableIds = decisions
            .Where(decision => !decision.Keep)
            .Select(decision => decision.VersionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prunableManifests = manifests
            .Where(manifest => prunableIds.Contains(manifest.VersionId))
            .ToArray();
        var keptManifests = manifests
            .Where(manifest => !prunableIds.Contains(manifest.VersionId))
            .ToArray();

        var keptDigests = keptManifests
            .SelectMany(manifest => manifest.Chunks.Select(chunk => chunk.Digest))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prunableChunkDigests = prunableManifests
            .SelectMany(manifest => manifest.Chunks.Select(chunk => chunk.Digest))
            .Where(digest => !keptDigests.Contains(digest))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RetentionState(
            decisions,
            decisions.Count(decision => decision.Keep),
            prunableManifests.Length,
            prunableManifests,
            prunableChunkDigests,
            prunableChunkDigests.Sum(GetLocalChunkStorageBytes));
    }

    private async Task<IReadOnlyList<FileVersionManifest>> ReadAllManifestsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ManifestsPath(rootPath)))
        {
            return [];
        }

        var manifests = new List<FileVersionManifest>();
        foreach (var path in Directory.EnumerateFiles(ManifestsPath(rootPath), "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifests.Add(await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return manifests;
    }

    private long GetLocalChunkStorageBytes(string digest)
    {
        return GetFileLength(ChunkPath(rootPath, digest)) + GetFileLength(MetadataPath(rootPath, digest));
    }

    private static long GetRepositorySize(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(GetFileLength);
    }

    private static long DeleteLocalFile(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var length = new FileInfo(path).Length;
        File.Delete(path);
        return length;
    }

    private void DeleteMirrorFile(Func<string, string, string> pathFactory, string key, List<string> warnings)
    {
        if (mirrorPath is null)
        {
            return;
        }

        var path = pathFactory(mirrorPath, key);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not delete mirror artefact {path}: {exception.Message}");
        }
    }

    private static long GetFileLength(string path)
    {
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Path has no directory: {path}");
        Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            return;
        }

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(tempPath, bytes);
        try
        {
            File.Move(tempPath, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(tempPath);
        }
    }

    private static string ChunksPath(string root) => Path.Combine(root, "chunks");

    private static string ManifestsPath(string root) => Path.Combine(root, "manifests");

    private static string ChunkPath(string root, string digest)
    {
        return Path.Combine(ChunksPath(root), digest[..2], $"{digest}.chunk");
    }

    private static string MetadataPath(string root, string digest)
    {
        return Path.Combine(ChunksPath(root), digest[..2], $"{digest}.json");
    }

    private static string ManifestPath(string root, string versionId)
    {
        return Path.Combine(ManifestsPath(root), $"{versionId}.json");
    }

    private sealed record PreparedChunk(byte[] Payload, ChunkMetadata Metadata);

    private sealed record RetentionState(
        IReadOnlyList<RepositoryVersionRetentionDecision> Decisions,
        int KeptVersionCount,
        int PrunableVersionCount,
        IReadOnlyList<FileVersionManifest> PrunableManifests,
        IReadOnlyList<string> PrunableChunkDigests,
        long PrunableStorageBytes);
}
