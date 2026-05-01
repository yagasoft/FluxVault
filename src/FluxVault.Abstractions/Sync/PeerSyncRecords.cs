namespace FluxVault.Abstractions.Sync;

public enum PeerOperationKind
{
    VersionCommitted = 0,
    MappingProposed = 1,
    MappingConfirmed = 2,
    RemoteVersionApplied = 3
}

public sealed record PeerOperationDraft(
    PeerOperationKind Kind,
    string? VersionId = null,
    string? SourcePath = null,
    string? ContentSignature = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? OperationId = null);

public sealed record PeerOperationRecord(
    string OperationId,
    string DeviceId,
    string DeviceDisplayName,
    long SequenceNumber,
    DateTimeOffset CreatedAtUtc,
    PeerOperationKind Kind,
    string? VersionId = null,
    string? SourcePath = null,
    string? ContentSignature = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record PeerHeadRecord(
    string DeviceId,
    string DeviceDisplayName,
    long HeadSequenceNumber,
    string HeadOperationId,
    DateTimeOffset UpdatedAtUtc);

public sealed record PeerCursorRecord(
    string PeerDeviceId,
    long LastSeenSequenceNumber,
    string LastSeenOperationId,
    DateTimeOffset UpdatedAtUtc);
