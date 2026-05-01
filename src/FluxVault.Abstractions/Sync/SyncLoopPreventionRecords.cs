using FluxVault.Abstractions.Storage;

namespace FluxVault.Abstractions.Sync;

public sealed record SyncOriginMetadata(
    string SourceDeviceId,
    string SourceOperationId,
    string SourceVersionId,
    DateTimeOffset AppliedAtUtc,
    string? MappingId = null);

public sealed record SyncAppliedVersionRecord(
    string SourceDeviceId,
    string SourceOperationId,
    string SourceVersionId,
    string LocalVersionId,
    string LocalPath,
    string ContentSignature,
    DateTimeOffset AppliedAtUtc,
    string? MappingId = null);

public static class SyncPublishGate
{
    public static bool ShouldPublishLocalChange(FileVersionManifest manifest, string localDeviceId)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return manifest.SyncOrigin is null
               || string.Equals(manifest.SyncOrigin.SourceDeviceId, localDeviceId, StringComparison.OrdinalIgnoreCase);
    }
}
