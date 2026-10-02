using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Storage.Integrity;

internal static class RestoreManifestValidator
{
    internal static void ValidateFile(FileVersionManifest manifest, VerifiedChunkReader reader)
    {
        VerifiedChunkReader.ValidateHex(manifest.VersionId, 32, "version id");
        if (manifest.LogicalLength < 0 || manifest.Chunks is null || manifest.EntryKind != RepositoryEntryKind.File || manifest.IsDeleted || manifest.FolderEntries is { Count: > 0 })
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Invalid file manifest.");
        long expectedOffset = 0;
        var descriptors = new Dictionary<string, ChunkDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in manifest.Chunks)
        {
            var descriptor = ChunkDescriptor.FromChunk(chunk);
            reader.ValidateDescriptor(descriptor);
            if (chunk.Offset != expectedOffset || chunk.Length > long.MaxValue - expectedOffset)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Chunk layout has a gap, overlap or overflowing length.");
            expectedOffset += chunk.Length;
            if (descriptors.TryGetValue(descriptor.Digest, out var existing) && existing != descriptor)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, "One digest has conflicting chunk descriptors.");
            descriptors[descriptor.Digest] = descriptor;
        }
        if (expectedOffset != manifest.LogicalLength)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Manifest logical length does not match its chunk layout.");
        if (manifest.ContentSignature is not null && !string.Equals(manifest.ContentSignature,
                RepositoryMetadataStoreHelpers.ComputeContentSignature(manifest.LogicalLength, manifest.Chunks), StringComparison.Ordinal))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Content signature disagrees with the canonical chunk sequence.");
    }
}
