using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Storage.Integrity;

internal static class ChunkDescriptorLookup
{
    internal static ChunkDescriptor? Resolve(IEnumerable<ManifestChunk> chunks)
    {
        ChunkDescriptor? descriptor = null;
        foreach (var chunk in chunks)
        {
            var candidate = ChunkDescriptor.FromChunk(chunk);
            if (descriptor is not null && descriptor != candidate)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, $"Digest {candidate.Digest} has conflicting acknowledged representations.");
            descriptor = candidate;
        }
        return descriptor;
    }
}
