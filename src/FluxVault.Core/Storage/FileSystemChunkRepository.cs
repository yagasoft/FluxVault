using System.Text.Json;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;

namespace FluxVault.Core.Storage;

public sealed class FileSystemChunkRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string rootPath;
    private readonly string? mirrorPath;
    private readonly FastCdcChunker chunker;
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
        this.chunker = chunker;
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
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var chunks = new List<ManifestChunk>();
        var newChunkCount = 0;

        foreach (var chunk in chunker.Chunk(bytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = bytes.AsSpan(chunk.Offset, chunk.Length);
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

            chunks.Add(new ManifestChunk(digest, chunk.Offset, chunk.Length, metadata.StoredLength, metadata.Encoding));
        }

        var manifest = new FileVersionManifest(
            VersionId: Guid.CreateVersion7().ToString("N"),
            WatchedFolderId: request.WatchedFolderId,
            SourcePath: request.SourcePath,
            CapturedAtUtc: request.CapturedAtUtc,
            Consistency: request.Consistency,
            LogicalLength: bytes.LongLength,
            Chunks: chunks);

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        AtomicWrite(ManifestPath(rootPath, manifest.VersionId), manifestBytes);
        MirrorManifestIfNeeded(manifest.VersionId, manifestBytes);

        return new FileCommitResult(manifest, newChunkCount);
    }

    public async Task RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var manifestPath = ManifestPath(rootPath, versionId);
        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<FileVersionManifest>(manifestStream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"Manifest {versionId} could not be read.");

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
}
