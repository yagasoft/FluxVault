using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Retention;

namespace FluxVault.Core.Storage;

public sealed class FileSystemChunkRepository : IChunkRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string rootPath;
    private readonly MirrorSetConfiguration mirrorSet;
    private readonly IReadOnlyList<MirrorNodeConfiguration> mirrorNodes;
    private readonly MirrorPlacementPlanner mirrorPlacementPlanner = new();
    private readonly StreamingFastCdcChunker streamingChunker;
    private readonly Blake3ContentHasher hasher;
    private readonly ZstdChunkCodec codec;

    public FileSystemChunkRepository(
        string rootPath,
        FastCdcChunker chunker,
        Blake3ContentHasher hasher,
        ZstdChunkCodec codec,
        string? mirrorPath = null)
        : this(rootPath, chunker, hasher, codec, MirrorSetConfiguration.FromLegacyPath(mirrorPath))
    {
    }

    public FileSystemChunkRepository(
        string rootPath,
        FastCdcChunker chunker,
        Blake3ContentHasher hasher,
        ZstdChunkCodec codec,
        MirrorSetConfiguration? mirrorSet)
    {
        this.rootPath = rootPath;
        streamingChunker = new StreamingFastCdcChunker(chunker.Options);
        this.hasher = hasher;
        this.codec = codec;
        this.mirrorSet = (mirrorSet ?? MirrorSetConfiguration.CreateDefault()).Normalise();
        mirrorNodes = this.mirrorSet.EnabledNodes;
    }

    public async Task<FileCommitResult> CommitAsync(FileCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(ChunksPath(rootPath));
        Directory.CreateDirectory(ManifestsPath(rootPath));

        await using var content = request.Content;
        var chunks = new List<ManifestChunk>();
        var mirrorWarnings = new List<string>();
        var newChunkCount = 0;
        var logicalLength = 0L;

        await foreach (var chunk in streamingChunker.ChunkAsync(content, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = chunk.Payload.AsSpan();
            var digest = hasher.Hash(raw);
            var metadata = ReadChunkMetadata(rootPath, digest);

            if (metadata is null)
            {
                var prepared = PreparePayload(raw, digest, request.Compression, request.MinimumCompressionBytes);
                AtomicWrite(ChunkPath(rootPath, digest), prepared.Payload);
                AtomicWrite(MetadataPath(rootPath, digest), JsonSerializer.SerializeToUtf8Bytes(prepared.Metadata, JsonOptions));
                mirrorWarnings.AddRange(MirrorChunkIfNeeded(digest, prepared));
                metadata = prepared.Metadata;
                newChunkCount++;
            }

            chunks.Add(new ManifestChunk(digest, chunk.Offset, chunk.Payload.Length, metadata.StoredLength, metadata.Encoding));
            logicalLength += chunk.Payload.Length;
        }

        var sourcePath = Path.GetFullPath(request.SourcePath);
        var existingManifests = await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        var contentSignature = ComputeContentSignature(logicalLength, chunks);
        var lineage = ReadRestoreHint(sourcePath) is { } restoreHint
            ? ResolveRestoreLineage(sourcePath, restoreHint, existingManifests)
            : ResolveCaptureLineage(sourcePath, contentSignature, existingManifests);

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
            ContentSignature: contentSignature);

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        AtomicWrite(ManifestPath(rootPath, manifest.VersionId), manifestBytes);
        mirrorWarnings.AddRange(MirrorManifestIfNeeded(manifest.VersionId, manifestBytes));
        if (lineage.OperationType == VersionOperationType.Restore)
        {
            DeleteRestoreHint(sourcePath);
        }

        return new FileCommitResult(manifest, newChunkCount, CondenseMirrorWarnings(mirrorWarnings));
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
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

    public async Task<RepositoryInspection> InspectAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        var manifest = await ReadManifestByVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        return new RepositoryInspection(
            manifest,
            manifest.Chunks.Count,
            manifest.LogicalLength,
            manifest.Chunks.Sum(chunk => (long)chunk.StoredLength));
    }

    public async Task RestoreAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var manifest = await ReadManifestByVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        await RestoreManifestAsync(manifest, outputPath, writeRestoreHint: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RepositoryScrubReport> ScrubAsync(
        bool autoRepairFromMirror,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<RepositoryScrubIssue>();
        var manifests = await ReadRepairableManifestsAsync(autoRepairFromMirror, issues, cancellationToken).ConfigureAwait(false);
        var checkedChunks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                            RepairChunk(mirrorNode.Path, rootPath, chunk.Digest);
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
                    }

                    continue;
                }

                var targetNodeIds = mirrorPlacementPlanner
                    .SelectChunkTargets(chunk.Digest, chunk.StoredLength, mirrorSet, GetMirrorNodeUsedBytes())
                    .TargetNodeIds
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                        RepairChunk(rootPath, mirrorNode.Path, chunk.Digest);
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
        var allNodes = mirrorNodes.ToArray();
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
            var primaryManifestPath = ManifestPath(rootPath, manifest.VersionId);
            var primaryManifestBytes = await File.ReadAllBytesAsync(primaryManifestPath, cancellationToken).ConfigureAwait(false);

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
                    AtomicWriteOverwrite(ManifestPath(node.Path, manifest.VersionId), primaryManifestBytes);
                    manifestIssue = manifestIssue with { RepairAction = MirrorRepairAction.RepairedMirrorFromPrimary };
                }

                nodeIssues[node.Id].Add(manifestIssue);
            }

            foreach (var chunk in manifest.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                        RepairChunk(sourceMirror.Path, rootPath, chunk.Digest);
                        primaryHealthy = true;
                    }

                    primaryIssues.Add(issue);
                }

                if (!primaryHealthy)
                {
                    continue;
                }

                var targetNodeIds = mirrorPlacementPlanner
                    .SelectChunkTargets(chunk.Digest, chunk.StoredLength, mirrorSet, GetMirrorNodeUsedBytes())
                    .TargetNodeIds
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                        RepairChunk(rootPath, node.Path, chunk.Digest);
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
                manifests.Add(await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false));
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
                            var mirrorManifest = await ReadManifestAsync(mirrorManifestPath, cancellationToken).ConfigureAwait(false);
                            AtomicWriteOverwrite(path, await File.ReadAllBytesAsync(mirrorManifestPath, cancellationToken).ConfigureAwait(false));
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
            var bytes = File.ReadAllBytes(path);
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
                var payload = File.ReadAllBytes(path);
                if (payload.Length != manifestChunk.StoredLength)
                {
                    return NewChunkIssue($"Mirror '{node.Label}' chunk stored length differs from the manifest.");
                }

                var raw = manifestChunk.Encoding == ChunkEncoding.Raw
                    ? payload
                    : codec.Decompress(payload, manifestChunk.Length, manifestChunk.Encoding);
                if (raw.Length != manifestChunk.Length)
                {
                    return NewChunkIssue($"Mirror '{node.Label}' chunk logical length differs from the manifest.");
                }

                var digest = hasher.Hash(raw);
                if (!string.Equals(digest, manifestChunk.Digest, StringComparison.OrdinalIgnoreCase))
                {
                    return NewChunkIssue($"Mirror '{node.Label}' chunk digest differs from the manifest.");
                }

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
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        var requested = Math.Max(0, maxVersions);
        var manifests = (await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(manifest => manifest.CapturedAtUtc)
            .ThenByDescending(manifest => manifest.VersionId, StringComparer.Ordinal)
            .Take(requested)
            .ToArray();
        var results = new List<RestoreRehearsalResult>();

        try
        {
            Directory.CreateDirectory(tempRoot);
            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = string.IsNullOrWhiteSpace(Path.GetFileName(manifest.SourcePath))
                    ? $"{manifest.VersionId}.rehearsal"
                    : $"{manifest.VersionId}-{Path.GetFileName(manifest.SourcePath)}";
                var outputPath = Path.Combine(tempRoot, fileName);
                try
                {
                    await RestoreManifestAsync(manifest, outputPath, writeRestoreHint: false, cancellationToken)
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
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }

        var failures = results.Count(result => !result.Success);
        return new RestoreRehearsalReport(
            CompletedAtUtc: DateTimeOffset.UtcNow,
            HealthState: failures == 0 ? RepositoryHealthState.Healthy : RepositoryHealthState.Critical,
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
        var state = await BuildRetentionStateAsync(policy, nowUtc, cancellationToken).ConfigureAwait(false);
        var mirrorWarnings = new List<string>();
        var reclaimedBytes = 0L;

        foreach (var manifest in state.PrunableManifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reclaimedBytes += DeleteLocalFile(ManifestPath(rootPath, manifest.VersionId));
            DeleteMirrorFile(ManifestPath, manifest.VersionId, mirrorWarnings);
        }

        var deletedChunkCount = 0;
        foreach (var digest in state.PrunableChunkDigests)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private IReadOnlyList<string> MirrorChunkIfNeeded(string digest, PreparedChunk prepared)
    {
        if (mirrorNodes.Count == 0)
        {
            return [];
        }

        var warnings = new List<string>();
        var selection = mirrorPlacementPlanner.SelectChunkTargets(
            digest,
            prepared.Metadata.StoredLength,
            mirrorSet,
            GetMirrorNodeUsedBytes());
        if (selection.IsUnderSatisfied)
        {
            warnings.Add($"Mirror placement for chunk {digest} is under-satisfied: {selection.TargetNodeIds.Count} of {selection.RequiredCopyCount} required mirror copy/copies are available.");
        }

        foreach (var mirrorNode in selection.TargetNodes)
        {
            try
            {
                AtomicWrite(ChunkPath(mirrorNode.Path, digest), prepared.Payload);
                AtomicWrite(MetadataPath(mirrorNode.Path, digest), JsonSerializer.SerializeToUtf8Bytes(prepared.Metadata, JsonOptions));
            }
            catch (Exception exception) when (IsMirrorIoFailure(exception))
            {
                warnings.Add(FormatMirrorWarning(mirrorNode, $"write chunk {digest}", exception));
            }
        }

        return warnings;
    }

    public async Task<MirrorRebalancePreviewReport> PreviewMirrorRebalanceAsync(
        CancellationToken cancellationToken = default)
    {
        return await BuildMirrorRebalanceReportAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MirrorRebalancePreviewReport> RunMirrorRebalanceAsync(
        CancellationToken cancellationToken = default)
    {
        var initial = await BuildMirrorRebalanceReportAsync(cancellationToken).ConfigureAwait(false);
        foreach (var action in initial.Actions.Where(action => action.Action == MirrorRebalanceActionKind.CopyToMirror))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = action.ArtefactKind == MirrorRebalanceArtefactKind.Chunk
                ? ChunkPath(rootPath, action.ChunkDigest)
                : MetadataPath(rootPath, action.ChunkDigest);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            AtomicWrite(action.Path, await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false));
        }

        var afterCopies = await BuildMirrorRebalanceReportAsync(cancellationToken).ConfigureAwait(false);
        var blockedChunks = afterCopies.Actions
            .Where(action => action.Action is MirrorRebalanceActionKind.CopyToMirror or MirrorRebalanceActionKind.Unresolved)
            .Select(action => action.ChunkDigest)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var action in afterCopies.Actions.Where(action => action.Action == MirrorRebalanceActionKind.DeleteFromMirror))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blockedChunks.Contains(action.ChunkDigest))
            {
                continue;
            }

            if (File.Exists(action.Path))
            {
                File.Delete(action.Path);
            }
        }

        return await BuildMirrorRebalanceReportAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MirrorRebalancePreviewReport> BuildMirrorRebalanceReportAsync(
        CancellationToken cancellationToken)
    {
        var manifests = await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        var chunks = manifests
            .SelectMany(manifest => manifest.Chunks)
            .GroupBy(chunk => chunk.Digest, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var nodeActions = mirrorNodes.ToDictionary(
            node => node.Id,
            _ => new List<MirrorRebalanceAction>(),
            StringComparer.OrdinalIgnoreCase);
        var usedBytes = GetMirrorNodeUsedBytes();

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var primaryValidation = ValidateChunk(rootPath, chunk);
            var selection = mirrorPlacementPlanner.SelectChunkTargets(
                chunk.Digest,
                chunk.StoredLength,
                mirrorSet,
                usedBytes);
            var targetIds = selection.TargetNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var node in mirrorNodes)
            {
                if (!nodeActions.TryGetValue(node.Id, out var currentNodeActions))
                {
                    continue;
                }

                if (!Directory.Exists(node.Path))
                {
                    AddUnavailableNodeAction(currentNodeActions, node, chunk.Digest);
                    continue;
                }

                var chunkPath = ChunkPath(node.Path, chunk.Digest);
                var metadataPath = MetadataPath(node.Path, chunk.Digest);
                if (targetIds.Contains(node.Id))
                {
                    AddMissingAction(currentNodeActions, node, chunk, chunkPath, MirrorRebalanceArtefactKind.Chunk, primaryValidation.IsHealthy);
                    AddMissingAction(currentNodeActions, node, chunk, metadataPath, MirrorRebalanceArtefactKind.Metadata, primaryValidation.IsHealthy);
                }
                else
                {
                    AddExtraAction(currentNodeActions, node, chunk, chunkPath, MirrorRebalanceArtefactKind.Chunk);
                    AddExtraAction(currentNodeActions, node, chunk, metadataPath, MirrorRebalanceArtefactKind.Metadata);
                }
            }
        }

        var actions = nodeActions.Values.SelectMany(static actions => actions).ToArray();
        var nodes = mirrorNodes
            .Select(node => BuildMirrorNodeRebalancePreview(node, nodeActions[node.Id]))
            .ToArray();
        return new MirrorRebalancePreviewReport(
            DateTimeOffset.UtcNow,
            actions.Length == 0 ? RepositoryHealthState.Healthy : RepositoryHealthState.Warning,
            chunks.Length,
            actions.Length,
            actions.Where(action => action.Action == MirrorRebalanceActionKind.CopyToMirror).Sum(action => action.EstimatedBytes),
            actions.Where(action => action.Action == MirrorRebalanceActionKind.DeleteFromMirror).Sum(action => action.EstimatedBytes),
            nodes,
            actions);
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

    private static void AddMissingAction(
        List<MirrorRebalanceAction> actions,
        MirrorNodeConfiguration node,
        ManifestChunk chunk,
        string path,
        MirrorRebalanceArtefactKind artefactKind,
        bool isPrimaryHealthy)
    {
        if (File.Exists(path))
        {
            return;
        }

        if (!isPrimaryHealthy)
        {
            actions.Add(new MirrorRebalanceAction(
                MirrorRebalanceActionKind.Unresolved,
                artefactKind,
                node.Id,
                node.Label,
                path,
                chunk.Digest,
                0,
                $"Mirror '{node.Label}' cannot receive {artefactKind.ToString().ToLowerInvariant()} for chunk {chunk.Digest} because the primary repository copy is missing or corrupt."));
            return;
        }

        actions.Add(new MirrorRebalanceAction(
            MirrorRebalanceActionKind.CopyToMirror,
            artefactKind,
            node.Id,
            node.Label,
            path,
            chunk.Digest,
            artefactKind == MirrorRebalanceArtefactKind.Chunk ? chunk.StoredLength : GetFileLength(path),
            $"Mirror '{node.Label}' is missing required {artefactKind.ToString().ToLowerInvariant()} for chunk {chunk.Digest}."));
    }

    private static void AddUnavailableNodeAction(
        List<MirrorRebalanceAction> actions,
        MirrorNodeConfiguration node,
        string chunkDigest)
    {
        if (actions.Any(action => action.Action == MirrorRebalanceActionKind.Unresolved
                                  && string.Equals(action.ChunkDigest, chunkDigest, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        actions.Add(new MirrorRebalanceAction(
            MirrorRebalanceActionKind.Unresolved,
            MirrorRebalanceArtefactKind.Chunk,
            node.Id,
            node.Label,
            node.Path,
            chunkDigest,
            0,
            $"Mirror '{node.Label}' is unavailable at its configured path."));
    }

    private static void AddExtraAction(
        List<MirrorRebalanceAction> actions,
        MirrorNodeConfiguration node,
        ManifestChunk chunk,
        string path,
        MirrorRebalanceArtefactKind artefactKind)
    {
        if (!File.Exists(path))
        {
            return;
        }

        actions.Add(new MirrorRebalanceAction(
            MirrorRebalanceActionKind.DeleteFromMirror,
            artefactKind,
            node.Id,
            node.Label,
            path,
            chunk.Digest,
            GetFileLength(path),
            $"Mirror '{node.Label}' has non-target {artefactKind.ToString().ToLowerInvariant()} for chunk {chunk.Digest}."));
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

    private IReadOnlyDictionary<string, long> GetMirrorNodeUsedBytes()
    {
        return mirrorNodes.ToDictionary(
            node => node.Id,
            node => Directory.Exists(node.Path) ? GetRepositorySize(node.Path) : 0L,
            StringComparer.OrdinalIgnoreCase);
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

    private async Task<FileVersionManifest> ReadManifestByVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestPath(rootPath, versionId);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest {versionId} was not found.", manifestPath);
        }

        return await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileVersionManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        await using var manifestStream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<FileVersionManifest>(manifestStream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Manifest {manifestPath} could not be read.");
    }

    private async Task RestoreManifestAsync(
        FileVersionManifest manifest,
        string outputPath,
        bool writeRestoreHint,
        CancellationToken cancellationToken)
    {
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
                var bytes = chunk.Encoding == ChunkEncoding.Raw
                    ? payload
                    : codec.Decompress(payload, chunk.Length, chunk.Encoding);

                await output.WriteAsync(bytes, cancellationToken);
            }

            output.Close();
            File.Move(tempPath, outputPath, overwrite: true);
            if (writeRestoreHint)
            {
                WriteRestoreHint(outputPath, manifest);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task<IReadOnlyList<FileVersionManifest>> ReadRepairableManifestsAsync(
        bool autoRepairFromMirror,
        List<RepositoryScrubIssue> issues,
        CancellationToken cancellationToken)
    {
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
                manifests.Add(await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false));
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
                            var mirrorManifest = await ReadManifestAsync(mirrorManifestPath, cancellationToken)
                                .ConfigureAwait(false);
                            AtomicWriteOverwrite(path, await File.ReadAllBytesAsync(mirrorManifestPath, cancellationToken).ConfigureAwait(false));
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

    private ChunkValidation ValidateChunk(string root, ManifestChunk chunk)
    {
        var chunkPath = ChunkPath(root, chunk.Digest);
        if (!File.Exists(chunkPath))
        {
            return ChunkValidation.Unhealthy(RepositoryScrubIssueKind.MissingChunk, $"Chunk {chunk.Digest} is missing.");
        }

        try
        {
            var metadata = ReadChunkMetadataSafe(root, chunk.Digest);
            if (metadata is null)
            {
                return ChunkValidation.Unhealthy(RepositoryScrubIssueKind.CorruptChunk, $"Chunk {chunk.Digest} metadata is missing or unreadable.");
            }

            if (!string.Equals(metadata.Digest, chunk.Digest, StringComparison.OrdinalIgnoreCase)
                || metadata.LogicalLength != chunk.Length
                || metadata.StoredLength != chunk.StoredLength
                || metadata.Encoding != chunk.Encoding)
            {
                return ChunkValidation.Unhealthy(RepositoryScrubIssueKind.CorruptChunk, $"Chunk {chunk.Digest} metadata does not match the manifest.");
            }

            var payload = File.ReadAllBytes(chunkPath);
            if (payload.Length != chunk.StoredLength)
            {
                return ChunkValidation.Unhealthy(RepositoryScrubIssueKind.CorruptChunk, $"Chunk {chunk.Digest} stored length mismatch.");
            }

            byte[] raw;
            try
            {
                raw = chunk.Encoding == ChunkEncoding.Raw
                    ? payload
                    : codec.Decompress(payload, chunk.Length, chunk.Encoding);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                return ChunkValidation.Unhealthy(RepositoryScrubIssueKind.CorruptChunk, $"Chunk {chunk.Digest} could not be decoded: {exception.Message}");
            }

            if (raw.Length != chunk.Length)
            {
                return ChunkValidation.Unhealthy(RepositoryScrubIssueKind.CorruptChunk, $"Chunk {chunk.Digest} length mismatch.");
            }

            var digest = hasher.Hash(raw);
            if (!string.Equals(digest, chunk.Digest, StringComparison.OrdinalIgnoreCase))
            {
                return ChunkValidation.Unhealthy(RepositoryScrubIssueKind.CorruptChunk, $"Chunk {chunk.Digest} digest mismatch.");
            }

            return ChunkValidation.Healthy;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ChunkValidation.Unhealthy(RepositoryScrubIssueKind.CorruptChunk, $"Chunk {chunk.Digest} could not be read: {exception.Message}");
        }
    }

    private static ChunkMetadata? ReadChunkMetadataSafe(string root, string digest)
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

    private static void RepairChunk(string sourceRoot, string destinationRoot, string digest)
    {
        AtomicWriteOverwrite(ChunkPath(destinationRoot, digest), File.ReadAllBytes(ChunkPath(sourceRoot, digest)));
        var sourceMetadataPath = MetadataPath(sourceRoot, digest);
        if (File.Exists(sourceMetadataPath))
        {
            AtomicWriteOverwrite(MetadataPath(destinationRoot, digest), File.ReadAllBytes(sourceMetadataPath));
        }
    }

    private async Task<RetentionState> BuildRetentionStateAsync(
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var manifests = await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        var summaries = manifests.Select(ToSummary).ToArray();

        var decisions = RetentionPlanner.Decide(summaries, policy, nowUtc).ToArray();
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

    private async Task<IReadOnlyList<FileVersionManifest>> ReadAllManifestsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ManifestsPath(rootPath)))
        {
            return [];
        }

        var manifests = new List<FileVersionManifest>();
        foreach (var path in Directory.EnumerateFiles(ManifestsPath(rootPath), "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifests.Add(await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return manifests;
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

    private LineageResolution ResolveRestoreLineage(
        string sourcePath,
        RestoreLineageHint hint,
        IReadOnlyList<FileVersionManifest> existingManifests)
    {
        var samePathParent = LatestForPath(existingManifests, sourcePath);
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
        var samePathParent = LatestForPath(existingManifests, sourcePath);
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
            .Where(manifest => string.Equals(GetContentSignature(manifest), contentSignature, StringComparison.Ordinal))
            .OrderByDescending(manifest => manifest.CapturedAtUtc)
            .ThenByDescending(manifest => manifest.VersionId, StringComparer.Ordinal)
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

    private static FileVersionManifest? LatestForPath(IReadOnlyList<FileVersionManifest> manifests, string sourcePath)
    {
        return manifests
            .Where(manifest => PathEquals(manifest.SourcePath, sourcePath))
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
            GetContentSignature(manifest));
    }

    private string GetContentSignature(FileVersionManifest manifest)
    {
        return manifest.ContentSignature ?? ComputeContentSignature(manifest.LogicalLength, manifest.Chunks);
    }

    private string ComputeContentSignature(long logicalLength, IReadOnlyList<ManifestChunk> chunks)
    {
        var builder = new StringBuilder();
        builder.Append("fv-content-v1:").Append(logicalLength);
        foreach (var chunk in chunks.OrderBy(chunk => chunk.Offset))
        {
            builder
                .Append('|')
                .Append(chunk.Offset)
                .Append(':')
                .Append(chunk.Digest)
                .Append(':')
                .Append(chunk.Length);
        }

        return hasher.Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void AtomicWriteOverwrite(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Path has no directory: {path}");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string ChunksPath(string root) => Path.Combine(root, "chunks");

    private static string ManifestsPath(string root) => Path.Combine(root, "manifests");

    private static string LineagePath(string root) => Path.Combine(root, "lineage");

    private static string RestoreHintsPath(string root) => Path.Combine(LineagePath(root), "restore-hints");

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
