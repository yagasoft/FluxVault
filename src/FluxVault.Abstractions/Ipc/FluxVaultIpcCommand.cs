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
    RunRetentionNow = 8,
    GetActivity = 9,
    ListBlockedFiles = 10,
    SetProtectionPaused = 11,
    PreviewMirrorRebalance = 12,
    RunMirrorRebalance = 13,
    ResolveConflict = 14,
    GetSyncStatus = 15,
    GetRepositoryHealth = 16,
    RunRepositoryScrub = 17,
    RunRestoreRehearsal = 18,
    PreviewMirrorRepair = 19,
    RunMirrorRepair = 20,
    PreviewMirrorDrain = 21,
    RunMirrorDrain = 22,
    RestoreVersionPreview = 23,
    PreviewRestoreSelection = 24,
    RunRestoreSelection = 25,
    // Wire values 26-30 and 32 are retired and must remain unassigned.
    GetPerformance = 31,
    GetOperationStatus = 33,
    SetVaultAccess = 34,
    ListHistoryPage = 35,
    GetSnapshotPage = 36,
    ListCurrentEntriesPage = 37
}
