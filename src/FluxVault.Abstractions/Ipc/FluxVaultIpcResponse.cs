using FluxVault.Abstractions.Storage;

namespace FluxVault.Abstractions.Ipc;

public sealed record FluxVaultIpcResponse(
    bool Success,
    string? ErrorMessage,
    FluxVaultServiceStatus? Status,
    BackupRunSummary? Backup,
    IReadOnlyList<RepositoryVersionSummary>? Versions,
    RepositoryInspection? Inspection,
    string? OutputPath,
    RepositoryRetentionPreview? RetentionPreview = null,
    RepositoryRetentionResult? RetentionResult = null,
    IReadOnlyList<FluxVaultActivityEvent>? ActivityEvents = null,
    IReadOnlyList<CaptureRuntimeStatus>? BlockedFiles = null,
    RepositoryHealthSnapshot? RepositoryHealth = null,
    RepositoryScrubReport? RepositoryScrub = null,
    RestoreRehearsalReport? RestoreRehearsal = null,
    MirrorRepairReport? MirrorRepair = null,
    MirrorRebalancePreviewReport? MirrorRebalance = null,
    RestoreSelectionSummary? RestoreSelection = null,
    RepositoryPurgeResult? Purge = null,
    PerformanceTelemetryStatus? Performance = null)
{
    public static FluxVaultIpcResponse Ok()
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, null, null);
    }

    public static FluxVaultIpcResponse WithStatus(FluxVaultServiceStatus status)
    {
        return new FluxVaultIpcResponse(true, null, status, null, null, null, null);
    }

    public static FluxVaultIpcResponse WithBackup(BackupRunSummary backup)
    {
        return new FluxVaultIpcResponse(true, null, null, backup, null, null, null);
    }

    public static FluxVaultIpcResponse WithVersions(IReadOnlyList<RepositoryVersionSummary> versions)
    {
        return new FluxVaultIpcResponse(true, null, null, null, versions, null, null);
    }

    public static FluxVaultIpcResponse WithInspection(RepositoryInspection inspection)
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, inspection, null);
    }

    public static FluxVaultIpcResponse WithOutputPath(string outputPath)
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, null, outputPath);
    }

    public static FluxVaultIpcResponse WithRetentionPreview(RepositoryRetentionPreview preview)
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, null, null, preview);
    }

    public static FluxVaultIpcResponse WithRetentionResult(RepositoryRetentionResult result)
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, null, null, null, result);
    }

    public static FluxVaultIpcResponse WithActivity(IReadOnlyList<FluxVaultActivityEvent> events)
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, null, null, null, null, events);
    }

    public static FluxVaultIpcResponse WithBlockedFiles(IReadOnlyList<CaptureRuntimeStatus> blockedFiles)
    {
        return new FluxVaultIpcResponse(true, null, null, null, null, null, null, null, null, null, blockedFiles);
    }

    public static FluxVaultIpcResponse WithRepositoryHealth(RepositoryHealthSnapshot health)
    {
        return new FluxVaultIpcResponse(
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            RepositoryHealth: health);
    }

    public static FluxVaultIpcResponse WithRepositoryScrub(RepositoryScrubReport report)
    {
        return new FluxVaultIpcResponse(
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            RepositoryScrub: report);
    }

    public static FluxVaultIpcResponse WithRestoreRehearsal(RestoreRehearsalReport report)
    {
        return new FluxVaultIpcResponse(
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            RestoreRehearsal: report);
    }

    public static FluxVaultIpcResponse WithMirrorRepair(MirrorRepairReport report)
    {
        return new FluxVaultIpcResponse(
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            MirrorRepair: report);
    }

    public static FluxVaultIpcResponse WithMirrorRebalance(MirrorRebalancePreviewReport report)
    {
        return new FluxVaultIpcResponse(
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            MirrorRebalance: report);
    }

    public static FluxVaultIpcResponse WithRestoreSelection(RestoreSelectionSummary summary)
    {
        return new FluxVaultIpcResponse(
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            RestoreSelection: summary);
    }

    public static FluxVaultIpcResponse WithPurge(RepositoryPurgeResult? purge)
    {
        return new FluxVaultIpcResponse(
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            Purge: purge);
    }

    public static FluxVaultIpcResponse WithPerformance(PerformanceTelemetryStatus performance)
    {
        return new FluxVaultIpcResponse(
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            Performance: performance);
    }

    public static FluxVaultIpcResponse Failure(string errorMessage)
    {
        return new FluxVaultIpcResponse(false, errorMessage, null, null, null, null, null);
    }
}
