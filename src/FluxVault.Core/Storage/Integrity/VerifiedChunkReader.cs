using System.Text.Json;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Content;

namespace FluxVault.Core.Storage.Integrity;

internal sealed class VerifiedChunkReader(Blake3ContentHasher hasher, ZstdChunkCodec codec, RepositoryIntegrityLimits limits)
{
    internal static string PayloadPath(string root, string digest) => Path.Combine(root, "chunks", digest[..2], digest + ".chunk");
    internal static string MetadataPath(string root, string digest) => Path.Combine(root, "chunks", digest[..2], digest + ".json");

    internal static void ValidateHex(string? value, int length, string name)
    {
        if (value is null || value.Length != length || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, $"Invalid {name}.");
    }

    internal void ValidateDescriptor(ChunkDescriptor expected)
    {
        ValidateHex(expected.Digest, 64, "chunk digest");
        if (!Enum.IsDefined(expected.Encoding) || expected.LogicalLength <= 0 || expected.StoredLength <= 0)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Invalid chunk descriptor.");
        if (expected.LogicalLength > limits.MaxDecodedChunkBytes || expected.StoredLength > limits.MaxStoredChunkBytes)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "Chunk exceeds the supported integrity limit.");
    }

    internal async Task<byte[]> ReadAsync(string root, ChunkDescriptor expected, CancellationToken cancellationToken)
    {
        var stored = await ReadStoredAsync(root, expected, cancellationToken).ConfigureAwait(false);
        return DecodeAndVerify(stored, expected);
    }

    internal byte[] DecodeAndVerify(ReadOnlySpan<byte> stored, ChunkDescriptor expected)
    {
        ValidateDescriptor(expected);
        if (stored.Length != expected.StoredLength)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Stored chunk length mismatch.");
        var decoded = codec.Decompress(stored, expected.LogicalLength, expected.Encoding, limits);
        if (!string.Equals(hasher.Hash(decoded), expected.Digest, StringComparison.OrdinalIgnoreCase))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, $"Chunk {expected.Digest} failed content verification.");
        return decoded;
    }

    internal async Task<ChunkMetadata> ReadMetadataAsync(string root, string digest, CancellationToken cancellationToken)
    {
        ValidateHex(digest, 64, "chunk digest");
        try
        {
            var json = await ReadBoundedAsync(MetadataPath(root, digest), limits.MaxSidecarBytes, cancellationToken).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize<ChunkMetadata>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (metadata is null || !string.Equals(metadata.Digest, digest, StringComparison.OrdinalIgnoreCase))
                throw new JsonException();
            ValidateDescriptor(new ChunkDescriptor(digest, metadata.LogicalLength, metadata.StoredLength, metadata.Encoding));
            return metadata;
        }
        catch (JsonException)
        {
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Chunk sidecar is invalid.");
        }
    }

    internal async Task<byte[]> ReadStoredAsync(string root, ChunkDescriptor expected, CancellationToken cancellationToken)
    {
        ValidateDescriptor(expected);
        try
        {
            var json = await ReadBoundedAsync(MetadataPath(root, expected.Digest), limits.MaxSidecarBytes, cancellationToken).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize<ChunkMetadata>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (metadata is null || !string.Equals(metadata.Digest, expected.Digest, StringComparison.OrdinalIgnoreCase) ||
                metadata.LogicalLength != expected.LogicalLength || metadata.StoredLength != expected.StoredLength || metadata.Encoding != expected.Encoding)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Chunk sidecar disagrees with the acknowledged descriptor.");
            return await ReadBoundedAsync(PayloadPath(root, expected.Digest), limits.MaxStoredChunkBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Chunk sidecar is invalid.");
        }
        catch (FileNotFoundException)
        {
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.MissingObject, "Stored chunk or sidecar is missing; repair is required.");
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        StorageOwnership.RejectReparseComponents(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "Stored object exceeds its supported size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Stored object changed during reading.");
        return bytes;
    }
}
