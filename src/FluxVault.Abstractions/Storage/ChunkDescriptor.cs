namespace FluxVault.Abstractions.Storage;

public sealed record ChunkDescriptor(string Digest, int LogicalLength, int StoredLength, ChunkEncoding Encoding)
{
    public static ChunkDescriptor FromChunk(ManifestChunk chunk) => new(
        chunk.Digest.ToLowerInvariant(), chunk.Length, chunk.StoredLength, chunk.Encoding);
}
