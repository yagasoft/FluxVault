using System.Text.Json;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Storage.Integrity;

internal sealed class RepositoryObjectPublisher(VerifiedChunkReader reader, RepositoryIntegrityLimits limits, RepositoryFaults? faults)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal Task PublishNewAsync(string root, ChunkDescriptor descriptor, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        PublishAsync(root, descriptor, payload, repair: false, cancellationToken);

    internal async Task RepairAsync(string donorRoot, string targetRoot, ChunkDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (StorageOwnership.Canonical(donorRoot).Equals(StorageOwnership.Canonical(targetRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Repair requires a separate donor.");
        var stored = await reader.ReadStoredAsync(donorRoot, descriptor, cancellationToken).ConfigureAwait(false);
        reader.DecodeAndVerify(stored, descriptor);
        // Copy exactly the buffer that was verified; never reread an unchecked donor.
        await PublishAsync(targetRoot, descriptor, stored, repair: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishAsync(string root, ChunkDescriptor descriptor, ReadOnlyMemory<byte> payload, bool repair, CancellationToken cancellationToken)
    {
        reader.DecodeAndVerify(payload.Span, descriptor);
        var payloadPath = VerifiedChunkReader.PayloadPath(root, descriptor.Digest);
        var metadataPath = VerifiedChunkReader.MetadataPath(root, descriptor.Digest);
        StorageOwnership.RejectReparseComponents(payloadPath);
        StorageOwnership.RejectReparseComponents(metadataPath);
        if (!repair && (File.Exists(payloadPath) || File.Exists(metadataPath)))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Incomplete or competing chunk publication requires repair.");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        var suffix = $".{Guid.NewGuid():N}.tmp";
        var stagedPayload = payloadPath + suffix;
        var stagedMetadata = metadataPath + suffix;
        try
        {
            var metadata = new ChunkMetadata(descriptor.Digest, descriptor.LogicalLength, descriptor.StoredLength, descriptor.Encoding);
            var json = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
            if (json.Length > limits.MaxSidecarBytes)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "Chunk sidecar exceeds its size limit.");
            await WriteFlushedAsync(stagedPayload, payload, cancellationToken).ConfigureAwait(false);
            await WriteFlushedAsync(stagedMetadata, json, cancellationToken).ConfigureAwait(false);
            var staged = await VerifiedChunkReader.ReadBoundedAsync(stagedPayload, limits.MaxStoredChunkBytes, cancellationToken).ConfigureAwait(false);
            reader.DecodeAndVerify(staged, descriptor);
            var stagedJson = await VerifiedChunkReader.ReadBoundedAsync(stagedMetadata, limits.MaxSidecarBytes, cancellationToken).ConfigureAwait(false);
            if (!stagedJson.AsSpan().SequenceEqual(json))
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, "Staged sidecar verification failed.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedPayload, payloadPath, overwrite: repair);
            faults?.Hit(RepositoryFaultPoint.ObjectPayloadPublished, payloadPath);
            File.Move(stagedMetadata, metadataPath, overwrite: repair);
            await reader.ReadAsync(root, descriptor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(stagedPayload)) File.Delete(stagedPayload);
            if (File.Exists(stagedMetadata)) File.Delete(stagedMetadata);
        }
    }

    internal static async Task WriteFlushedAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
}
