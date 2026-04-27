using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Storage;

internal sealed record ChunkMetadata(
    string Digest,
    int LogicalLength,
    int StoredLength,
    ChunkEncoding Encoding);
