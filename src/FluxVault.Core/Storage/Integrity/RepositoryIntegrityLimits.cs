namespace FluxVault.Core.Storage.Integrity;

public sealed record RepositoryIntegrityLimits(
    int MaxDecodedChunkBytes = 16 * 1024 * 1024,
    int MaxStoredChunkBytes = 16 * 1024 * 1024,
    int MaxDecoderWindowBytes = 16 * 1024 * 1024,
    int MaxSidecarBytes = 4096,
    int MaxManifestBytes = 64 * 1024 * 1024,
    int MaxGraphDepth = 128,
    int MaxGraphNodes = 100_000,
    int MaxExpandedEntries = 100_000,
    int MaxExpandedChunkReferences = 1_000_000,
    long MaxRestoreMetadataBytes = 128 * 1024 * 1024)
{
    public void Validate()
    {
        if (MaxDecodedChunkBytes <= 0 || MaxStoredChunkBytes <= 0 || MaxDecoderWindowBytes < 1024 ||
            MaxSidecarBytes <= 0 || MaxManifestBytes <= 0 || MaxGraphDepth <= 0 || MaxGraphNodes <= 0 ||
            MaxExpandedEntries <= 0 || MaxExpandedChunkReferences <= 0 || MaxRestoreMetadataBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(RepositoryIntegrityLimits), "Integrity limits must be positive.");
    }
}
