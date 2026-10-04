using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Retention;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Security;

namespace FluxVault.Core.Storage;

public sealed class FileSystemChunkRepository : IChunkRepository
{

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string rootPath;
    private readonly VaultBinding? binding;
    private readonly MirrorSetConfiguration mirrorSet;
    private IReadOnlyList<MirrorNodeConfiguration> mirrorNodes => leasedMirrorNodes;
    private readonly MirrorPlacementPlanner mirrorPlacementPlanner = new();
    private readonly StreamingFastCdcChunker streamingChunker;
    private readonly Blake3ContentHasher hasher;
    private readonly ZstdChunkCodec codec;
    private readonly IRepositoryMetadataStore? metadataStore;
    private readonly RepositoryIntegrityLimits integrityLimits;
    private readonly VerifiedChunkReader verifiedReader;
    private readonly RepositoryObjectPublisher publisher;
    private readonly RepositoryFaults? faults;
    private readonly IReadOnlyList<MirrorNodeConfiguration> configuredMirrorNodes;
    private IReadOnlyList<MirrorNodeConfiguration> leasedMirrorNodes = [];
    private List<FileVersionManifest>? mutationManifestCache;

    private async ValueTask<RepositoryLeaseSet> AcquireLeaseAsync(MirrorLeaseMode mode, CancellationToken cancellationToken)
    {
        if (binding is not null && metadataStore is PostgreSqlRepositoryMetadataStore boundStore)
            await boundStore.VerifyBindingAsync(cancellationToken).ConfigureAwait(false);
        var lease = await RepositoryLeaseSet.AcquireAsync(rootPath, configuredMirrorNodes.Select(node => node.Path).ToArray(), mode, cancellationToken, binding: binding).ConfigureAwait(false);
        foreach (var node in configuredMirrorNodes) lease.LabelMirror(StorageOwnership.Canonical(node.Path), node.Label);
        leasedMirrorNodes = configuredMirrorNodes.Where(node => lease.MirrorRoots.Contains(StorageOwnership.Canonical(node.Path), StringComparer.OrdinalIgnoreCase)).ToArray();
        return lease;
    }

    public FileSystemChunkRepository(
        string rootPath,
        FastCdcChunker chunker,
        Blake3ContentHasher hasher,
        ZstdChunkCodec codec,
        string? mirrorPath = null)
        : this(rootPath, chunker, hasher, codec, MirrorSetConfiguration.FromLegacyPath(mirrorPath), metadataStore: null)
    {
    }

    public FileSystemChunkRepository(
        string rootPath,
        FastCdcChunker chunker,
        Blake3ContentHasher hasher,
        ZstdChunkCodec codec,
        MirrorSetConfiguration? mirrorSet)
        : this(rootPath, chunker, hasher, codec, mirrorSet, metadataStore: null)
    {
    }

    public FileSystemChunkRepository(
        string rootPath,
        FastCdcChunker chunker,
        Blake3ContentHasher hasher,
        ZstdChunkCodec codec,
        MirrorSetConfiguration? mirrorSet,
        IRepositoryMetadataStore? metadataStore,
        RepositoryIntegrityLimits? integrityLimits = null)
        : this(rootPath, chunker, hasher, codec, mirrorSet, metadataStore, integrityLimits, null)
    {
    }

    internal FileSystemChunkRepository(string rootPath, FastCdcChunker chunker, Blake3ContentHasher hasher,
        ZstdChunkCodec codec, MirrorSetConfiguration? mirrorSet, IRepositoryMetadataStore? metadataStore,
        RepositoryIntegrityLimits? integrityLimits, RepositoryFaults? faults, VaultBinding? binding = null)
    {
        binding?.Validate();
        if (binding is not null && !string.Equals(StorageOwnership.Canonical(rootPath), StorageOwnership.Canonical(binding.RepositoryPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The repository root must match the immutable vault binding.", nameof(binding));
        this.binding = binding;
        this.rootPath = rootPath;
        streamingChunker = new StreamingFastCdcChunker(chunker.Options);
        this.hasher = hasher;
        this.codec = codec;
        this.mirrorSet = (mirrorSet ?? MirrorSetConfiguration.CreateDefault()).Normalise();
        configuredMirrorNodes = this.mirrorSet.EnabledNodes;
        leasedMirrorNodes = configuredMirrorNodes;
        this.metadataStore = metadataStore;
        this.integrityLimits = integrityLimits ?? new RepositoryIntegrityLimits();
        this.integrityLimits.Validate();
        if (chunker.Options.MaximumSize > this.integrityLimits.MaxDecodedChunkBytes)
            throw new ArgumentOutOfRangeException(nameof(chunker), "Chunker exceeds the verified read limit.");
        verifiedReader = new VerifiedChunkReader(hasher, codec, this.integrityLimits);
        this.faults = faults;
        publisher = new RepositoryObjectPublisher(verifiedReader, this.integrityLimits, faults);
    }

    internal async Task InitializeStorageAsync(CancellationToken cancellationToken)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None, cancellationToken).ConfigureAwait(false);
    }

    public FileSystemChunkRepository(VaultBinding binding, FastCdcChunker chunker, Blake3ContentHasher hasher,
        ZstdChunkCodec codec, MirrorSetConfiguration? mirrorSet, PostgreSqlRepositoryMetadataStore metadataStore,
        RepositoryIntegrityLimits? integrityLimits = null)
        : this(binding.RepositoryPath, chunker, hasher, codec, mirrorSet, metadataStore, integrityLimits, null, binding)
    {
        if (metadataStore.Binding != binding) throw new ArgumentException("The service repository requires its exact bound PostgreSQL metadata store.", nameof(metadataStore));
    }

    public async Task ProvisionVaultStorageAsync(CancellationToken cancellationToken = default)
    {
        if (binding is null) throw new InvalidOperationException("Explicit vault binding is required for provisioning.");
        if (metadataStore is PostgreSqlRepositoryMetadataStore boundStore)
            await boundStore.VerifyBindingAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await RepositoryLeaseSet.AcquireAsync(rootPath, configuredMirrorNodes.Select(node => node.Path).ToArray(),
            MirrorLeaseMode.Required, cancellationToken, binding: binding, provision: true).ConfigureAwait(false);
    }

    public async Task<FileCommitResult> CommitAsync(FileCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var content = request.Content;
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.BestEffort, cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(ChunksPath(rootPath));
            if (metadataStore is null)
            {
                Directory.CreateDirectory(ManifestsPath(rootPath));
            }

            var existingManifests = metadataStore is null
                ? await ReadAllManifestsForMutationAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var localReferences = existingManifests?.SelectMany(manifest => manifest.Chunks).ToLookup(chunk => chunk.Digest, StringComparer.OrdinalIgnoreCase);
            var verifiedDescriptors = new Dictionary<string, ChunkDescriptor>(StringComparer.OrdinalIgnoreCase);
            var mirrorUsedBytes = new Dictionary<string, long>(GetMirrorNodeUsedBytes(), StringComparer.OrdinalIgnoreCase);
            var chunks = new List<ManifestChunk>();
            var mirrorWarnings = new List<string>(lease.Warnings);
            var newChunkCount = 0;
            var logicalLength = 0L;

            await foreach (var chunk in streamingChunker.ChunkAsync(content, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var digest = hasher.Hash(chunk.Payload);
                if (!verifiedDescriptors.TryGetValue(digest, out var descriptor))
                {
                    descriptor = metadataStore is null
                        ? ChunkDescriptorLookup.Resolve(localReferences![digest])
                        : await metadataStore.FindChunkDescriptorAsync(digest, cancellationToken).ConfigureAwait(false);
                    if (descriptor is not null)
                    {
                        await verifiedReader.ReadAsync(rootPath, descriptor, cancellationToken).ConfigureAwait(false);
                    }
                    else if (File.Exists(ChunkPath(rootPath, digest)) || File.Exists(MetadataPath(rootPath, digest)))
                    {
                        var metadata = await verifiedReader.ReadMetadataAsync(rootPath, digest, cancellationToken).ConfigureAwait(false);
                        descriptor = new ChunkDescriptor(digest, metadata.LogicalLength, metadata.StoredLength, metadata.Encoding);
                        await verifiedReader.ReadAsync(rootPath, descriptor, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var prepared = PreparePayload(chunk.Payload, digest, request.Compression, request.MinimumCompressionBytes);
                        descriptor = new ChunkDescriptor(digest, chunk.Payload.Length, prepared.Payload.Length, prepared.Metadata.Encoding);
                        await publisher.PublishNewAsync(rootPath, descriptor, prepared.Payload, cancellationToken).ConfigureAwait(false);
                        mirrorWarnings.AddRange(await MirrorChunkIfNeededAsync(descriptor, mirrorUsedBytes, cancellationToken).ConfigureAwait(false));
                        newChunkCount++;
                    }
                    verifiedDescriptors[digest] = descriptor;
                }
                if (descriptor.LogicalLength != chunk.Payload.Length)
                    throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, "Captured bytes disagree with the acknowledged chunk length.");
                chunks.Add(new ManifestChunk(digest, chunk.Offset, chunk.Payload.Length, descriptor.StoredLength, descriptor.Encoding));
                logicalLength += chunk.Payload.Length;
            }

            var sourcePath = Path.GetFullPath(request.SourcePath);
            var contentSignature = ComputeContentSignature(logicalLength, chunks);
            var lineage = metadataStore is null
                ? request.SyncOrigin is not null
                    ? ResolveSyncLineage(sourcePath, existingManifests ?? [])
                    : ReadRestoreHint(sourcePath) is { } restoreHint
                    ? ResolveRestoreLineage(sourcePath, restoreHint, existingManifests ?? [])
                    : ResolveCaptureLineage(sourcePath, contentSignature, existingManifests ?? [])
                : await ResolveMetadataBackedLineageAsync(
                    sourcePath,
                    contentSignature,
                    request.SyncOrigin is not null,
                    ReadRestoreHint(sourcePath),
                    cancellationToken).ConfigureAwait(false);

            var manifest = new FileVersionManifest(
                VersionId: Guid.CreateVersion7().ToString("N"),
                WatchedFolderId: request.WatchedFolderId,
                SourcePath: sourcePath,
                CapturedAtUtc: request.CapturedAtUtc,
                Consistency: request.Consistency,
                LogicalLength: logicalLength,
                Chunks: chunks,
                OperationType: lineage.OperationType,
                ParentVersionIds: lineage.ParentVersionIds,
                RestoredFromVersionId: lineage.RestoredFromVersionId,
                ForkOriginVersionId: lineage.ForkOriginVersionId,
                InheritedFromVersionId: lineage.InheritedFromVersionId,
                InheritedFromSourcePath: lineage.InheritedFromSourcePath,
                ContentSignature: contentSignature,
                SyncOrigin: request.SyncOrigin,
                SourceLastWriteUtc: request.SourceLastWriteUtc,
                VaultId: binding?.Id);

            var manifestsToRecord = new List<FileVersionManifest> { manifest };
            existingManifests?.Add(manifest);
            manifestsToRecord.AddRange(await BuildFolderCascadeManifestsAsync(
                    existingManifests,
                    manifest,
                    request.WatchedFolderPath,
                    cancellationToken)
                .ConfigureAwait(false));
            mirrorWarnings.AddRange(await WriteManifestsAsync(manifestsToRecord, cancellationToken).ConfigureAwait(false));
            if (lineage.OperationType == VersionOperationType.Restore)
            {
                DeleteRestoreHint(sourcePath);
            }

            return new FileCommitResult(manifest, newChunkCount, CondenseMirrorWarnings(mirrorWarnings));
        }
        finally
        {
            mutationManifestCache = null;
        }
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None, cancellationToken).ConfigureAwait(false);
        if (metadataStore is not null)
        {
            return await metadataStore.ListVersionsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.Exists(ManifestsPath(rootPath)))
        {
            return [];
        }

        var versions = new List<RepositoryVersionSummary>();
        foreach (var path in Directory.EnumerateFiles(ManifestsPath(rootPath), "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
            versions.Add(ToSummary(manifest));
        }

        return versions
            .OrderByDescending(version => version.CapturedAtUtc)
            .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None, cancellationToken).ConfigureAwait(false);
        if (metadataStore is not null)
        {
            return await metadataStore.ListLatestEntriesAsync(cancellationToken).ConfigureAwait(false);
        }

        var manifests = await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        return manifests
            .GroupBy(manifest => ToEntryKey(manifest.SourcePath, manifest.EntryKind), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(manifest => manifest.CapturedAtUtc)
                .ThenByDescending(manifest => manifest.VersionId, StringComparer.Ordinal)
                .First())
            .Select(ToSummary)
            .OrderBy(version => version.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(version => version.EntryKind)
            .ToArray();
    }

    public async Task<RepositoryDeletionResult?> RecordDeletionAsync(
        RepositoryDeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.BestEffort, cancellationToken).ConfigureAwait(false);
        try
        {
            if (metadataStore is null)
            {
                Directory.CreateDirectory(ManifestsPath(rootPath));
            }

            var sourcePath = Path.GetFullPath(request.SourcePath);
            var entryKind = request.IsDirectory ? RepositoryEntryKind.Folder : RepositoryEntryKind.File;
            var existingManifests = metadataStore is null
                ? await ReadAllManifestsForMutationAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var deletedFrom = existingManifests is null
                ? await FindLatestManifestAsync(sourcePath, entryKind, cancellationToken).ConfigureAwait(false)
                : LatestForPath(existingManifests, sourcePath, entryKind);
            if (deletedFrom is null || deletedFrom.IsDeleted)
            {
                return null;
            }

            var manifest = new FileVersionManifest(
                VersionId: Guid.CreateVersion7().ToString("N"),
                WatchedFolderId: request.WatchedFolderId,
                SourcePath: sourcePath,
                CapturedAtUtc: entryKind == RepositoryEntryKind.Folder
                    ? NextFolderSnapshotTime(request.DeletedAtUtc, deletedFrom)
                    : request.DeletedAtUtc,
                Consistency: request.Consistency,
                LogicalLength: deletedFrom.LogicalLength,
                Chunks: [],
                OperationType: VersionOperationType.Delete,
                ParentVersionIds: [deletedFrom.VersionId],
                ContentSignature: ComputeDeletedContentSignature(sourcePath, deletedFrom.VersionId),
                EntryKind: entryKind,
                IsDeleted: true,
                FolderEntries: entryKind == RepositoryEntryKind.Folder ? deletedFrom.FolderEntries : null,
                DeletedFromVersionId: deletedFrom.VersionId,
                VaultId: binding?.Id);

            var mirrorWarnings = new List<string>(lease.Warnings);
            var manifestsToRecord = new List<FileVersionManifest> { manifest };
            existingManifests?.Add(manifest);
            manifestsToRecord.AddRange(await BuildFolderCascadeManifestsAsync(
                    existingManifests,
                    manifest,
                    request.WatchedFolderPath,
                    cancellationToken)
                .ConfigureAwait(false));
            mirrorWarnings.AddRange(await WriteManifestsAsync(manifestsToRecord, cancellationToken).ConfigureAwait(false));
            return new RepositoryDeletionResult(manifest, CondenseMirrorWarnings(mirrorWarnings));
        }
        finally
        {
            mutationManifestCache = null;
        }
    }

