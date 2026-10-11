using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using FluxVault.Core.Storage.Integrity;

namespace FluxVault.Core.Storage;

public sealed partial class FileSystemChunkRepository
{
    /// <summary>Replicate one retained primary result; the caller advances its durable cursor under this lease.</summary>
    public async Task RetryAutomaticMirrorVersionAsync(string scopeId,string sourcePath,string? versionId,
        Func<IReadOnlyList<string>,CancellationToken,Task> afterRetry,CancellationToken ct=default,
        IReadOnlyList<string>? unavailableMirrors=null)
    {
        await using var lease=await AcquireLeaseAsync(MirrorLeaseMode.BestEffort,ct).ConfigureAwait(false);
        FileVersionManifest? manifest=null;
        if(versionId is not null)
        {
            try {manifest=await ReadManifestByVersionAsync(versionId,ct).ConfigureAwait(false);}
            catch(FileNotFoundException) { } // Authoritative deletion cannot resurrect mirror history.
        }
        if(manifest is null) {await afterRetry([],ct).ConfigureAwait(false);return;}
        if(manifest.VersionId!=versionId || manifest.VaultId!=binding?.Id || manifest.WatchedFolderId!=scopeId ||
            manifest.EntryKind!=RepositoryEntryKind.File ||
            !string.Equals(Path.GetFullPath(manifest.SourcePath),Path.GetFullPath(sourcePath),StringComparison.OrdinalIgnoreCase))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch,"The mirror retry does not match its retained source and vault.");
        await ValidateManifestGraphAsync(manifest,ReadManifestByVersionAsync,ct).ConfigureAwait(false);
        var warnings=new List<string>(lease.Warnings);warnings.AddRange(unavailableMirrors ?? []);
        var usedBytes=new Dictionary<string,long>(GetMirrorNodeUsedBytes(),StringComparer.OrdinalIgnoreCase);
        foreach(var chunk in manifest.Chunks)
        {
            var descriptor=ChunkDescriptor.FromChunk(chunk);
            await verifiedReader.ReadAsync(rootPath,descriptor,ct).ConfigureAwait(false);
            var selection=SelectVerifiedMirrorTargets(chunk,usedBytes);
            if(selection.IsUnderSatisfied)warnings.Add($"Mirror placement for chunk {chunk.Digest} remains under-satisfied.");
            foreach(var node in selection.TargetNodes)
            {
                try
                {
                    var oldLength=GetFileLength(ChunkPath(node.Path,chunk.Digest))+GetFileLength(MetadataPath(node.Path,chunk.Digest));
                    await publisher.RepairAsync(rootPath,node.Path,descriptor,ct).ConfigureAwait(false);
                    usedBytes[node.Id]+=GetFileLength(ChunkPath(node.Path,chunk.Digest))+GetFileLength(MetadataPath(node.Path,chunk.Digest))-oldLength;
                }
                catch(Exception exception) when(IsMirrorIoFailure(exception))
                {warnings.Add(FormatMirrorWarning(node,"retry verified chunk "+chunk.Digest,exception));}
            }
        }
        if(metadataStore is null)
            warnings.AddRange(MirrorManifestIfNeeded(manifest.VersionId,System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest,JsonOptions)));
        ct.ThrowIfCancellationRequested();
        await afterRetry(CondenseMirrorWarnings(warnings),ct).ConfigureAwait(false);
    }

    public async Task<int> DrainMetadataOutboxAsync(CancellationToken ct = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None,ct).ConfigureAwait(false);
        return metadataStore is null ? 0 : await metadataStore.ExportOutboxAsync(rootPath,ct).ConfigureAwait(false);
    }

    /// <summary>Only a missing reserved manifest is absence; invalid content remains uncertain.</summary>
    public async Task<FileVersionManifest?> VerifyAutomaticResultAsync(AutomaticPathWork work, CancellationToken ct = default,
        Func<FileVersionManifest?,CancellationToken,Task>? afterVerification = null)
    {
        if (work.ReservedVersionId is null) throw new ArgumentException("A reserved capture identity is required.");
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None,ct).ConfigureAwait(false);
        FileVersionManifest manifest;
        try { manifest=await ReadManifestByVersionAsync(work.ReservedVersionId,ct).ConfigureAwait(false); }
        catch(FileNotFoundException)
        {
            if (afterVerification is not null) await afterVerification(null,ct).ConfigureAwait(false);
            return null;
        }
        if (manifest.VersionId!=work.ReservedVersionId || manifest.VaultId!=binding?.Id ||
            manifest.WatchedFolderId!=work.ScopeId || manifest.EntryKind!=RepositoryEntryKind.File ||
            manifest.IsDeleted!=work.IsDeletion ||
            (work.IsDeletion && (manifest.OperationType!=VersionOperationType.Delete ||
                work.DeletedFromVersionId is null || manifest.DeletedFromVersionId!=work.DeletedFromVersionId ||
                manifest.ParentVersionIds?.Count!=1 || manifest.ParentVersionIds[0]!=work.DeletedFromVersionId)) ||
            !string.Equals(Path.GetFullPath(manifest.SourcePath),Path.GetFullPath(work.SourcePath),StringComparison.OrdinalIgnoreCase))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch,"The interrupted capture does not match its reserved source and vault.");
        await ValidateManifestGraphAsync(manifest,ReadManifestByVersionAsync,ct).ConfigureAwait(false);
        foreach(var chunk in manifest.Chunks)
            await verifiedReader.ReadAsync(rootPath,ChunkDescriptor.FromChunk(chunk),ct).ConfigureAwait(false);
        if (afterVerification is not null) await afterVerification(manifest,ct).ConfigureAwait(false);
        return manifest;
    }
}
