namespace FluxVault.Abstractions.Ipc;

public enum FluxVaultIpcCommand
{
    GetStatus = 0,
    SaveConfiguration = 1,
    RunBackupNow = 2,
    ListVersions = 3,
    InspectVersion = 4,
    RestoreVersion = 5,
    ExportDiagnostics = 6,
    PreviewRetention = 7,
    RunRetentionNow = 8
}