    public async Task<RepositoryPurgeResult> PurgeAsync(
        RepositoryPurgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopes = NormalisePurgeScopes(request.Scopes);
        if (scopes.Count == 0)
        {
            return new RepositoryPurgeResult(0, 0, 0, []);
        }

        var preserveScopes = NormalisePurgeScopes(request.PreserveScopes ?? []);
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.Required, cancellationToken).ConfigureAwait(false);
        try
        {
            var manifests = (await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            ValidateMaintenanceInputs(manifests);
            if (manifests.Length == 0)
            {
                return new RepositoryPurgeResult(0, 0, 0, []);
            }

            var purgeIds = SelectInitialPurgeIds(manifests, scopes, preserveScopes);
            ExpandPurgeReferences(manifests, purgeIds);
            if (purgeIds.Count == 0)
            {
                return new RepositoryPurgeResult(0, 0, 0, []);
            }

            var purgedManifests = manifests
                .Where(manifest => purgeIds.Contains(manifest.VersionId))
                .ToArray();
            var remainingManifests = manifests
                .Where(manifest => !purgeIds.Contains(manifest.VersionId))
                .ToArray();
            var purgedDigests = purgedManifests
                .SelectMany(manifest => manifest.Chunks)
                .Select(chunk => chunk.Digest)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var mirrorWarnings = new List<string>(lease.Warnings);
            var reclaimedBytes = 0L;

            mutationManifestCache = remainingManifests.ToList();
            if (metadataStore is not null)
            {
                await metadataStore.DeleteVersionsAsync(purgeIds.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            foreach (var manifest in purgedManifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reclaimedBytes += DeleteLocalFile(ManifestPath(rootPath, manifest.VersionId));
                DeleteMirrorFile(ManifestPath, manifest.VersionId, mirrorWarnings);
            }

            var remainingReferences = metadataStore is null
                ? CountChunkReferences(remainingManifests, purgedDigests)
                : await metadataStore.CountChunkReferencesAsync(purgedDigests, cancellationToken).ConfigureAwait(false);
            var deletedChunkCount = 0;
            foreach (var digest in purgedDigests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remainingReferences.GetValueOrDefault(digest) > 0)
                {
                    continue;
                }

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

            return new RepositoryPurgeResult(
                purgedManifests.Length,
                deletedChunkCount,
                reclaimedBytes,
                CondenseMirrorWarnings(mirrorWarnings));
        }
        finally
        {
            mutationManifestCache = null;
        }
    }

    public async Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None, cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        var manifest = await ReadManifestByVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        return new RepositoryInspection(
            manifest,
            manifest.Chunks.Count,
            manifest.LogicalLength,
            manifest.Chunks.Sum(chunk => (long)chunk.StoredLength));
    }

    public async Task<RepositoryRestoreResult> RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        outputPath = Path.GetFullPath(outputPath);
        ValidateRestoreDestination(outputPath);
        var destinationExisted = File.Exists(outputPath);
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadManifestByVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        return await RestoreManifestAsync(manifest, outputPath, destinationExisted, writeRestoreHint: true, cancellationToken).ConfigureAwait(false);
    }

    public Task<RepositoryRestoreResult> RestoreAsync(string versionId, IRepositoryRestoreTarget target, CancellationToken cancellationToken = default)
        => RestoreToTargetAsync(versionId, target, writeRestoreHint: true, cancellationToken);

    public async Task RestorePreviewAsync(string versionId, IRepositoryRestoreTarget target, CancellationToken cancellationToken = default)
        => await RestoreToTargetAsync(versionId, target, writeRestoreHint: false, cancellationToken).ConfigureAwait(false);

    private async Task<RepositoryRestoreResult> RestoreToTargetAsync(string versionId, IRepositoryRestoreTarget target,
        bool writeRestoreHint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using (target.ConfigureAwait(false))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
            var outputPath = Path.GetFullPath(target.OutputPath);
            // The target owns native destination validation. Privileged repository code does not reopen user paths.
            if (new[] { rootPath }.Concat(configuredMirrorNodes.Select(node => node.Path)).Any(root => StorageOwnership.Contains(root, outputPath)))
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Restore destination must be outside repository storage.");
            await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestByVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
            var plan = await new RestoreGraphValidator(verifiedReader, integrityLimits, ReadManifestByVersionAsync)
                .BuildAsync(manifest, outputPath, cancellationToken).ConfigureAwait(false);
            await target.PrepareAsync(plan.Kind, cancellationToken).ConfigureAwait(false);
            foreach (var directory in plan.Directories.Where(path => path.Length > 0))
                await target.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            foreach (var file in plan.Files)
            {
                await using var output = await target.CreateFileAsync(file.RelativePath, cancellationToken).ConfigureAwait(false);
                foreach (var chunk in file.Manifest.Chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = await verifiedReader.ReadAsync(rootPath, ChunkDescriptor.FromChunk(chunk), cancellationToken).ConfigureAwait(false);
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                if (output.Length != file.Manifest.LogicalLength)
                    throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Restored file length mismatch.");
                await target.FlushFileAsync(output, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            faults?.Hit(RepositoryFaultPoint.BeforeRestorePublication, outputPath);
            cancellationToken.ThrowIfCancellationRequested();
            var warnings = new List<string>(await target.PublishAsync(cancellationToken).ConfigureAwait(false));
            if (writeRestoreHint)
                foreach (var file in plan.Files)
                {
                    var restoredPath = plan.Kind == RepositoryEntryKind.Folder ? Path.Combine(outputPath, file.RelativePath) : outputPath;
                    try
                    {
                        faults?.Hit(RepositoryFaultPoint.AfterRestorePublication, restoredPath);
                        faults?.Hit(RepositoryFaultPoint.BeforeRestoreHint, restoredPath);
                        WriteRestoreHint(restoredPath, file.Manifest);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
                    { warnings.Add($"Content was restored and verified, but lineage recording failed for '{restoredPath}': {exception.Message}"); }
                }
            return new(outputPath, plan.LogicalBytes, plan.Files.Count, warnings);
        }
    }

    public async Task RestorePreviewAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        outputPath = Path.GetFullPath(outputPath);
        ValidateRestoreDestination(outputPath);
        var destinationExisted = File.Exists(outputPath);
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadManifestByVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        await RestoreManifestAsync(manifest, outputPath, destinationExisted, writeRestoreHint: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RepositoryScrubReport> ScrubAsync(
        bool autoRepairFromMirror,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.Required, cancellationToken).ConfigureAwait(false);
        var issues = new List<RepositoryScrubIssue>();
        var manifests = await ReadRepairableManifestsAsync(autoRepairFromMirror, issues, cancellationToken).ConfigureAwait(false);
        ValidateReferencedDescriptors(manifests);
        manifests = await FilterValidManifestGraphsAsync(manifests, (manifest, exception) =>
            issues.Add(new RepositoryScrubIssue(RepositoryScrubIssueSeverity.Critical, RepositoryScrubIssueKind.InvalidManifest,
                rootPath, manifest.VersionId, null, exception.Message, RepositoryRepairAction.Unresolved)), cancellationToken).ConfigureAwait(false);
        var checkedChunks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedBytes = new Dictionary<string, long>(GetMirrorNodeUsedBytes(), StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in manifests)
        {
            foreach (var chunk in manifest.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!checkedChunks.Add(chunk.Digest))
                {
                    continue;
                }

                var primary = ValidateChunk(rootPath, chunk);
                if (!primary.IsHealthy)
                {
                    var repaired = false;
                    if (autoRepairFromMirror)
                    {
                        foreach (var mirrorNode in mirrorNodes)
                        {
                            var mirror = ValidateChunk(mirrorNode.Path, chunk);
                            if (!mirror.IsHealthy)
                            {
                                continue;
                            }

                            await publisher.RepairAsync(mirrorNode.Path, rootPath, ChunkDescriptor.FromChunk(chunk), cancellationToken).ConfigureAwait(false);
                            issues.Add(new RepositoryScrubIssue(
                                Severity: RepositoryScrubIssueSeverity.Warning,
                                Kind: primary.IssueKind ?? RepositoryScrubIssueKind.CorruptChunk,
                                Path: ChunkPath(rootPath, chunk.Digest),
                                VersionId: manifest.VersionId,
                                ChunkDigest: chunk.Digest,
                                Message: $"Primary chunk was repaired from mirror '{mirrorNode.Label}'.",
                            RepairAction: RepositoryRepairAction.RepairedPrimaryFromMirror));
                            repaired = true;
                            break;
                        }
                    }

                    if (!repaired)
                    {
                        issues.Add(new RepositoryScrubIssue(
                            Severity: RepositoryScrubIssueSeverity.Critical,
                            Kind: primary.IssueKind ?? RepositoryScrubIssueKind.CorruptChunk,
                            Path: ChunkPath(rootPath, chunk.Digest),
                            VersionId: manifest.VersionId,
                            ChunkDigest: chunk.Digest,
                            Message: primary.Message ?? "Primary chunk is unavailable or corrupt and no healthy repair copy exists.",
                            RepairAction: RepositoryRepairAction.Unresolved));
                        continue;
                    }
                }

                var selection = SelectVerifiedMirrorTargets(chunk, usedBytes);
                if (selection.IsUnderSatisfied)
                    issues.Add(new RepositoryScrubIssue(RepositoryScrubIssueSeverity.Warning, RepositoryScrubIssueKind.MirrorDrift,
                        rootPath, manifest.VersionId, chunk.Digest, "Required mirror copies cannot fit within the available capacity.", RepositoryRepairAction.None));
                var targetNodeIds = selection.TargetNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var mirrorNode in mirrorNodes)
                {
                    if (!targetNodeIds.Contains(mirrorNode.Id))
                    {
                        continue;
                    }

                    var mirrorValidation = ValidateChunk(mirrorNode.Path, chunk);
                    if (mirrorValidation.IsHealthy)
                    {
                        continue;
                    }

                    if (autoRepairFromMirror)
                    {
                        var oldBytes = GetFileLength(ChunkPath(mirrorNode.Path, chunk.Digest)) + GetFileLength(MetadataPath(mirrorNode.Path, chunk.Digest));
                        await publisher.RepairAsync(rootPath, mirrorNode.Path, ChunkDescriptor.FromChunk(chunk), cancellationToken).ConfigureAwait(false);
                        usedBytes[mirrorNode.Id] += GetFileLength(ChunkPath(mirrorNode.Path, chunk.Digest)) + GetFileLength(MetadataPath(mirrorNode.Path, chunk.Digest)) - oldBytes;
                        issues.Add(new RepositoryScrubIssue(
                            Severity: RepositoryScrubIssueSeverity.Warning,
                            Kind: RepositoryScrubIssueKind.MirrorDrift,
                            Path: ChunkPath(mirrorNode.Path, chunk.Digest),
                            VersionId: manifest.VersionId,
                            ChunkDigest: chunk.Digest,
                            Message: $"Mirror '{mirrorNode.Label}' chunk was repaired from a healthy primary copy.",
                            RepairAction: RepositoryRepairAction.RepairedMirrorFromPrimary));
                    }
                    else
                    {
                        issues.Add(new RepositoryScrubIssue(
                            Severity: RepositoryScrubIssueSeverity.Warning,
                            Kind: RepositoryScrubIssueKind.MirrorDrift,
                            Path: ChunkPath(mirrorNode.Path, chunk.Digest),
                            VersionId: manifest.VersionId,
                            ChunkDigest: chunk.Digest,
                            Message: mirrorValidation.Message ?? $"Mirror '{mirrorNode.Label}' chunk differs from the primary repository.",
                            RepairAction: RepositoryRepairAction.None));
                    }
                }
            }
        }

        var repairedIssueCount = issues.Count(issue => issue.RepairAction is
            RepositoryRepairAction.RepairedPrimaryFromMirror or RepositoryRepairAction.RepairedMirrorFromPrimary);
        var healthState = CalculateScrubHealth(issues);
        return new RepositoryScrubReport(
            CompletedAtUtc: DateTimeOffset.UtcNow,
            HealthState: healthState,
            ManifestCount: manifests.Count,
            CheckedChunkCount: checkedChunks.Count,
            IssueCount: issues.Count,
            RepairedIssueCount: repairedIssueCount,
            Issues: issues);
    }

    public Task<MirrorRepairReport> PreviewMirrorRepairAsync(
        string? mirrorNodeId = null,
        CancellationToken cancellationToken = default)
    {
        return RunMirrorRepairCoreAsync(isPreview: true, mirrorNodeId, cancellationToken);
    }

    public Task<MirrorRepairReport> RunMirrorRepairAsync(
        string? mirrorNodeId = null,
        CancellationToken cancellationToken = default)
    {
        return RunMirrorRepairCoreAsync(isPreview: false, mirrorNodeId, cancellationToken);
    }

    private async Task<MirrorRepairReport> RunMirrorRepairCoreAsync(
        bool isPreview,
        string? requestedMirrorNodeId,
        CancellationToken cancellationToken)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.Required, cancellationToken).ConfigureAwait(false);
        var allNodes = mirrorNodes.ToArray();
        var usedBytes = new Dictionary<string, long>(GetMirrorNodeUsedBytes(), StringComparer.OrdinalIgnoreCase);
        var checkedChunks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodeIssues = allNodes.ToDictionary(
            node => node.Id,
            _ => new List<MirrorRepairIssue>(),
            StringComparer.OrdinalIgnoreCase);
        var primaryIssues = new List<MirrorRepairIssue>();
        var repairAll = string.IsNullOrWhiteSpace(requestedMirrorNodeId);
        var canWritePrimary = !isPreview && repairAll;

