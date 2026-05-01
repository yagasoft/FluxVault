using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Sync;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Sync;

public sealed class FileSyncHydrator(
    string localRepositoryPath,
    FileSystemChunkRepository localRepository)
{
    private readonly FileSyncApplicationStore applicationStore = new(localRepositoryPath);
    private readonly FileSyncConflictStore conflictStore = new(localRepositoryPath);
    private readonly FileSyncHydrationStore hydrationStore = new(localRepositoryPath);
    private readonly ZstdChunkCodec codec = new();

    public async Task<SyncHydrationRecord> ApplyRemoteVersionAsync(
        string remoteRepositoryPath,
        FileVersionManifest remoteManifest,
        string targetPath,
        SyncOriginMetadata origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteManifest);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteRepositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        await CopyMissingChunksAsync(remoteRepositoryPath, remoteManifest, cancellationToken).ConfigureAwait(false);

        if (File.Exists(targetPath) && !CanOpenForExclusiveWrite(targetPath, out var lockedMessage))
        {
            return await RecordHydrationAsync(remoteManifest, targetPath, SyncHydrationState.Blocked, lockedMessage, origin, null, cancellationToken).ConfigureAwait(false);
        }

        var remoteBytes = await BuildPayloadAsync(remoteManifest, cancellationToken).ConfigureAwait(false);
        if (File.Exists(targetPath))
        {
            var localBytes = await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (!localBytes.AsSpan().SequenceEqual(remoteBytes))
            {
                var conflict = await conflictStore.RecordConflictAsync(new SyncConflictRecord(
                    ConflictId: $"conflict-{Guid.CreateVersion7():N}",
                    LocalPath: targetPath,
                    SourceDeviceId: origin.SourceDeviceId,
                    SourceVersionId: origin.SourceVersionId,
                    SourceOperationId: origin.SourceOperationId,
                    DetectedAtUtc: DateTimeOffset.UtcNow,
                    Status: SyncConflictStatus.Open,
                    AvailableActions:
                    [
                        SyncConflictAction.KeepLocal,
                        SyncConflictAction.KeepRemote,
                        SyncConflictAction.RestoreRemoteAsCopy,
                        SyncConflictAction.MarkResolved
                    ]), cancellationToken).ConfigureAwait(false);
                return await RecordHydrationAsync(remoteManifest, targetPath, SyncHydrationState.Conflict, "Target has local changes.", origin, conflict.ConflictId, cancellationToken).ConfigureAwait(false);
            }
        }

        await AtomicWriteTargetAsync(targetPath, remoteBytes, cancellationToken).ConfigureAwait(false);
        var commit = await localRepository.CommitAsync(new FileCommitRequest(
            remoteManifest.WatchedFolderId,
            targetPath,
            DateTimeOffset.UtcNow,
            remoteManifest.Consistency,
            CompressionPreference.Zstd,
            MinimumCompressionBytes: 128,
            new MemoryStream(remoteBytes),
            origin), cancellationToken).ConfigureAwait(false);
        await applicationStore.RecordAppliedVersionAsync(new SyncAppliedVersionRecord(
            origin.SourceDeviceId,
            origin.SourceOperationId,
            origin.SourceVersionId,
            commit.Manifest.VersionId,
            targetPath,
            commit.Manifest.ContentSignature ?? string.Empty,
            DateTimeOffset.UtcNow,
            origin.MappingId), cancellationToken).ConfigureAwait(false);
        return await RecordHydrationAsync(remoteManifest, targetPath, SyncHydrationState.Applied, "Remote version applied.", origin, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyMissingChunksAsync(
        string remoteRepositoryPath,
        FileVersionManifest remoteManifest,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in remoteManifest.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationChunkPath = ChunkPath(localRepositoryPath, chunk.Digest);
            var destinationMetadataPath = MetadataPath(localRepositoryPath, chunk.Digest);
            if (!File.Exists(destinationChunkPath))
            {
                AtomicWrite(destinationChunkPath, await File.ReadAllBytesAsync(ChunkPath(remoteRepositoryPath, chunk.Digest), cancellationToken).ConfigureAwait(false), overwrite: true);
            }

            if (!File.Exists(destinationMetadataPath))
            {
                AtomicWrite(destinationMetadataPath, await File.ReadAllBytesAsync(MetadataPath(remoteRepositoryPath, chunk.Digest), cancellationToken).ConfigureAwait(false), overwrite: true);
            }
        }
    }

    private async Task<byte[]> BuildPayloadAsync(FileVersionManifest manifest, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        foreach (var chunk in manifest.Chunks.OrderBy(chunk => chunk.Offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await File.ReadAllBytesAsync(ChunkPath(localRepositoryPath, chunk.Digest), cancellationToken).ConfigureAwait(false);
            var bytes = chunk.Encoding == ChunkEncoding.Raw
                ? payload
                : codec.Decompress(payload, chunk.Length, chunk.Encoding);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private async Task<SyncHydrationRecord> RecordHydrationAsync(
        FileVersionManifest manifest,
        string targetPath,
        SyncHydrationState state,
        string message,
        SyncOriginMetadata origin,
        string? conflictId,
        CancellationToken cancellationToken)
    {
        var record = new SyncHydrationRecord(
            HydrationId: $"hydration-{Guid.CreateVersion7():N}",
            SourceDeviceId: origin.SourceDeviceId,
            SourceOperationId: origin.SourceOperationId,
            SourceVersionId: manifest.VersionId,
            LocalPath: targetPath,
            State: state,
            Message: message,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            ConflictId: conflictId);
        await hydrationStore.RecordHydrationAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    private static bool CanOpenForExclusiveWrite(string targetPath, out string message)
    {
        try
        {
            using var stream = File.Open(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            message = string.Empty;
            return true;
        }
        catch (IOException ex)
        {
            message = $"Target is locked: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            message = $"Target is unavailable: {ex.Message}";
            return false;
        }
    }

    private static async Task AtomicWriteTargetAsync(string targetPath, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite);
    }

    private static string ChunksPath(string root) => Path.Combine(root, "chunks");

    private static string ChunkPath(string root, string digest) => Path.Combine(ChunksPath(root), digest[..2], $"{digest}.chunk");

    private static string MetadataPath(string root, string digest) => Path.Combine(ChunksPath(root), digest[..2], $"{digest}.json");
}
