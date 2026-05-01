namespace FluxVault.Abstractions.Sync;

public enum SyncMappingStatus
{
    PendingConfirmation = 0,
    Confirmed = 1,
    Blocked = 2
}

public sealed record SyncMappingRecord(
    string MappingId,
    string SourceDeviceId,
    string SourcePath,
    string LocalDeviceId,
    string? LocalPath,
    SyncMappingStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ConfirmedAtUtc = null,
    string? ConfirmedByDeviceId = null,
    string? Notes = null);

public static class SyncMappingGate
{
    public static bool CanHydrate(SyncMappingRecord? mapping)
    {
        return mapping?.Status == SyncMappingStatus.Confirmed
               && !string.IsNullOrWhiteSpace(mapping.LocalPath);
    }
}