        var manifests = await ReadPrimaryManifestsForMirrorRepairAsync(
                canWritePrimary,
                primaryIssues,
                cancellationToken)
            .ConfigureAwait(false);

        ValidateReferencedDescriptors(manifests);
        manifests = await FilterValidManifestGraphsAsync(manifests, (manifest, exception) =>
            primaryIssues.Add(new MirrorRepairIssue(null, null, RepositoryScrubIssueSeverity.Critical, MirrorRepairArtefactKind.Manifest,
                rootPath, manifest.VersionId, null, exception.Message, MirrorRepairAction.Unresolved)), cancellationToken).ConfigureAwait(false);

        foreach (var node in allNodes)
        {
            if (File.Exists(node.Path))
            {
                nodeIssues[node.Id].Add(new MirrorRepairIssue(
                    node.Id,
                    node.Label,
                    RepositoryScrubIssueSeverity.Warning,
                    MirrorRepairArtefactKind.Repository,
                    node.Path,
                    null,
                    null,
                    $"Mirror '{node.Label}' is unavailable because its configured path is a file.",
                    MirrorRepairAction.Unresolved));
            }
            else if (!isPreview && ShouldRepairNode(node, requestedMirrorNodeId))
            {
                Directory.CreateDirectory(node.Path);
            }
        }

        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (metadataStore is null)
            {
                var primaryManifestPath = ManifestPath(rootPath, manifest.VersionId);
                var primaryManifestBytes = (await ReadManifestObjectAsync(primaryManifestPath, cancellationToken).ConfigureAwait(false)).Bytes;

                foreach (var node in allNodes)
                {
                    if (nodeIssues[node.Id].Any(issue => issue.ArtefactKind == MirrorRepairArtefactKind.Repository))
                    {
                        continue;
                    }

                    var manifestIssue = BuildManifestMirrorIssue(node, manifest, primaryManifestBytes);
                    if (manifestIssue is null)
                    {
                        continue;
                    }

                    if (!isPreview && ShouldRepairNode(node, requestedMirrorNodeId))
                    {
                        var oldBytes = GetFileLength(ManifestPath(node.Path, manifest.VersionId));
                        AtomicWriteOverwrite(ManifestPath(node.Path, manifest.VersionId), primaryManifestBytes);
                        usedBytes[node.Id] += primaryManifestBytes.Length - oldBytes;
                        manifestIssue = manifestIssue with { RepairAction = MirrorRepairAction.RepairedMirrorFromPrimary };
                    }

                    nodeIssues[node.Id].Add(manifestIssue);
                }
            }

            foreach (var chunk in manifest.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!checkedChunks.Add(chunk.Digest)) continue;
                var primaryValidation = ValidateChunk(rootPath, chunk);
                var primaryHealthy = primaryValidation.IsHealthy;
                if (!primaryHealthy)
                {
                    var issue = BuildPrimaryRepairIssue(
                        chunk,
                        primaryValidation,
                        requestedMirrorNodeId,
                        isPreview,
                        allNodes);
                    if (!isPreview && repairAll && issue.RepairAction == MirrorRepairAction.RepairedPrimaryFromMirror)
                    {
                        var sourceMirror = allNodes.First(node => ValidateChunk(node.Path, chunk).IsHealthy);
                        await publisher.RepairAsync(sourceMirror.Path, rootPath, ChunkDescriptor.FromChunk(chunk), cancellationToken).ConfigureAwait(false);
                        primaryHealthy = true;
                    }

                    primaryIssues.Add(issue);
                }

                if (!primaryHealthy)
                {
                    continue;
                }

