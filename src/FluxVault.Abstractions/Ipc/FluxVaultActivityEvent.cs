namespace FluxVault.Abstractions.Ipc;

public enum FluxVaultActivityKind
{
    Info = 0,
    Pending = 1,
    Capturing = 2,
    Captured = 3,
    Blocked = 4,
    Failed = 5,
    Retention = 6
}

public sealed record FluxVaultActivityEvent(
    DateTimeOffset TimestampUtc,
    FluxVaultActivityKind Kind,
    string Title,
    string Detail,
    string? SourcePath = null);
