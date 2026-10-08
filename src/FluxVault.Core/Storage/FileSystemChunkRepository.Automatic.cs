using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using FluxVault.Core.Storage.Integrity;

namespace FluxVault.Core.Storage;

public sealed partial class FileSystemChunkRepository
{
    public async Task<int> DrainMetadataOutboxAsync(CancellationToken ct = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None,ct).ConfigureAwait(false);
        return metadataStore is null ? 0 : await metadataStore.ExportOutboxAsync(rootPath,ct).ConfigureAwait(false);
    }

    /// <summary>Only a missing reserved manifest is absence; invalid content remains uncertain.</summary>
    public async Task<FileVersionManifest?> VerifyAutomaticResultAsync(AutomaticPathWork work, CancellationToken ct = default)
    {
        if (work.ReservedVersionId is null) throw new ArgumentException("A reserved capture identity is required.");
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None,ct).ConfigureAwait(false);
        FileVersionManifest manifest;
        try { manifest=await ReadManifestByVersionAsync(work.ReservedVersionId,ct).ConfigureAwait(false); }
        catch(FileNotFoundException) { return null; }
        if (manifest.VersionId!=work.ReservedVersionId || manifest.VaultId!=binding?.Id ||
            manifest.WatchedFolderId!=work.ScopeId || manifest.EntryKind!=RepositoryEntryKind.File ||
            !string.Equals(Path.GetFullPath(manifest.SourcePath),Path.GetFullPath(work.SourcePath),StringComparison.OrdinalIgnoreCase))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch,"The interrupted capture does not match its reserved source and vault.");
        await ValidateManifestGraphAsync(manifest,ReadManifestByVersionAsync,ct).ConfigureAwait(false);
        foreach(var chunk in manifest.Chunks)
            await verifiedReader.ReadAsync(rootPath,ChunkDescriptor.FromChunk(chunk),ct).ConfigureAwait(false);
        return manifest;
    }
}