                var selection = SelectVerifiedMirrorTargets(chunk, usedBytes);
                if (selection.IsUnderSatisfied)
                    primaryIssues.Add(new MirrorRepairIssue(null, null, RepositoryScrubIssueSeverity.Warning, MirrorRepairArtefactKind.Repository,
                        rootPath, manifest.VersionId, chunk.Digest, "Required mirror copies cannot fit within the available capacity.", MirrorRepairAction.None));
                var targetNodeIds = selection.TargetNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var node in allNodes)
                {
                    if (!targetNodeIds.Contains(node.Id))
                    {
                        continue;
                    }

                    if (nodeIssues[node.Id].Any(issue => issue.ArtefactKind == MirrorRepairArtefactKind.Repository))
                    {
                        continue;
                    }

                    var issues = BuildMirrorChunkIssues(node, chunk).ToList();
                    if (issues.Count == 0)
                    {
                        continue;
                    }

                    if (!isPreview && ShouldRepairNode(node, requestedMirrorNodeId))
                    {
                        var oldBytes = GetFileLength(ChunkPath(node.Path, chunk.Digest)) + GetFileLength(MetadataPath(node.Path, chunk.Digest));
                        await publisher.RepairAsync(rootPath, node.Path, ChunkDescriptor.FromChunk(chunk), cancellationToken).ConfigureAwait(false);
                        usedBytes[node.Id] += GetFileLength(ChunkPath(node.Path, chunk.Digest)) + GetFileLength(MetadataPath(node.Path, chunk.Digest)) - oldBytes;
                        issues = issues
                            .Select(issue => issue with { RepairAction = MirrorRepairAction.RepairedMirrorFromPrimary })
                            .ToList();
                    }

                    nodeIssues[node.Id].AddRange(issues);
                }
            }
        }

        var nodes = allNodes
            .Select(node => BuildMirrorNodeRepairReport(node, nodeIssues[node.Id]))
            .ToArray();
        var issueCount = primaryIssues.Count + nodes.Sum(node => node.IssueCount);
        var repairedIssueCount = primaryIssues.Count(issue => issue.RepairAction != MirrorRepairAction.None
                                                              && issue.RepairAction != MirrorRepairAction.Unresolved)
                                 + nodes.Sum(node => node.RepairedIssueCount);
        var healthState = CalculateMirrorRepairHealth(primaryIssues, nodes);
        return new MirrorRepairReport(
            DateTimeOffset.UtcNow,
            isPreview,
            requestedMirrorNodeId,
            healthState,
            issueCount,
            repairedIssueCount,
            nodes,
            primaryIssues);
    }

    private async Task<IReadOnlyList<FileVersionManifest>> ReadPrimaryManifestsForMirrorRepairAsync(
        bool canRepairPrimaryManifest,
        List<MirrorRepairIssue> primaryIssues,
        CancellationToken cancellationToken)
    {
        if (metadataStore is not null)
        {
            return await metadataStore.ListManifestsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.Exists(ManifestsPath(rootPath)))
        {
            return [];
        }

        var manifests = new List<FileVersionManifest>();
        foreach (var path in Directory.EnumerateFiles(ManifestsPath(rootPath), "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versionId = Path.GetFileNameWithoutExtension(path);
            try
            {
                var manifest = await ReadManifestByVersionAsync(Path.GetFileNameWithoutExtension(path), cancellationToken).ConfigureAwait(false);
                await ValidateManifestGraphAsync(manifest, ReadManifestByVersionAsync, cancellationToken).ConfigureAwait(false);
                manifests.Add(manifest);
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                var repaired = false;
                if (canRepairPrimaryManifest)
                {
                    foreach (var node in mirrorNodes)
                    {
                        var mirrorManifestPath = ManifestPath(node.Path, versionId);
                        if (!File.Exists(mirrorManifestPath))
                        {
                            continue;
                        }

                        try
                        {
                            var (mirrorManifest, mirrorManifestBytes) = await ReadManifestObjectAsync(mirrorManifestPath, cancellationToken).ConfigureAwait(false);
                            await ValidateManifestGraphAsync(mirrorManifest,
                                (id, token) => ReadManifestAsync(ManifestPath(node.Path, id), token), cancellationToken).ConfigureAwait(false);
                            AtomicWriteOverwrite(path, mirrorManifestBytes);
                            manifests.Add(mirrorManifest);
                            primaryIssues.Add(new MirrorRepairIssue(
                                null,
                                null,
                                RepositoryScrubIssueSeverity.Warning,
                                MirrorRepairArtefactKind.Manifest,
                                path,
                                versionId,
                                null,
                                $"Primary manifest was repaired from mirror '{node.Label}'.",
                                MirrorRepairAction.RepairedPrimaryFromMirror));
                            repaired = true;
                            break;
                        }
                        catch (Exception mirrorException) when (mirrorException is JsonException or IOException or InvalidDataException)
                        {
                        }
                    }
                }

                if (!repaired)
                {
                    primaryIssues.Add(new MirrorRepairIssue(
                        null,
                        null,
                        RepositoryScrubIssueSeverity.Critical,
                        MirrorRepairArtefactKind.Manifest,
                        path,
                        versionId,
                        null,
                        $"Primary manifest could not be read: {exception.Message}",
                        canRepairPrimaryManifest ? MirrorRepairAction.Unresolved : MirrorRepairAction.None));
                }
            }
        }

        return manifests;
    }

    private static bool ShouldRepairNode(MirrorNodeConfiguration node, string? requestedMirrorNodeId)
    {
        return string.IsNullOrWhiteSpace(requestedMirrorNodeId)
               || string.Equals(node.Id, requestedMirrorNodeId, StringComparison.OrdinalIgnoreCase);
    }

    private static MirrorNodeRepairReport BuildMirrorNodeRepairReport(
        MirrorNodeConfiguration node,
        IReadOnlyList<MirrorRepairIssue> issues)
    {
        var health = issues.Count == 0 || issues.All(issue => issue.RepairAction == MirrorRepairAction.RepairedMirrorFromPrimary)
            ? RepositoryHealthState.Healthy
            : RepositoryHealthState.Warning;
        return new MirrorNodeRepairReport(
            node.Id,
            node.Label,
            node.Path,
            node.IsEnabled,
            health,
            issues.Count,
            issues.Count(issue => issue.RepairAction == MirrorRepairAction.RepairedMirrorFromPrimary),
            issues);
    }

    private static RepositoryHealthState CalculateMirrorRepairHealth(
        IReadOnlyList<MirrorRepairIssue> primaryIssues,
        IReadOnlyList<MirrorNodeRepairReport> nodes)
    {
        if (primaryIssues.Any(issue => issue.RepairAction == MirrorRepairAction.Unresolved
                                       || issue.Severity == RepositoryScrubIssueSeverity.Critical))
        {
            return RepositoryHealthState.Critical;
        }

        return primaryIssues.Any(issue => issue.RepairAction == MirrorRepairAction.None)
               || nodes.Any(node => node.HealthState != RepositoryHealthState.Healthy)
            ? RepositoryHealthState.Warning
            : RepositoryHealthState.Healthy;
    }

    private MirrorRepairIssue BuildPrimaryRepairIssue(
        ManifestChunk chunk,
        ChunkValidation primaryValidation,
        string? requestedMirrorNodeId,
        bool isPreview,
        IReadOnlyList<MirrorNodeConfiguration> allNodes)
    {
        var canRepairFromMirror = string.IsNullOrWhiteSpace(requestedMirrorNodeId)
                                  && allNodes.Any(node => ValidateChunk(node.Path, chunk).IsHealthy);
        var action = isPreview
            ? MirrorRepairAction.None
            : canRepairFromMirror
                ? MirrorRepairAction.RepairedPrimaryFromMirror
                : MirrorRepairAction.Unresolved;
        var message = string.IsNullOrWhiteSpace(requestedMirrorNodeId)
            ? primaryValidation.Message ?? "Primary artefact is unavailable or corrupt."
            : "Selected mirror repair cannot repair primary artefacts; run repair all first.";
        return new MirrorRepairIssue(
            null,
            null,
            action == MirrorRepairAction.Unresolved ? RepositoryScrubIssueSeverity.Critical : RepositoryScrubIssueSeverity.Warning,
            MirrorRepairArtefactKind.Chunk,
            ChunkPath(rootPath, chunk.Digest),
            null,
            chunk.Digest,
            message,
            action);
    }

    private MirrorRepairIssue? BuildManifestMirrorIssue(
        MirrorNodeConfiguration node,
        FileVersionManifest manifest,
        byte[] primaryManifestBytes)
    {
        var path = ManifestPath(node.Path, manifest.VersionId);
        if (!File.Exists(path))
        {
            return new MirrorRepairIssue(
                node.Id,
                node.Label,
                RepositoryScrubIssueSeverity.Warning,
                MirrorRepairArtefactKind.Manifest,
                path,
                manifest.VersionId,
                null,
                $"Mirror '{node.Label}' manifest is missing.",
                MirrorRepairAction.None);
        }

        try
        {
            var bytes = VerifiedChunkReader.ReadBoundedAsync(path, integrityLimits.MaxManifestBytes, default).GetAwaiter().GetResult();
            if (bytes.SequenceEqual(primaryManifestBytes))
            {
                return null;
            }

            return new MirrorRepairIssue(
                node.Id,
                node.Label,
                RepositoryScrubIssueSeverity.Warning,
                MirrorRepairArtefactKind.Manifest,
                path,
                manifest.VersionId,
                null,
                $"Mirror '{node.Label}' manifest differs from the primary repository.",
                MirrorRepairAction.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new MirrorRepairIssue(
                node.Id,
                node.Label,
                RepositoryScrubIssueSeverity.Warning,
                MirrorRepairArtefactKind.Manifest,
                path,
                manifest.VersionId,
                null,
                $"Mirror '{node.Label}' manifest could not be read: {exception.Message}",
                MirrorRepairAction.Unresolved);
        }
    }

    private IEnumerable<MirrorRepairIssue> BuildMirrorChunkIssues(MirrorNodeConfiguration node, ManifestChunk chunk)
    {
        var chunkPath = ChunkPath(node.Path, chunk.Digest);
        var metadataPath = MetadataPath(node.Path, chunk.Digest);
        if (!File.Exists(chunkPath))
        {
            yield return new MirrorRepairIssue(
                node.Id,
                node.Label,
                RepositoryScrubIssueSeverity.Warning,
                MirrorRepairArtefactKind.Chunk,
                chunkPath,
                null,
                chunk.Digest,
                $"Mirror '{node.Label}' chunk is missing.",
                MirrorRepairAction.None);
        }
        else
        {
            var chunkValidation = ValidateChunkPayload(chunkPath, chunk);
            if (chunkValidation is not null)
            {
                yield return chunkValidation with { MirrorNodeId = node.Id, MirrorNodeLabel = node.Label };
            }
        }

        var metadataValidation = ValidateChunkMetadata(node.Path, chunk);
        if (metadataValidation is not null)
        {
            yield return metadataValidation with { MirrorNodeId = node.Id, MirrorNodeLabel = node.Label };
        }

        MirrorRepairIssue? ValidateChunkPayload(string path, ManifestChunk manifestChunk)
        {
            try
            {
                var payload = VerifiedChunkReader.ReadBoundedAsync(path, integrityLimits.MaxStoredChunkBytes, default).GetAwaiter().GetResult();
                if (payload.Length != manifestChunk.StoredLength)
                {
                    return NewChunkIssue($"Mirror '{node.Label}' chunk stored length differs from the manifest.");
                }

                verifiedReader.DecodeAndVerify(payload, ChunkDescriptor.FromChunk(manifestChunk));

                return null;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return NewChunkIssue($"Mirror '{node.Label}' chunk could not be read: {exception.Message}");
            }

            MirrorRepairIssue NewChunkIssue(string message)
            {
                return new MirrorRepairIssue(
                    null,
                    null,
                    RepositoryScrubIssueSeverity.Warning,
                    MirrorRepairArtefactKind.Chunk,
                    path,
                    null,
                    manifestChunk.Digest,
                    message,
                    MirrorRepairAction.None);
            }
        }
    }

    private MirrorRepairIssue? ValidateChunkMetadata(string root, ManifestChunk chunk)
    {
        var path = MetadataPath(root, chunk.Digest);
        if (!File.Exists(path))
        {
            return new MirrorRepairIssue(
                null,
                null,
                RepositoryScrubIssueSeverity.Warning,
                MirrorRepairArtefactKind.Metadata,
                path,
                null,
                chunk.Digest,
                "Mirror chunk metadata is missing.",
                MirrorRepairAction.None);
        }

        var metadata = ReadChunkMetadataSafe(root, chunk.Digest);
        if (metadata is not null
            && string.Equals(metadata.Digest, chunk.Digest, StringComparison.OrdinalIgnoreCase)
            && metadata.LogicalLength == chunk.Length
            && metadata.StoredLength == chunk.StoredLength
            && metadata.Encoding == chunk.Encoding)
        {
            return null;
        }

        return new MirrorRepairIssue(
            null,
            null,
            RepositoryScrubIssueSeverity.Warning,
            MirrorRepairArtefactKind.Metadata,
            path,
            null,
            chunk.Digest,
            "Mirror chunk metadata is missing, corrupt, or differs from the manifest.",
            MirrorRepairAction.None);
    }

    public async Task<RestoreRehearsalReport> RunRestoreRehearsalAsync(
        string tempRoot,
        int maxVersions,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None, cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        var requested = Math.Max(0, maxVersions);
        var manifests = (await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false))
            .Where(manifest => manifest.EntryKind == RepositoryEntryKind.File && !manifest.IsDeleted)
            .OrderByDescending(manifest => manifest.CapturedAtUtc)
            .ThenByDescending(manifest => manifest.VersionId, StringComparer.Ordinal)
            .Take(requested)
            .ToArray();
        var results = new List<RestoreRehearsalResult>();
        ValidateRestoreDestination(Path.GetFullPath(tempRoot));
        var operationRoot = Path.Combine(Path.GetFullPath(tempRoot), $"rehearsal-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(operationRoot);
            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = string.IsNullOrWhiteSpace(Path.GetFileName(manifest.SourcePath))
                    ? $"{manifest.VersionId}.rehearsal"
                    : $"{manifest.VersionId}-{Path.GetFileName(manifest.SourcePath)}";
                var outputPath = Path.Combine(operationRoot, fileName);
                try
                {
                    await RestoreManifestAsync(manifest, outputPath, destinationExisted: false, writeRestoreHint: false, cancellationToken)
                        .ConfigureAwait(false);
                    var restoredLength = new FileInfo(outputPath).Length;
                    var success = restoredLength == manifest.LogicalLength;
                    results.Add(new RestoreRehearsalResult(
                        manifest.VersionId,
                        manifest.SourcePath,
                        success,
                        manifest.LogicalLength,
                        success
                            ? "Restore rehearsal passed."
                            : $"Restore rehearsal length mismatch: expected {manifest.LogicalLength} byte(s), got {restoredLength}."));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    results.Add(new RestoreRehearsalResult(
                        manifest.VersionId,
                        manifest.SourcePath,
                        Success: false,
                        LogicalLength: manifest.LogicalLength,
                        Message: exception.Message));
                }
            }
        }
        finally
        {
            if (Directory.Exists(operationRoot))
            {
                Directory.Delete(operationRoot, recursive: true);
            }
        }

        var failures = results.Count(result => !result.Success);
        return new RestoreRehearsalReport(
            CompletedAtUtc: DateTimeOffset.UtcNow,
            HealthState: failures > 0 ? RepositoryHealthState.Critical : results.Count == 0 ? RepositoryHealthState.Warning : RepositoryHealthState.Healthy,
            RequestedVersionCount: requested,
            RehearsedVersionCount: results.Count - failures,
            FailedVersionCount: failures,
            Results: results);
    }

    public async Task<RepositoryRetentionPreview> PreviewRetentionAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.None, cancellationToken).ConfigureAwait(false);
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
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.Required, cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await BuildRetentionStateAsync(policy, nowUtc, cancellationToken).ConfigureAwait(false);
            var mirrorWarnings = new List<string>(lease.Warnings);
            var reclaimedBytes = 0L;
            if (state.PrunableManifests.Count > 0)
            {
                mutationManifestCache = null;
            }

            if (metadataStore is not null)
            {
                await metadataStore.DeleteVersionsAsync(
                        state.PrunableManifests.Select(manifest => manifest.VersionId).ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                foreach (var manifest in state.PrunableManifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    reclaimedBytes += DeleteLocalFile(ManifestPath(rootPath, manifest.VersionId));
                    DeleteMirrorFile(ManifestPath, manifest.VersionId, mirrorWarnings);
                }
            }

            var deletedChunkCount = 0;
            var remainingReferences = metadataStore is null
                ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                : await metadataStore.CountChunkReferencesAsync(state.PrunableChunkDigests, cancellationToken).ConfigureAwait(false);
            foreach (var digest in state.PrunableChunkDigests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remainingReferences.GetValueOrDefault(digest) > 0)
                {
                    continue;
                }

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
        finally
        {
            mutationManifestCache = null;
        }
    }

    private PreparedChunk PreparePayload(
        ReadOnlySpan<byte> raw,
        string digest,
        CompressionPreference compression,
        int minimumCompressionBytes)
    {
        var encoding = ToChunkEncoding(compression);
        if (encoding != ChunkEncoding.Raw && raw.Length >= minimumCompressionBytes)
        {
            var compressed = codec.Compress(raw, encoding, level: 3);
            if (compressed.Length < raw.Length)
            {
                return new PreparedChunk(
                    compressed,
                    new ChunkMetadata(digest, raw.Length, compressed.Length, encoding));
            }
        }

        return new PreparedChunk(
            raw.ToArray(),
            new ChunkMetadata(digest, raw.Length, raw.Length, ChunkEncoding.Raw));
    }

    private static ChunkEncoding ToChunkEncoding(CompressionPreference compression)
    {
        return compression switch
        {
            CompressionPreference.Zstd => ChunkEncoding.Zstd,
            CompressionPreference.Lz4 => ChunkEncoding.Lz4,
            CompressionPreference.Brotli => ChunkEncoding.Brotli,
            CompressionPreference.Lzma => ChunkEncoding.Lzma,
            _ => ChunkEncoding.Raw
        };
    }

    private async Task<IReadOnlyList<string>> MirrorChunkIfNeededAsync(ChunkDescriptor descriptor,
        Dictionary<string, long> usedBytes, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(new ChunkMetadata(descriptor.Digest, descriptor.LogicalLength, descriptor.StoredLength, descriptor.Encoding), JsonOptions).Length;
        var selection = mirrorPlacementPlanner.SelectChunkTargets(descriptor.Digest, descriptor.StoredLength + metadataBytes,
            new MirrorSetConfiguration(mirrorNodes, mirrorSet.PlacementPolicy), usedBytes);
        if (selection.IsUnderSatisfied)
            warnings.Add($"Mirror placement for chunk {descriptor.Digest} is under-satisfied: {selection.TargetNodeIds.Count} of {selection.RequiredCopyCount} required mirror copies are available.");
        foreach (var node in selection.TargetNodes)
        {
            try
            {
                var oldLength = GetFileLength(ChunkPath(node.Path, descriptor.Digest)) + GetFileLength(MetadataPath(node.Path, descriptor.Digest));
                await publisher.RepairAsync(rootPath, node.Path, descriptor, cancellationToken).ConfigureAwait(false);
                usedBytes[node.Id] += GetFileLength(ChunkPath(node.Path, descriptor.Digest)) + GetFileLength(MetadataPath(node.Path, descriptor.Digest)) - oldLength;
            }
            catch (Exception exception) when (IsMirrorIoFailure(exception))
            { warnings.Add(FormatMirrorWarning(node, $"write verified chunk {descriptor.Digest}", exception)); }
        }
        return warnings;
    }

    public async Task<MirrorRebalancePreviewReport> PreviewMirrorRebalanceAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.Required, cancellationToken).ConfigureAwait(false);
        return await BuildMirrorRebalanceReportAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MirrorRebalancePreviewReport> RunMirrorRebalanceAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.Required, cancellationToken).ConfigureAwait(false);
        var executed = await BuildMirrorRebalanceReportAsync(cancellationToken, isPreview: false, execute: true).ConfigureAwait(false);
        var final = await BuildMirrorRebalanceReportAsync(cancellationToken, isPreview: false).ConfigureAwait(false);
        var failures = executed.Actions.Where(action => action.Action == MirrorRebalanceActionKind.Unresolved).ToArray();
        if (failures.Length == 0) return final;
        var actions = final.Actions.Concat(failures).Distinct().ToArray();
        return final with { HealthState = RepositoryHealthState.Warning, Actions = actions, ActionCount = actions.Length,
            Nodes = mirrorNodes.Select(node => BuildMirrorNodeRebalancePreview(node, actions.Where(action => action.MirrorNodeId == node.Id).ToArray())).ToArray() };
    }

    public async Task<MirrorRebalancePreviewReport> PreviewMirrorDrainAsync(string mirrorNodeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorNodeId);
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.Required, cancellationToken).ConfigureAwait(false);
        return await BuildMirrorRebalanceReportAsync(cancellationToken, mirrorNodeId).ConfigureAwait(false);
    }

    public async Task<MirrorRebalancePreviewReport> RunMirrorDrainAsync(string mirrorNodeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorNodeId);
        await using var lease = await AcquireLeaseAsync(MirrorLeaseMode.Required, cancellationToken).ConfigureAwait(false);
        var executed = await BuildMirrorRebalanceReportAsync(cancellationToken, mirrorNodeId, isPreview: false, execute: true).ConfigureAwait(false);
        var final = await BuildMirrorRebalanceReportAsync(cancellationToken, mirrorNodeId, isPreview: false).ConfigureAwait(false);
        var failures = executed.Actions.Where(action => action.Action == MirrorRebalanceActionKind.Unresolved).ToArray();
        if (failures.Length == 0) return final;
        var actions = final.Actions.Concat(failures).Distinct().ToArray();
        return final with { HealthState = RepositoryHealthState.Warning, Actions = actions, ActionCount = actions.Length,
            Nodes = mirrorNodes.Select(node => BuildMirrorNodeRebalancePreview(node, actions.Where(action => action.MirrorNodeId == node.Id).ToArray())).ToArray() };
    }

    private async Task<MirrorRebalancePreviewReport> BuildMirrorRebalanceReportAsync(CancellationToken cancellationToken,
        string? drainMirrorNodeId = null, bool isPreview = true, bool execute = false)
    {
        var manifests = await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        ValidateReferencedDescriptors(manifests);
        var chunks = manifests.SelectMany(manifest => manifest.Chunks).DistinctBy(chunk => chunk.Digest, StringComparer.OrdinalIgnoreCase).ToArray();
        var draining = drainMirrorNodeId is not null;
        var drainNode = mirrorNodes.FirstOrDefault(node => string.Equals(node.Id, drainMirrorNodeId, StringComparison.OrdinalIgnoreCase));
        var nodeActions = mirrorNodes.ToDictionary(node => node.Id, _ => new List<MirrorRebalanceAction>(), StringComparer.OrdinalIgnoreCase);
        var primaryActions = new List<MirrorRebalanceAction>();
        var usedBytes = new Dictionary<string, long>(GetMirrorNodeUsedBytes(), StringComparer.OrdinalIgnoreCase);
        var planningSet = new MirrorSetConfiguration(mirrorNodes.Where(node => !draining || node.Id != drainNode?.Id).ToArray(), mirrorSet.PlacementPolicy).Normalise();
        if (draining && drainNode is null)
            AddUnresolved(null, "", $"Mirror node '{drainMirrorNodeId}' is not an enabled configured mirror.");
        else foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = ChunkDescriptor.FromChunk(chunk);
            var health = mirrorNodes.ToDictionary(node => node.Id, node => ValidateChunk(node.Path, chunk).IsHealthy, StringComparer.OrdinalIgnoreCase);
            var primaryHealthy = ValidateChunk(rootPath, chunk).IsHealthy;
            var donor = primaryHealthy ? rootPath : mirrorNodes.FirstOrDefault(node => health[node.Id])?.Path;
            var sidecarBytes = JsonSerializer.SerializeToUtf8Bytes(new ChunkMetadata(descriptor.Digest, descriptor.LogicalLength, descriptor.StoredLength, descriptor.Encoding)).Length;
            var footprint = descriptor.StoredLength + sidecarBytes;
            var effectiveUsage = planningSet.EnabledNodes.ToDictionary(node => node.Id,
                node => health[node.Id] ? Math.Min(usedBytes[node.Id], node.CapacityBudgetBytes ?? usedBytes[node.Id]) - footprint : usedBytes[node.Id],
                StringComparer.OrdinalIgnoreCase);
            var selection = mirrorPlacementPlanner.SelectChunkTargets(descriptor.Digest, footprint, planningSet, effectiveUsage);
            var required = draining ? Math.Max(1, selection.RequiredCopyCount) : selection.RequiredCopyCount;
            var targets = selection.TargetNodes;
            var satisfied = targets.Count >= required;
            if (donor is null)
            {
                AddUnresolved(drainNode, descriptor.Digest, "No verified donor matches the acknowledged chunk descriptor.");
                continue;
            }
            if (!primaryHealthy)
            {
                if (execute)
                {
                    try
                    {
                        await publisher.RepairAsync(donor, rootPath, descriptor, cancellationToken).ConfigureAwait(false);
                        primaryHealthy = true;
                    }
                    catch (Exception exception) when (IsMirrorIoFailure(exception))
                    { AddUnresolved(null, descriptor.Digest, $"Primary repair failed: {exception.Message}"); continue; }
                }
                else AddUnresolved(null, descriptor.Digest, "Primary repair from a verified mirror is required before deletion.");
            }
            foreach (var target in targets.Where(node => !health[node.Id]))
            {
                if (!execute)
                {
                    AddAction(target, descriptor, MirrorRebalanceActionKind.CopyToMirror, "Verify and repair the required mirror copy before deleting any donor.");
                    continue;
                }
                try
                {
                    var oldBytes = GetFileLength(ChunkPath(target.Path, descriptor.Digest)) + GetFileLength(MetadataPath(target.Path, descriptor.Digest));
                    await publisher.RepairAsync(rootPath, target.Path, descriptor, cancellationToken).ConfigureAwait(false);
                    usedBytes[target.Id] += GetFileLength(ChunkPath(target.Path, descriptor.Digest)) + GetFileLength(MetadataPath(target.Path, descriptor.Digest)) - oldBytes;
                    health[target.Id] = true;
                }
                catch (Exception exception) when (IsMirrorIoFailure(exception))
                { satisfied = false; AddUnresolved(target, descriptor.Digest, $"Mirror repair failed: {exception.Message}"); }
            }
            if (!satisfied)
            {
                AddUnresolved(drainNode, descriptor.Digest, $"Required mirror copies cannot be established ({targets.Count} of {required}); all departing objects were preserved.");
                continue;
            }
            var targetIds = targets.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var departing = mirrorNodes.Where(node => draining ? node.Id == drainNode!.Id : !targetIds.Contains(node.Id));
            foreach (var node in departing)
            {
                if (!File.Exists(ChunkPath(node.Path, descriptor.Digest)) && !File.Exists(MetadataPath(node.Path, descriptor.Digest))) continue;
                if (!execute)
                {
                    AddAction(node, descriptor, MirrorRebalanceActionKind.DeleteFromMirror, "Remove only after primary and required surviving mirrors verify.");
                    continue;
                }
                try
                {
                    // Validate complete surviving pairs immediately before either donor artefact is removed.
                    await verifiedReader.ReadAsync(rootPath, descriptor, cancellationToken).ConfigureAwait(false);
                    foreach (var target in targets) await verifiedReader.ReadAsync(target.Path, descriptor, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var oldBytes = GetFileLength(ChunkPath(node.Path, descriptor.Digest)) + GetFileLength(MetadataPath(node.Path, descriptor.Digest));
                    File.Delete(ChunkPath(node.Path, descriptor.Digest));
                    faults?.Hit(RepositoryFaultPoint.DepartingPayloadDeleted, ChunkPath(node.Path, descriptor.Digest));
                    File.Delete(MetadataPath(node.Path, descriptor.Digest));
                    usedBytes[node.Id] -= oldBytes;
                }
                catch (Exception exception) when (IsMirrorIoFailure(exception))
                { AddUnresolved(node, descriptor.Digest, $"Deletion was stopped: {exception.Message}"); }
            }
        }
        var actions = primaryActions.Concat(nodeActions.Values.SelectMany(actions => actions)).ToArray();
        return new MirrorRebalancePreviewReport(DateTimeOffset.UtcNow,
            actions.Length == 0 ? RepositoryHealthState.Healthy : RepositoryHealthState.Warning,
            chunks.Length, actions.Length,
            actions.Where(action => action.Action == MirrorRebalanceActionKind.CopyToMirror).Sum(action => action.EstimatedBytes),
            actions.Where(action => action.Action == MirrorRebalanceActionKind.DeleteFromMirror).Sum(action => action.EstimatedBytes),
            mirrorNodes.Select(node => BuildMirrorNodeRebalancePreview(node, nodeActions[node.Id])).ToArray(), actions,
            draining ? MirrorRebalanceOperation.Drain : MirrorRebalanceOperation.Placement, isPreview, drainMirrorNodeId);

        void AddUnresolved(MirrorNodeConfiguration? node, string digest, string message)
        {
            var action = new MirrorRebalanceAction(MirrorRebalanceActionKind.Unresolved, MirrorRebalanceArtefactKind.Chunk,
                node?.Id ?? "primary", node?.Label ?? "Primary", node?.Path ?? rootPath, digest, 0, message);
            if (node is null) primaryActions.Add(action); else nodeActions[node.Id].Add(action);
        }
        void AddAction(MirrorNodeConfiguration node, ChunkDescriptor descriptor, MirrorRebalanceActionKind kind, string message)
        {
            if (kind == MirrorRebalanceActionKind.CopyToMirror || File.Exists(ChunkPath(node.Path, descriptor.Digest)))
                nodeActions[node.Id].Add(new MirrorRebalanceAction(kind, MirrorRebalanceArtefactKind.Chunk, node.Id, node.Label,
                    ChunkPath(node.Path, descriptor.Digest), descriptor.Digest, descriptor.StoredLength, message));
            if (kind == MirrorRebalanceActionKind.CopyToMirror || File.Exists(MetadataPath(node.Path, descriptor.Digest)))
                nodeActions[node.Id].Add(new MirrorRebalanceAction(kind, MirrorRebalanceArtefactKind.Metadata, node.Id, node.Label,
                    MetadataPath(node.Path, descriptor.Digest), descriptor.Digest, integrityLimits.MaxSidecarBytes, message));
        }
    }

    private IReadOnlyList<string> MirrorManifestIfNeeded(string versionId, byte[] manifestBytes)
    {
        if (mirrorNodes.Count == 0)
        {
            return [];
        }

        var warnings = new List<string>();
        foreach (var mirrorNode in mirrorNodes)
        {
            try
            {
                AtomicWrite(ManifestPath(mirrorNode.Path, versionId), manifestBytes);
            }
            catch (Exception exception) when (IsMirrorIoFailure(exception))
            {
                warnings.Add(FormatMirrorWarning(mirrorNode, $"write manifest {versionId}", exception));
            }
        }

        return warnings;
    }

    private async Task<IReadOnlyList<string>> WriteManifestAsync(
        FileVersionManifest manifest,
        CancellationToken cancellationToken)
    {
        return await WriteManifestsAsync([manifest], cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> WriteManifestsAsync(
        IReadOnlyCollection<FileVersionManifest> manifests,
        CancellationToken cancellationToken)
    {
        if (manifests.Count == 0)
        {
            return [];
        }

        var pending = manifests.ToDictionary(manifest => manifest.VersionId, StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
            await ValidateManifestGraphAsync(manifest, (id, token) => pending.TryGetValue(id, out var item)
                ? Task.FromResult(item) : ReadManifestByVersionAsync(id, token), cancellationToken).ConfigureAwait(false);

        if (metadataStore is not null)
        {
            await metadataStore.RecordVersionsAsync(manifests, cancellationToken).ConfigureAwait(false);
            faults?.Hit(RepositoryFaultPoint.AfterMetadataRecorded, manifests.First().VersionId);
            try
            {
                await metadataStore.ExportOutboxAsync(
                        rootPath,
                        manifests.Select(manifest => manifest.VersionId).ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
                return [];
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return [$"Metadata journal export is lagging: {exception.Message}"];
            }
        }

        var mirrorWarnings = new List<string>();
        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            AtomicWrite(ManifestPath(rootPath, manifest.VersionId), manifestBytes);
            mirrorWarnings.AddRange(MirrorManifestIfNeeded(manifest.VersionId, manifestBytes));
        }

        return mirrorWarnings;
    }

    private async Task<IReadOnlyList<FileVersionManifest>> BuildFolderCascadeManifestsAsync(
        ICollection<FileVersionManifest>? existingManifests,
        FileVersionManifest childManifest,
        string? watchedFolderPath,
        CancellationToken cancellationToken)
    {
        var folderPaths = GetFolderCascadePaths(childManifest.SourcePath, watchedFolderPath);
        if (folderPaths.Count == 0)
        {
            return [];
        }

        var manifests = new List<FileVersionManifest>();
        var currentChild = childManifest;
        foreach (var folderPath in folderPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parent = existingManifests is null
                ? await FindLatestManifestAsync(folderPath, RepositoryEntryKind.Folder, cancellationToken).ConfigureAwait(false)
                : LatestForPath(existingManifests, folderPath, RepositoryEntryKind.Folder);
            var folderEntries = MergeFolderEntries(parent?.FolderEntries, ToFolderEntry(currentChild));
            var logicalLength = folderEntries
                .Where(entry => !entry.IsDeleted)
                .Sum(entry => entry.LogicalLength);
            var folderManifest = new FileVersionManifest(
                VersionId: Guid.CreateVersion7().ToString("N"),
                WatchedFolderId: childManifest.WatchedFolderId,
                SourcePath: folderPath,
                CapturedAtUtc: NextFolderSnapshotTime(currentChild.CapturedAtUtc, parent),
                Consistency: childManifest.Consistency,
                LogicalLength: logicalLength,
                Chunks: [],
                OperationType: VersionOperationType.Capture,
                ParentVersionIds: parent is null ? [] : [parent.VersionId],
                ContentSignature: ComputeFolderContentSignature(folderEntries),
                EntryKind: RepositoryEntryKind.Folder,
                FolderEntries: folderEntries,
                VaultId: binding?.Id);

            manifests.Add(folderManifest);
            existingManifests?.Add(folderManifest);
            currentChild = folderManifest;
        }

        return manifests;
    }

    private static DateTimeOffset NextFolderSnapshotTime(DateTimeOffset candidate, FileVersionManifest? previous)
    {
        if (previous is null) return candidate;
        try
        {
            // Folder snapshots describe serial publication, whereas file timestamps describe capture.
            // PostgreSQL stores microseconds, so advance by at least one microsecond, even for a
            // later candidate within the same microsecond. Selection and publication hold the lease.
            var next = previous.CapturedAtUtc.ToUniversalTime().AddTicks(10);
            return candidate > next ? candidate : next;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest,
                "The next folder snapshot time cannot be represented; no new version was acknowledged.");
        }
    }

    private static MirrorNodeRebalancePreview BuildMirrorNodeRebalancePreview(
        MirrorNodeConfiguration node,
        IReadOnlyList<MirrorRebalanceAction> actions)
    {
        return new MirrorNodeRebalancePreview(
            node.Id,
            node.Label,
            node.Path,
            node.IsEnabled,
            actions.Count == 0 ? RepositoryHealthState.Healthy : RepositoryHealthState.Warning,
            actions.Count,
            actions.Where(action => action.Action == MirrorRebalanceActionKind.CopyToMirror).Sum(action => action.EstimatedBytes),
            actions.Where(action => action.Action == MirrorRebalanceActionKind.DeleteFromMirror).Sum(action => action.EstimatedBytes));
    }

    private MirrorPlacementSelection SelectVerifiedMirrorTargets(ManifestChunk chunk, IReadOnlyDictionary<string, long> usedBytes)
    {
        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(new ChunkMetadata(chunk.Digest, chunk.Length, chunk.StoredLength, chunk.Encoding)).Length;
        var footprint = chunk.StoredLength + metadataBytes;
        var effective = mirrorNodes.ToDictionary(node => node.Id,
            node => ValidateChunk(node.Path, chunk).IsHealthy ? Math.Min(usedBytes[node.Id], node.CapacityBudgetBytes ?? usedBytes[node.Id]) - footprint : usedBytes[node.Id],
            StringComparer.OrdinalIgnoreCase);
        return mirrorPlacementPlanner.SelectChunkTargets(chunk.Digest, footprint, mirrorSet, effective);
    }

    private IReadOnlyDictionary<string, long> GetMirrorNodeUsedBytes()
    {
        return mirrorNodes.ToDictionary(
            node => node.Id,
            node => Directory.Exists(node.Path) ? GetRepositorySize(node.Path) : 0L,
            StringComparer.OrdinalIgnoreCase);
    }

    private ChunkMetadata? ReadChunkMetadata(string root, string digest)
    {
        var path = MetadataPath(root, digest);
        if (!File.Exists(path))
        {
            return null;
        }

        return verifiedReader.ReadMetadataAsync(root, digest, default).GetAwaiter().GetResult();
    }

    private async Task<FileVersionManifest> ReadManifestByVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        VerifiedChunkReader.ValidateHex(versionId, 32, "version id");
        if (metadataStore is null && !File.Exists(ManifestPath(rootPath, versionId)))
            throw new FileNotFoundException($"Manifest {versionId} was not found.");
        var manifest = metadataStore is not null
            ? await metadataStore.ReadManifestAsync(versionId, cancellationToken).ConfigureAwait(false)
            : await ReadManifestAsync(ManifestPath(rootPath, versionId), cancellationToken).ConfigureAwait(false);
        ValidateManifestBinding(manifest);
        if (!string.Equals(manifest.VersionId, versionId, StringComparison.OrdinalIgnoreCase))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Manifest identity does not match the requested version.");
        return manifest;
    }

    private async Task<FileVersionManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken) =>
        (await ReadManifestObjectAsync(manifestPath, cancellationToken).ConfigureAwait(false)).Manifest;

    private async Task<(FileVersionManifest Manifest, byte[] Bytes)> ReadManifestObjectAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var expectedId = Path.GetFileNameWithoutExtension(manifestPath);
        VerifiedChunkReader.ValidateHex(expectedId, 32, "version id");
        var bytes = await VerifiedChunkReader.ReadBoundedAsync(manifestPath, integrityLimits.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = JsonSerializer.Deserialize<FileVersionManifest>(bytes, JsonOptions) ?? throw new JsonException("Null manifest.");
            ValidateManifestBinding(manifest);
            if (!string.Equals(manifest.VersionId, expectedId, StringComparison.OrdinalIgnoreCase))
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Manifest identity disagrees with its stored filename.");
            return (manifest, bytes);
        }
        catch (JsonException)
        {
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Manifest is invalid.");
        }
    }

    private void ValidateManifestBinding(FileVersionManifest manifest)
    {
        if (binding is not null && manifest.VaultId != binding.Id)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "The manifest does not belong to this vault.");
    }

    private async Task<RepositoryRestoreResult> RestoreManifestAsync(FileVersionManifest manifest, string outputPath, bool destinationExisted,
        bool writeRestoreHint, CancellationToken cancellationToken)
    {
        outputPath = Path.GetFullPath(outputPath);
        ValidateRestoreDestination(outputPath);
        var plan = await new RestoreGraphValidator(verifiedReader, integrityLimits, ReadManifestByVersionAsync)
            .BuildAsync(manifest, outputPath, cancellationToken).ConfigureAwait(false);
        var folder = plan.Kind == RepositoryEntryKind.Folder;
        if (folder && (Directory.Exists(outputPath) || File.Exists(outputPath)))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Folder recovery requires a new destination. Existing folders cannot be merged.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var stagedPath = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (folder)
            {
                Directory.CreateDirectory(stagedPath);
                foreach (var directory in plan.Directories) Directory.CreateDirectory(Path.Combine(stagedPath, directory));
                foreach (var file in plan.Files)
                    await WriteVerifiedRestoreFileAsync(file.Manifest, Path.Combine(stagedPath, file.RelativePath), cancellationToken).ConfigureAwait(false);
            }
            else
                await WriteVerifiedRestoreFileAsync(plan.Files.Single().Manifest, stagedPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            faults?.Hit(RepositoryFaultPoint.BeforeRestorePublication, outputPath);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRestoreDestination(outputPath);
            if (folder) Directory.Move(stagedPath, outputPath);
            else File.Move(stagedPath, outputPath, overwrite: destinationExisted);
            var warnings = new List<string>();
            if (writeRestoreHint)
            {
                foreach (var file in plan.Files)
                {
                    var restoredPath = folder ? Path.Combine(outputPath, file.RelativePath) : outputPath;
                    try
                    {
                        faults?.Hit(RepositoryFaultPoint.AfterRestorePublication, restoredPath);
                        faults?.Hit(RepositoryFaultPoint.BeforeRestoreHint, restoredPath);
                        WriteRestoreHint(restoredPath, file.Manifest);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
                    { warnings.Add($"Content was restored and verified, but lineage recording failed for '{restoredPath}': {exception.Message}"); }
                }
            }
            return new RepositoryRestoreResult(outputPath, plan.LogicalBytes, plan.Files.Count, warnings);
        }
        finally
        {
            if (folder && Directory.Exists(stagedPath)) Directory.Delete(stagedPath, recursive: true);
            else if (!folder && File.Exists(stagedPath)) File.Delete(stagedPath);
        }
    }

    private void ValidateRestoreDestination(string outputPath)
    {
        StorageOwnership.RejectReparseComponents(outputPath);
        if (new[] { rootPath }.Concat(configuredMirrorNodes.Select(node => node.Path)).Any(root => StorageOwnership.Contains(root, outputPath)))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Restore destination must be outside repository storage.");
    }

    private async Task WriteVerifiedRestoreFileAsync(FileVersionManifest manifest, string stagedPath, CancellationToken cancellationToken)
    {
        StorageOwnership.RejectReparseComponents(stagedPath);
        await using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        foreach (var chunk in manifest.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await verifiedReader.ReadAsync(rootPath, ChunkDescriptor.FromChunk(chunk), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        if (output.Length != manifest.LogicalLength)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Restored file length mismatch.");
        output.Flush(flushToDisk: true);
    }

    private async Task<IReadOnlyList<FileVersionManifest>> ReadRepairableManifestsAsync(
        bool autoRepairFromMirror,
        List<RepositoryScrubIssue> issues,
        CancellationToken cancellationToken)
    {
        if (metadataStore is not null)
        {
            return await metadataStore.ListManifestsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.Exists(ManifestsPath(rootPath)))
        {
            return [];
        }

        var manifests = new List<FileVersionManifest>();
        foreach (var path in Directory.EnumerateFiles(ManifestsPath(rootPath), "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await ReadManifestByVersionAsync(Path.GetFileNameWithoutExtension(path), cancellationToken).ConfigureAwait(false);
                await ValidateManifestGraphAsync(manifest, ReadManifestByVersionAsync, cancellationToken).ConfigureAwait(false);
                manifests.Add(manifest);
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                var versionId = Path.GetFileNameWithoutExtension(path);
                var repaired = false;
                if (autoRepairFromMirror)
                {
                    foreach (var mirrorNode in mirrorNodes)
                    {
                        var mirrorManifestPath = ManifestPath(mirrorNode.Path, versionId);
                        if (!File.Exists(mirrorManifestPath))
                        {
                            continue;
                        }

                        try
                        {
                            var (mirrorManifest, mirrorManifestBytes) = await ReadManifestObjectAsync(mirrorManifestPath, cancellationToken)
                                .ConfigureAwait(false);
                            await ValidateManifestGraphAsync(mirrorManifest,
                                (id, token) => ReadManifestAsync(ManifestPath(mirrorNode.Path, id), token), cancellationToken).ConfigureAwait(false);
                            AtomicWriteOverwrite(path, mirrorManifestBytes);
                            manifests.Add(mirrorManifest);
                            issues.Add(new RepositoryScrubIssue(
                                RepositoryScrubIssueSeverity.Warning,
                                RepositoryScrubIssueKind.InvalidManifest,
                                path,
                                versionId,
                                null,
                                $"Primary manifest was repaired from mirror '{mirrorNode.Label}'.",
                                RepositoryRepairAction.RepairedPrimaryFromMirror));
                            repaired = true;
                            break;
                        }
                        catch (Exception mirrorException) when (mirrorException is JsonException or IOException or InvalidDataException)
                        {
                        }
                    }
                }

                if (!repaired)
                {
                    issues.Add(new RepositoryScrubIssue(
                        RepositoryScrubIssueSeverity.Critical,
                        RepositoryScrubIssueKind.InvalidManifest,
                        path,
                        versionId,
                        null,
                        $"Manifest could not be read: {exception.Message}",
                        RepositoryRepairAction.Unresolved));
                }
            }
        }

        return manifests;
    }

    private void ValidateMaintenanceInputs(IReadOnlyList<FileVersionManifest> manifests)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            VerifiedChunkReader.ValidateHex(manifest.VersionId, 32, "version id");
            if (!identities.Add(manifest.VersionId) || manifest.LogicalLength < 0 || manifest.Chunks is null || !Enum.IsDefined(manifest.EntryKind))
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Invalid maintenance manifest.");
            if (manifest.IsDeleted)
            {
                VerifiedChunkReader.ValidateHex(manifest.DeletedFromVersionId, 32, "deleted version id");
                if (manifest.Chunks.Count != 0)
                    throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Tombstone contains chunk data.");
            }
            else if (manifest.EntryKind == RepositoryEntryKind.File) RestoreManifestValidator.ValidateFile(manifest, verifiedReader);
            else if (manifest.Chunks.Count != 0)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "Folder contains chunk data.");
            foreach (var entry in manifest.FolderEntries ?? [])
            {
                VerifiedChunkReader.ValidateHex(entry.VersionId, 32, "child version id");
                RestoreGraphValidator.ValidateChildName(entry.Name);
            }
        }
        ValidateReferencedDescriptors(manifests);
        foreach (var root in new[] { rootPath }.Concat(mirrorNodes.Select(node => node.Path)))
        {
            foreach (var manifest in manifests)
            {
                StorageOwnership.RejectReparseComponents(ManifestPath(root, manifest.VersionId));
                foreach (var chunk in manifest.Chunks)
                {
                    StorageOwnership.RejectReparseComponents(ChunkPath(root, chunk.Digest));
                    StorageOwnership.RejectReparseComponents(MetadataPath(root, chunk.Digest));
                }
            }
        }
    }

    private async Task ValidateManifestGraphAsync(FileVersionManifest manifest,
        Func<string, CancellationToken, Task<FileVersionManifest>> read, CancellationToken cancellationToken)
    {
        await new RestoreGraphValidator(verifiedReader, integrityLimits, read)
            .BuildAsync(manifest, manifest.SourcePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FileVersionManifest>> FilterValidManifestGraphsAsync(
        IReadOnlyList<FileVersionManifest> manifests, Action<FileVersionManifest, Exception> report,
        CancellationToken cancellationToken)
    {
        var byId = manifests.ToDictionary(manifest => manifest.VersionId, StringComparer.OrdinalIgnoreCase);
        var valid = new List<FileVersionManifest>();
        foreach (var manifest in manifests)
        {
            try
            {
                await ValidateManifestGraphAsync(manifest, (id, token) => byId.TryGetValue(id, out var item)
                    ? Task.FromResult(item) : ReadManifestByVersionAsync(id, token), cancellationToken).ConfigureAwait(false);
                valid.Add(manifest);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                report(manifest, exception);
            }
        }
        return valid;
    }

    private void ValidateReferencedDescriptors(IEnumerable<FileVersionManifest> manifests)
    {
        foreach (var group in manifests.SelectMany(manifest => manifest.Chunks).GroupBy(chunk => chunk.Digest, StringComparer.OrdinalIgnoreCase))
            verifiedReader.ValidateDescriptor(ChunkDescriptorLookup.Resolve(group)!);
    }

    private ChunkValidation ValidateChunk(string root, ManifestChunk chunk)
    {
        try
        {
            verifiedReader.ReadAsync(root, ChunkDescriptor.FromChunk(chunk), default).GetAwaiter().GetResult();
            return ChunkValidation.Healthy;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ChunkValidation.Unhealthy(exception is RepositoryIntegrityException { Code: RepositoryIntegrityFailure.MissingObject }
                ? RepositoryScrubIssueKind.MissingChunk : RepositoryScrubIssueKind.CorruptChunk, exception.Message);
        }
    }

    private ChunkMetadata? ReadChunkMetadataSafe(string root, string digest)
    {
        try
        {
            return ReadChunkMetadata(root, digest);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static RepositoryHealthState CalculateScrubHealth(IReadOnlyList<RepositoryScrubIssue> issues)
    {
        if (issues.Any(issue => issue.RepairAction == RepositoryRepairAction.Unresolved
                                || issue.Severity == RepositoryScrubIssueSeverity.Critical))
        {
            return RepositoryHealthState.Critical;
        }

        if (issues.Any(issue => issue.RepairAction == RepositoryRepairAction.None))
        {
            return RepositoryHealthState.Warning;
        }

        return RepositoryHealthState.Healthy;
    }

    private async Task<RetentionState> BuildRetentionStateAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var manifests = await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        ValidateMaintenanceInputs(manifests);
        var summaries = manifests.Select(ToSummary).ToArray();

        var decisions = ExpandRetentionReferences(
            RetentionPlanner.Decide(summaries, policy, nowUtc),
            manifests,
            summaries);
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

    private static IReadOnlyList<RepositoryVersionRetentionDecision> ExpandRetentionReferences(
        IReadOnlyList<RepositoryVersionRetentionDecision> baseDecisions,
        IEnumerable<FileVersionManifest> manifests,
        IReadOnlyList<RepositoryVersionSummary> summaries)
    {
        var manifestsById = manifests.ToDictionary(manifest => manifest.VersionId, StringComparer.OrdinalIgnoreCase);
        var reasons = baseDecisions.ToDictionary(
            decision => decision.VersionId,
            decision => decision.Reason,
            StringComparer.OrdinalIgnoreCase);
        var retained = baseDecisions
            .Where(decision => decision.Keep)
            .Select(decision => decision.VersionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(retained);

        while (queue.Count > 0)
        {
            var versionId = queue.Dequeue();
            if (!manifestsById.TryGetValue(versionId, out var manifest))
            {
                continue;
            }

            if (manifest.IsDeleted
                && !string.IsNullOrWhiteSpace(manifest.DeletedFromVersionId)
                && manifestsById.ContainsKey(manifest.DeletedFromVersionId)
                && retained.Add(manifest.DeletedFromVersionId))
            {
                reasons[manifest.DeletedFromVersionId] = $"Referenced by deleted entry {manifest.VersionId}";
                queue.Enqueue(manifest.DeletedFromVersionId);
            }

            foreach (var entry in manifest.FolderEntries ?? [])
            {
                if (!manifestsById.ContainsKey(entry.VersionId) || !retained.Add(entry.VersionId))
                {
                    continue;
                }

                reasons[entry.VersionId] = $"Referenced by folder version {manifest.VersionId}";
                queue.Enqueue(entry.VersionId);
            }
        }

        return summaries
            .OrderByDescending(summary => summary.CapturedAtUtc)
            .ThenByDescending(summary => summary.VersionId, StringComparer.Ordinal)
            .Select(summary =>
            {
                var keep = retained.Contains(summary.VersionId);
                return new RepositoryVersionRetentionDecision(
                    summary.VersionId,
                    summary.SourcePath,
                    summary.CapturedAtUtc,
                    keep,
                    keep ? reasons[summary.VersionId] : "Pruned by retention policy",
                    summary.LogicalLength,
                    summary.ChunkCount);
            })
            .ToArray();
    }

    private static IReadOnlyList<RepositoryPurgeScope> NormalisePurgeScopes(IEnumerable<RepositoryPurgeScope> scopes)
    {
        return scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope.SourcePath))
            .Select(scope => scope with { SourcePath = Path.GetFullPath(scope.SourcePath) })
            .DistinctBy(scope => $"{scope.Kind}:{NormaliseDirectoryPath(scope.SourcePath)}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<string> SelectInitialPurgeIds(
        IEnumerable<FileVersionManifest> manifests,
        IReadOnlyList<RepositoryPurgeScope> scopes,
        IReadOnlyList<RepositoryPurgeScope> preserveScopes)
    {
        return manifests
            .Where(manifest => scopes.Any(scope => PurgeScopeMatches(scope, manifest)))
            .Where(manifest => !preserveScopes.Any(scope => PurgeScopeMatches(scope, manifest)))
            .Select(manifest => manifest.VersionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ExpandPurgeReferences(
        IReadOnlyList<FileVersionManifest> manifests,
        HashSet<string> purgeIds)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var manifest in manifests)
            {
                if (purgeIds.Contains(manifest.VersionId))
                {
                    continue;
                }

                if (ReferencesAnyPurgedVersion(manifest, purgeIds)
                    && purgeIds.Add(manifest.VersionId))
                {
                    changed = true;
                }
            }
        }
    }

    private static bool ReferencesAnyPurgedVersion(
        FileVersionManifest manifest,
        ISet<string> purgeIds)
    {
        return (manifest.ParentVersionIds ?? []).Any(purgeIds.Contains)
               || (!string.IsNullOrWhiteSpace(manifest.DeletedFromVersionId) && purgeIds.Contains(manifest.DeletedFromVersionId))
               || (!string.IsNullOrWhiteSpace(manifest.RestoredFromVersionId) && purgeIds.Contains(manifest.RestoredFromVersionId))
               || (!string.IsNullOrWhiteSpace(manifest.ForkOriginVersionId) && purgeIds.Contains(manifest.ForkOriginVersionId))
               || (!string.IsNullOrWhiteSpace(manifest.InheritedFromVersionId) && purgeIds.Contains(manifest.InheritedFromVersionId))
               || (manifest.FolderEntries ?? []).Any(entry => purgeIds.Contains(entry.VersionId));
    }

    private static bool PurgeScopeMatches(RepositoryPurgeScope scope, FileVersionManifest manifest)
    {
        return scope.Kind switch
        {
            RepositoryPurgeScopeKind.File => manifest.EntryKind == RepositoryEntryKind.File
                                             && PathEquals(manifest.SourcePath, scope.SourcePath),
            RepositoryPurgeScopeKind.ImmediateFiles => manifest.EntryKind == RepositoryEntryKind.File
                                                       ? IsDirectChildFile(manifest.SourcePath, scope.SourcePath)
                                                       : manifest.EntryKind == RepositoryEntryKind.Folder
                                                         && PathEquals(manifest.SourcePath, scope.SourcePath),
            RepositoryPurgeScopeKind.RecursiveFolder => PathIsWithinOrEqual(manifest.SourcePath, scope.SourcePath),
            _ => false
        };
    }

    private static IReadOnlyDictionary<string, long> CountChunkReferences(
        IEnumerable<FileVersionManifest> manifests,
        IReadOnlyCollection<string> requestedDigests)
    {
        var requested = requestedDigests.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = requested.ToDictionary(digest => digest, _ => 0L, StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in manifests.SelectMany(manifest => manifest.Chunks))
        {
            if (requested.Contains(chunk.Digest))
            {
                counts[chunk.Digest] = counts.GetValueOrDefault(chunk.Digest) + 1;
            }
        }

        return counts;
    }

    private async Task<IReadOnlyList<FileVersionManifest>> ReadAllManifestsAsync(CancellationToken cancellationToken)
    {
        if (metadataStore is not null)
        {
            return await metadataStore.ListManifestsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.Exists(ManifestsPath(rootPath)))
        {
            return [];
        }

        var manifests = new List<FileVersionManifest>();
        foreach (var path in Directory.EnumerateFiles(ManifestsPath(rootPath), "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifests.Add(await ReadManifestByVersionAsync(Path.GetFileNameWithoutExtension(path), cancellationToken).ConfigureAwait(false));
        }

        return manifests;
    }

    private async Task<List<FileVersionManifest>> ReadAllManifestsForMutationAsync(CancellationToken cancellationToken)
    {
        if (mutationManifestCache is not null)
        {
            return mutationManifestCache;
        }

        mutationManifestCache = (await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        return mutationManifestCache;
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
        StorageOwnership.RejectReparseComponents(path);
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
        foreach (var mirrorNode in mirrorNodes)
        {
            var path = pathFactory(mirrorNode.Path, key);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                StorageOwnership.RejectReparseComponents(path);
                File.Delete(path);
            }
            catch (Exception exception) when (IsMirrorIoFailure(exception))
            {
                warnings.Add(FormatMirrorWarning(mirrorNode, $"delete artefact {path}", exception));
            }
        }
    }

    private static bool IsMirrorIoFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or NotSupportedException;
    }

    private static string FormatMirrorWarning(MirrorNodeConfiguration mirrorNode, string operation, Exception exception)
    {
        return $"Mirror '{mirrorNode.Label}' at {mirrorNode.Path} is unavailable. Last error while trying to {operation}: {exception.Message}";
    }

    private static IReadOnlyList<string> CondenseMirrorWarnings(IEnumerable<string> warnings)
    {
        var seenNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var condensed = new List<string>();
        foreach (var warning in warnings)
        {
            var key = warning.Split(" Last error ", 2, StringSplitOptions.None)[0];
            if (seenNodes.Add(key))
            {
                condensed.Add(warning);
            }
        }

        return condensed;
    }

    private static long GetFileLength(string path)
    {
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        StorageOwnership.RejectReparseComponents(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (existing.Length != bytes.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Existing publication conflicts with expected bytes.");
            return;
        }
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(tempPath, path, overwrite: false);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private void WriteRestoreHint(string outputPath, FileVersionManifest sourceManifest)
    {
        var destinationPath = Path.GetFullPath(outputPath);
        var forkOriginVersionId = sourceManifest.ForkOriginVersionId
            ?? sourceManifest.RestoredFromVersionId
            ?? sourceManifest.InheritedFromVersionId
            ?? sourceManifest.VersionId;
        var hint = new RestoreLineageHint(
            destinationPath,
            sourceManifest.VersionId,
            forkOriginVersionId,
            sourceManifest.SourcePath,
            DateTimeOffset.UtcNow);
        AtomicWriteOverwrite(
            RestoreHintPath(rootPath, destinationPath),
            JsonSerializer.SerializeToUtf8Bytes(hint, JsonOptions));
    }

    private RestoreLineageHint? ReadRestoreHint(string sourcePath)
    {
        var hintPath = RestoreHintPath(rootPath, sourcePath);
        if (!File.Exists(hintPath))
        {
            return null;
        }

        var hint = JsonSerializer.Deserialize<RestoreLineageHint>(File.ReadAllBytes(hintPath), JsonOptions);
        return hint is not null && PathEquals(hint.DestinationPath, sourcePath) ? hint : null;
    }

    private void DeleteRestoreHint(string sourcePath)
    {
        var hintPath = RestoreHintPath(rootPath, sourcePath);
        if (File.Exists(hintPath))
        {
            File.Delete(hintPath);
        }
    }

    private async Task<LineageResolution> ResolveMetadataBackedLineageAsync(
        string sourcePath,
        string contentSignature,
        bool isSyncOrigin,
        RestoreLineageHint? restoreHint,
        CancellationToken cancellationToken)
    {
        var samePathParent = await FindLatestManifestAsync(sourcePath, RepositoryEntryKind.File, cancellationToken)
            .ConfigureAwait(false);
        if (isSyncOrigin)
        {
            return new LineageResolution(
                VersionOperationType.RemoteSync,
                samePathParent is null ? [] : [samePathParent.VersionId],
                null,
                samePathParent is null ? null : GetForkOriginVersionId(samePathParent),
                null,
                null);
        }

        if (restoreHint is not null)
        {
            return new LineageResolution(
                VersionOperationType.Restore,
                samePathParent is null ? [] : [samePathParent.VersionId],
                restoreHint.RestoredFromVersionId,
                restoreHint.ForkOriginVersionId,
                null,
                null);
        }

        if (samePathParent is not null)
        {
            return new LineageResolution(
                VersionOperationType.Capture,
                [samePathParent.VersionId],
                null,
                GetForkOriginVersionId(samePathParent),
                null,
                null);
        }

        var inheritedFrom = await FindLiveFileByContentSignatureAsync(sourcePath, contentSignature, cancellationToken)
            .ConfigureAwait(false);
        if (inheritedFrom is not null)
        {
            return new LineageResolution(
                VersionOperationType.InheritedCopy,
                [inheritedFrom.VersionId],
                null,
                GetForkOriginVersionId(inheritedFrom) ?? inheritedFrom.VersionId,
                inheritedFrom.VersionId,
                inheritedFrom.SourcePath);
        }

        return new LineageResolution(VersionOperationType.Capture, [], null, null, null, null);
    }

    private async Task<FileVersionManifest?> FindLatestManifestAsync(
        string sourcePath,
        RepositoryEntryKind entryKind,
        CancellationToken cancellationToken)
    {
        if (metadataStore is not null)
        {
            try
            {
                return await metadataStore.FindLatestManifestAsync(sourcePath, entryKind, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
            }
        }

        return LatestForPath(
            await ReadAllManifestsForMutationAsync(cancellationToken).ConfigureAwait(false),
            sourcePath,
            entryKind);
    }

    private async Task<FileVersionManifest?> FindLiveFileByContentSignatureAsync(
        string sourcePath,
        string contentSignature,
        CancellationToken cancellationToken)
    {
        if (metadataStore is not null)
        {
            try
            {
                return await metadataStore.FindLiveFileByContentSignatureAsync(sourcePath, contentSignature, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
            }
        }

        return (await ReadAllManifestsForMutationAsync(cancellationToken).ConfigureAwait(false))
            .Where(manifest => !PathEquals(manifest.SourcePath, sourcePath))
            .Where(manifest => manifest.EntryKind == RepositoryEntryKind.File && !manifest.IsDeleted)
            .Where(manifest => string.Equals(GetContentSignature(manifest), contentSignature, StringComparison.Ordinal))
            .OrderBy(manifest => manifest.CapturedAtUtc)
            .ThenBy(manifest => manifest.VersionId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private LineageResolution ResolveRestoreLineage(
        string sourcePath,
        RestoreLineageHint hint,
        IReadOnlyList<FileVersionManifest> existingManifests)
    {
        var samePathParent = LatestForPath(existingManifests, sourcePath, RepositoryEntryKind.File);
        return new LineageResolution(
            VersionOperationType.Restore,
            samePathParent is null ? [] : [samePathParent.VersionId],
            hint.RestoredFromVersionId,
            hint.ForkOriginVersionId,
            null,
            null);
    }

    private LineageResolution ResolveCaptureLineage(
        string sourcePath,
        string contentSignature,
        IReadOnlyList<FileVersionManifest> existingManifests)
    {
        var samePathParent = LatestForPath(existingManifests, sourcePath, RepositoryEntryKind.File);
        if (samePathParent is not null)
        {
            return new LineageResolution(
                VersionOperationType.Capture,
                [samePathParent.VersionId],
                null,
                GetForkOriginVersionId(samePathParent),
                null,
                null);
        }

        var inheritedFrom = existingManifests
            .Where(manifest => !PathEquals(manifest.SourcePath, sourcePath))
            .Where(manifest => manifest.EntryKind == RepositoryEntryKind.File && !manifest.IsDeleted)
            .Where(manifest => string.Equals(GetContentSignature(manifest), contentSignature, StringComparison.Ordinal))
            .OrderBy(manifest => manifest.CapturedAtUtc)
            .ThenBy(manifest => manifest.VersionId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (inheritedFrom is not null)
        {
            return new LineageResolution(
                VersionOperationType.InheritedCopy,
                [inheritedFrom.VersionId],
                null,
                GetForkOriginVersionId(inheritedFrom) ?? inheritedFrom.VersionId,
                inheritedFrom.VersionId,
                inheritedFrom.SourcePath);
        }

        return new LineageResolution(VersionOperationType.Capture, [], null, null, null, null);
    }

    private LineageResolution ResolveSyncLineage(
        string sourcePath,
        IReadOnlyList<FileVersionManifest> existingManifests)
    {
        var samePathParent = LatestForPath(existingManifests, sourcePath, RepositoryEntryKind.File);
        return new LineageResolution(
            VersionOperationType.RemoteSync,
            samePathParent is null ? [] : [samePathParent.VersionId],
            null,
            samePathParent is null ? null : GetForkOriginVersionId(samePathParent),
            null,
            null);
    }

    private static FileVersionManifest? LatestForPath(
        IEnumerable<FileVersionManifest> manifests,
        string sourcePath,
        RepositoryEntryKind entryKind)
    {
        return manifests
            .Where(manifest => PathEquals(manifest.SourcePath, sourcePath))
            .Where(manifest => manifest.EntryKind == entryKind)
            .OrderByDescending(manifest => manifest.CapturedAtUtc)
            .ThenByDescending(manifest => manifest.VersionId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string? GetForkOriginVersionId(FileVersionManifest manifest)
    {
        return manifest.ForkOriginVersionId
            ?? manifest.RestoredFromVersionId
            ?? manifest.InheritedFromVersionId;
    }

    private RepositoryVersionSummary ToSummary(FileVersionManifest manifest)
    {
        return new RepositoryVersionSummary(
            manifest.VersionId,
            manifest.SourcePath,
            manifest.CapturedAtUtc,
            manifest.Consistency,
            manifest.LogicalLength,
            manifest.Chunks.Count,
            manifest.OperationType,
            manifest.ParentVersionIds,
            manifest.RestoredFromVersionId,
            manifest.ForkOriginVersionId,
            manifest.InheritedFromVersionId,
            manifest.InheritedFromSourcePath,
            GetContentSignature(manifest),
            manifest.SyncOrigin,
            manifest.EntryKind,
            manifest.IsDeleted,
            manifest.FolderEntries,
            manifest.DeletedFromVersionId,
            manifest.SourceLastWriteUtc);
    }

    private string GetContentSignature(FileVersionManifest manifest)
    {
        return manifest.ContentSignature ?? ComputeContentSignature(manifest.LogicalLength, manifest.Chunks);
    }

    private static string ComputeContentSignature(long logicalLength, IReadOnlyList<ManifestChunk> chunks) =>
        RepositoryMetadataStoreHelpers.ComputeContentSignature(logicalLength, chunks);

    private static string ComputeFolderContentSignature(IReadOnlyList<FolderVersionEntry> entries) =>
        RepositoryMetadataStoreHelpers.ComputeFolderContentSignature(entries);

    private string ComputeDeletedContentSignature(string sourcePath, string deletedFromVersionId)
    {
        return hasher.Hash(Encoding.UTF8.GetBytes(
            $"fv-deleted-v1:{Path.GetFullPath(sourcePath)}:{deletedFromVersionId}"));
    }

    private static IReadOnlyList<FolderVersionEntry> MergeFolderEntries(
        IReadOnlyList<FolderVersionEntry>? currentEntries,
        FolderVersionEntry changedEntry)
    {
        return (currentEntries ?? [])
            .Where(entry => !string.Equals(ToEntryKey(entry.SourcePath, entry.EntryKind), ToEntryKey(changedEntry.SourcePath, changedEntry.EntryKind), StringComparison.OrdinalIgnoreCase))
            .Append(changedEntry)
            .OrderByDescending(entry => entry.EntryKind)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static FolderVersionEntry ToFolderEntry(FileVersionManifest manifest)
    {
        var sourcePath = NormaliseDirectoryPath(manifest.SourcePath);
        var name = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = sourcePath;
        }

        return new FolderVersionEntry(
            name,
            sourcePath,
            manifest.EntryKind,
            manifest.VersionId,
            manifest.IsDeleted,
            manifest.LogicalLength,
            manifest.CapturedAtUtc);
    }

    private static IReadOnlyList<string> GetFolderCascadePaths(string sourcePath, string? watchedFolderPath)
    {
        if (string.IsNullOrWhiteSpace(watchedFolderPath))
        {
            return [];
        }

        var watchedRoot = NormaliseDirectoryPath(watchedFolderPath);
        var current = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        if (string.IsNullOrWhiteSpace(current))
        {
            return [];
        }

        var folders = new List<string>();
        while (!string.IsNullOrWhiteSpace(current))
        {
            var normalised = NormaliseDirectoryPath(current);
            if (!PathIsWithinOrEqual(normalised, watchedRoot))
            {
                break;
            }

            folders.Add(normalised);
            if (PathEquals(normalised, watchedRoot))
            {
                break;
            }

            current = Path.GetDirectoryName(normalised);
        }

        return folders;
    }

    private static bool PathIsWithinOrEqual(string path, string parentPath)
    {
        var normalisedPath = NormaliseDirectoryPath(path);
        var normalisedParent = NormaliseDirectoryPath(parentPath);
        if (string.Equals(normalisedPath, normalisedParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parentWithSeparator = normalisedParent.EndsWith(Path.DirectorySeparatorChar)
            ? normalisedParent
            : normalisedParent + Path.DirectorySeparatorChar;
        return normalisedPath.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectChildFile(string filePath, string folderPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
        return !string.IsNullOrWhiteSpace(parent)
               && PathEquals(parent, folderPath);
    }

    private static string NormaliseDirectoryPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ToEntryKey(string sourcePath, RepositoryEntryKind entryKind)
    {
        return $"{entryKind}:{NormaliseDirectoryPath(sourcePath)}";
    }

    private static void AtomicWriteOverwrite(string path, byte[] bytes)
    {
        StorageOwnership.RejectReparseComponents(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(tempPath, path, overwrite: true);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string ChunksPath(string root) => Path.Combine(root, "chunks");

    private static string ManifestsPath(string root) => Path.Combine(root, "manifests");

    private static string LineagePath(string root) => Path.Combine(root, "lineage");

    private static string RestoreHintsPath(string root) => Path.Combine(LineagePath(root), "restore-hints");

    private static string ChunkPath(string root, string digest)
    {
        VerifiedChunkReader.ValidateHex(digest, 64, "chunk digest");
        return Path.Combine(ChunksPath(root), digest[..2], $"{digest}.chunk");
    }

    private static string MetadataPath(string root, string digest)
    {
        VerifiedChunkReader.ValidateHex(digest, 64, "chunk digest");
        return Path.Combine(ChunksPath(root), digest[..2], $"{digest}.json");
    }

    private static string ManifestPath(string root, string versionId)
    {
        VerifiedChunkReader.ValidateHex(versionId, 32, "version id");
        return Path.Combine(ManifestsPath(root), $"{versionId}.json");
    }

    private static string RestoreHintPath(string root, string sourcePath)
    {
        return Path.Combine(RestoreHintsPath(root), $"{HashPathKey(sourcePath)}.json");

        static string HashPathKey(string value)
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                    Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant())))
                .ToLowerInvariant();
        }
    }

    private sealed record PreparedChunk(byte[] Payload, ChunkMetadata Metadata);

    private sealed record ChunkValidation(
        bool IsHealthy,
        RepositoryScrubIssueKind? IssueKind = null,
        string? Message = null)
    {
        public static ChunkValidation Healthy { get; } = new(true);

        public static ChunkValidation Unhealthy(RepositoryScrubIssueKind issueKind, string message)
        {
            return new ChunkValidation(false, issueKind, message);
        }
    }

    private sealed record RestoreLineageHint(
        string DestinationPath,
        string RestoredFromVersionId,
        string ForkOriginVersionId,
        string SourcePath,
        DateTimeOffset CreatedAtUtc);

    private sealed record LineageResolution(
        VersionOperationType OperationType,
        IReadOnlyList<string> ParentVersionIds,
        string? RestoredFromVersionId,
        string? ForkOriginVersionId,
        string? InheritedFromVersionId,
        string? InheritedFromSourcePath);

    private sealed record RetentionState(
        IReadOnlyList<RepositoryVersionRetentionDecision> Decisions,
        int KeptVersionCount,
        int PrunableVersionCount,
        IReadOnlyList<FileVersionManifest> PrunableManifests,
        IReadOnlyList<string> PrunableChunkDigests,
        long PrunableStorageBytes);
}
