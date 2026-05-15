using System.IO.Enumeration;
using System.Text.Json;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Cloud;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Ipc;
using FluxVault.Core.Policies;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Core.Sync;

namespace FluxVault.Core.Service;

public sealed class FluxVaultOperations(
    IFluxVaultConfigurationStore configurationStore,
    IFileCaptureProvider captureProvider,
    IRepositoryMaintenanceStateStore? repositoryMaintenanceStateStore = null,
    string? maintenanceStateRoot = null,
    Func<FluxVaultConfiguration, IRepositoryMetadataStore>? metadataStoreFactory = null,
    Func<FluxVaultConfiguration, IChunkRepository>? repositoryFactory = null) : IFluxVaultRequestHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IRepositoryMaintenanceStateStore repositoryMaintenanceStateStore =
        repositoryMaintenanceStateStore ?? new InMemoryRepositoryMaintenanceStateStore();
    private readonly Func<FluxVaultConfiguration, IRepositoryMetadataStore> metadataStoreFactory =
        metadataStoreFactory ?? CreateMetadataStore;
    private readonly Func<FluxVaultConfiguration, IChunkRepository> repositoryFactory =
        repositoryFactory ?? (configuration => CreateRepository(configuration, (metadataStoreFactory ?? CreateMetadataStore)(configuration)));
    private readonly string restoreRehearsalRoot = Path.Combine(
        maintenanceStateRoot ?? Path.Combine(Path.GetTempPath(), "FluxVault"),
        "restore-rehearsal");
    private readonly string versionPreviewRoot = Path.Combine(
        maintenanceStateRoot ?? Path.Combine(Path.GetTempPath(), "FluxVault"),
        "version-preview");
    private DateTimeOffset? lastCaptureUtc;
    private string lastMessage = "Ready";
    private IReadOnlyList<string> lastMirrorWarnings = [];
    private DurableChangeRuntimeStatus? durableChange;
    private RepositoryRetentionResult? lastRetention;
    private readonly Lock runtimeGate = new();
    private readonly Dictionary<string, CaptureRuntimeStatus> captureStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WatcherRuntimeStatus> watcherStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim mutatingOperationGate = new(1, 1);
    private readonly SemaphoreSlim backupOperationGate = new(1, 1);
    private readonly TimeSpan recentVersionStatusCacheDuration = TimeSpan.FromSeconds(15);
    private IReadOnlyList<RepositoryVersionSummary>? recentVersionStatusCache;
    private DateTimeOffset recentVersionStatusCacheUtc;
    private IReadOnlyList<RepositoryVersionSummary>? trackedEntryStatusCache;
    private DateTimeOffset trackedEntryStatusCacheUtc;
    private BackupRuntimeStatus backupRuntime = new(
        IsRunning: false,
        Phase: "Idle",
        Trigger: null,
        StartedAtUtc: null,
        CompletedAtUtc: null,
        EnumeratedFileCount: 0,
        CapturedFileCount: 0,
        SkippedUnchangedFileCount: 0,
        FailedFileCount: 0,
        RecordedDeletionCount: 0,
        ActiveWorkers: 0,
        EffectiveWorkerCount: 0);

    public async Task SaveConfigurationAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await configurationStore.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        InvalidateRecentVersionStatusCache();
        lastMirrorWarnings = [];
        lastMessage = "Configuration saved.";
    }

    public async Task<BackupRunSummary> RunBackupNowAsync(CancellationToken cancellationToken = default)
    {
        await backupOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = DateTimeOffset.UtcNow;
        var effectiveWorkers = 0;
        BeginBackup("Reconciliation scan", "Starting", started, effectiveWorkers);
        try
        {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!configuration.IsEnabled)
        {
            lastMirrorWarnings = [];
            return CompleteBackup(true, "Protection is disabled.", 0, 0, 0, 0, 0, started);
        }

        var repository = CreateRepository(configuration);
        var failed = 0;
        var messages = new List<string>();
        effectiveWorkers = GetEffectiveMaximumConcurrentCaptures(configuration.CaptureCadencePolicy.MaximumConcurrentCaptures);
        BeginBackup("Reconciliation scan", "Enumerating", started, effectiveWorkers);
        var latestEntries = await GetTrackedEntriesForBackupAsync(repository, cancellationToken).ConfigureAwait(false);
        var latestByPath = BuildLatestFileLookup(latestEntries);
        var (captured, captureFailed, enumerated, skipped, captureMessages, mirrorWarnings) = await CaptureTargetsAsync(
                repository,
                EnumerateBackupTargets(configuration, messages, () => failed++),
                effectiveWorkers,
                latestByPath,
                configuration.CaptureCadencePolicy.SourceDeepVerificationInterval,
                allowUnchangedSkip: true,
                cancellationToken)
            .ConfigureAwait(false);
        failed += captureFailed;
        messages.AddRange(captureMessages);
        var deletionSummary = await RecordMissingTrackedEntriesAsync(repository, configuration, latestEntries, cancellationToken)
            .ConfigureAwait(false);
        failed += deletionSummary.Failed;
        messages.AddRange(deletionSummary.Messages);
        mirrorWarnings = mirrorWarnings.Concat(deletionSummary.MirrorWarnings).ToArray();
        var success = failed == 0;
        var message = messages.Count == 0
            ? FormatBackupCaptureMessage(captured, deletionSummary.Recorded)
            : string.Join(" ", messages);
        var distinctMirrorWarnings = mirrorWarnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinctMirrorWarnings.Length > 0)
        {
            message = $"{message} Mirror warning(s): {distinctMirrorWarnings.Length} mirror write issue(s).";
        }

        lastMirrorWarnings = distinctMirrorWarnings;
        if (success && (captured > 0 || deletionSummary.Recorded > 0))
        {
            message = await ApplyRetentionAfterSuccessfulBackupAsync(repository, configuration, message, cancellationToken)
                .ConfigureAwait(false);
        }

        return CompleteBackup(success, message, captured, failed, enumerated, skipped, deletionSummary.Recorded, started);
        }
        finally
        {
            backupOperationGate.Release();
        }
    }

    public async Task<BackupRunSummary> RunBackupForFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        await backupOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = DateTimeOffset.UtcNow;
        var effectiveWorkers = 0;
        BeginBackup("Targeted backup", "Starting", started, effectiveWorkers);
        try
        {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!configuration.IsEnabled)
        {
            lastMirrorWarnings = [];
            return CompleteBackup(true, "Protection is disabled.", 0, 0, 0, 0, 0, started);
        }

        var requestedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var repository = CreateRepository(configuration);
        effectiveWorkers = GetEffectiveMaximumConcurrentCaptures(configuration.CaptureCadencePolicy.MaximumConcurrentCaptures);
        BeginBackup("Targeted backup", "Enumerating", started, effectiveWorkers);
        var latestEntries = await GetTrackedEntriesForBackupAsync(repository, cancellationToken).ConfigureAwait(false);
        var latestByPath = BuildLatestFileLookup(latestEntries);
        var (captured, failed, enumerated, skipped, messages, mirrorWarnings) = await CaptureTargetsAsync(
                repository,
                EnumerateBackupTargets(configuration, requestedPaths, cancellationToken),
                effectiveWorkers,
                latestByPath,
                configuration.CaptureCadencePolicy.SourceDeepVerificationInterval,
                allowUnchangedSkip: true,
                cancellationToken)
            .ConfigureAwait(false);
        var deletionSummary = await RecordTargetedDeletionsAsync(repository, configuration, requestedPaths, latestEntries, cancellationToken)
            .ConfigureAwait(false);
        failed += deletionSummary.Failed;
        messages = messages.Concat(deletionSummary.Messages).ToArray();
        mirrorWarnings = mirrorWarnings.Concat(deletionSummary.MirrorWarnings).ToArray();
        var success = failed == 0;
        var message = messages.Count == 0
            ? FormatBackupCaptureMessage(captured, deletionSummary.Recorded, "changed file")
            : string.Join(" ", messages);
        var distinctMirrorWarnings = mirrorWarnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinctMirrorWarnings.Length > 0)
        {
            message = $"{message} Mirror warning(s): {distinctMirrorWarnings.Length} mirror write issue(s).";
        }

        lastMirrorWarnings = distinctMirrorWarnings;
        if (success && (captured > 0 || deletionSummary.Recorded > 0))
        {
            message = await ApplyRetentionAfterSuccessfulBackupAsync(repository, configuration, message, cancellationToken)
                .ConfigureAwait(false);
        }

        return CompleteBackup(success, message, captured, failed, enumerated, skipped, deletionSummary.Recorded, started);
        }
        finally
        {
            backupOperationGate.Release();
        }
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return (await CreateRepository(configuration).ListVersionsAsync(cancellationToken).ConfigureAwait(false))
            .Where(version => version.EntryKind == RepositoryEntryKind.File)
            .ToArray();
    }

    private async Task<IReadOnlyList<RepositoryVersionSummary>> ListRepositoryHistoryAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return await CreateRepository(configuration).ListVersionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RepositoryInspection> InspectVersionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return await CreateRepository(configuration).InspectAsync(versionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreVersionAsync(string versionId, string outputPath, CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await CreateRepository(configuration).RestoreAsync(versionId, outputPath, cancellationToken).ConfigureAwait(false);
        InvalidateRecentVersionStatusCache();
        lastMessage = $"Restored {versionId} to {outputPath}.";
    }

    public async Task<RestoreSelectionSummary> PreviewRestoreSelectionAsync(
        string sourcePath,
        bool isDirectory,
        RestoreSelectionDestinationMode destinationMode,
        string? destinationPath,
        CancellationToken cancellationToken = default)
    {
        return await RestoreSelectionAsync(
                sourcePath,
                isDirectory,
                destinationMode,
                destinationPath,
                overwriteConfirmed: false,
                execute: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RestoreSelectionSummary> RunRestoreSelectionAsync(
        string sourcePath,
        bool isDirectory,
        RestoreSelectionDestinationMode destinationMode,
        string? destinationPath,
        bool overwriteConfirmed,
        CancellationToken cancellationToken = default)
    {
        return await RestoreSelectionAsync(
                sourcePath,
                isDirectory,
                destinationMode,
                destinationPath,
                overwriteConfirmed,
                execute: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> RestoreVersionPreviewAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var repository = CreateRepository(configuration);
        var inspection = await repository.InspectAsync(versionId, cancellationToken).ConfigureAwait(false);
        var outputPath = BuildVersionPreviewPath(inspection.Manifest);
        CleanOldVersionPreviews(DateTimeOffset.UtcNow.AddDays(-2));
        await repository.RestorePreviewAsync(versionId, outputPath, cancellationToken).ConfigureAwait(false);
        lastMessage = $"Prepared preview for {versionId}.";
        return outputPath;
    }

    private async Task<RestoreSelectionSummary> RestoreSelectionAsync(
        string sourcePath,
        bool isDirectory,
        RestoreSelectionDestinationMode destinationMode,
        string? destinationPath,
        bool overwriteConfirmed,
        bool execute,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationRootOrFile = ResolveRestoreSelectionDestination(
            sourceFullPath,
            isDirectory,
            destinationMode,
            destinationPath);
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var repository = CreateRepository(configuration);
        var versions = await repository.ListVersionsAsync(cancellationToken).ConfigureAwait(false);
        var selectedEntryKind = isDirectory ? RepositoryEntryKind.Folder : RepositoryEntryKind.File;
        var exactVersion = versions
            .Where(version => version.EntryKind == selectedEntryKind)
            .Where(version => string.Equals(TrimPath(version.SourcePath), TrimPath(sourceFullPath), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(version => version.CapturedAtUtc)
            .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
            .FirstOrDefault();
        var selections = exactVersion is not null
            ? new[]
            {
                new RestoreSelectionItem(
                    exactVersion.VersionId,
                    Path.GetFullPath(exactVersion.SourcePath),
                    destinationRootOrFile,
                    exactVersion.EntryKind,
                    CountRestorableFiles(exactVersion, versions),
                    CountRestoreConflicts(exactVersion, versions, destinationRootOrFile))
            }
            : versions
                .Where(version => version.EntryKind == RepositoryEntryKind.File && !version.IsDeleted)
                .Where(version => SourcePathMatchesSelection(version.SourcePath, sourceFullPath, isDirectory))
                .GroupBy(version => Path.GetFullPath(version.SourcePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(version => version.CapturedAtUtc)
                    .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
                    .First())
                .Select(version => new RestoreSelectionItem(
                    version.VersionId,
                    Path.GetFullPath(version.SourcePath),
                    MapRestoreSelectionDestination(
                        sourceFullPath,
                        version.SourcePath,
                        isDirectory,
                        destinationMode,
                        destinationRootOrFile),
                    version.EntryKind,
                    FileCount: 1,
                    ConflictCount: File.Exists(MapRestoreSelectionDestination(
                        sourceFullPath,
                        version.SourcePath,
                        isDirectory,
                        destinationMode,
                        destinationRootOrFile)) ? 1 : 0))
                .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var fileCount = selections.Sum(item => item.FileCount);
        var conflicts = selections.Sum(item => item.ConflictCount);
        if (execute && conflicts > 0 && !overwriteConfirmed)
        {
            throw new InvalidOperationException("Restore destination already contains file(s). Confirm overwrite before running restore.");
        }

        var restored = 0;
        var failedPaths = new List<string>();
        if (execute)
        {
            foreach (var selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var destinationFolder = Path.GetDirectoryName(selection.DestinationPath);
                    if (!string.IsNullOrWhiteSpace(destinationFolder))
                    {
                        Directory.CreateDirectory(destinationFolder);
                    }

                    await repository.RestoreAsync(selection.VersionId, selection.DestinationPath, cancellationToken)
                        .ConfigureAwait(false);
                    restored += selection.FileCount;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    failedPaths.Add($"{selection.SourcePath}: {ex.Message}");
                }
            }

            InvalidateRecentVersionStatusCache();
            lastMessage = $"Restored {restored} of {fileCount} latest item(s) for {sourceFullPath}.";
        }

        return new RestoreSelectionSummary(
            sourceFullPath,
            isDirectory,
            destinationMode,
            destinationRootOrFile,
            fileCount,
            conflicts,
            restored,
            failedPaths);
    }

    private static string ResolveRestoreSelectionDestination(
        string sourceFullPath,
        bool isDirectory,
        RestoreSelectionDestinationMode destinationMode,
        string? destinationPath)
    {
        if (destinationMode == RestoreSelectionDestinationMode.Original)
        {
            return sourceFullPath;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return Path.GetFullPath(destinationPath);
    }

    private static string MapRestoreSelectionDestination(
        string sourceRootOrFile,
        string versionSourcePath,
        bool isDirectory,
        RestoreSelectionDestinationMode destinationMode,
        string destinationRootOrFile)
    {
        if (!isDirectory)
        {
            return destinationMode == RestoreSelectionDestinationMode.Original
                ? Path.GetFullPath(versionSourcePath)
                : destinationRootOrFile;
        }

        var relativePath = Path.GetRelativePath(sourceRootOrFile, Path.GetFullPath(versionSourcePath));
        return Path.GetFullPath(Path.Combine(destinationRootOrFile, relativePath));
    }

    private static bool SourcePathMatchesSelection(string versionSourcePath, string selectedSourcePath, bool isDirectory)
    {
        var versionPath = Path.GetFullPath(versionSourcePath);
        if (string.Equals(TrimPath(versionPath), TrimPath(selectedSourcePath), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return isDirectory && IsUnderPath(versionPath, selectedSourcePath);
    }

    private static bool IsUnderPath(string path, string root)
    {
        var rootWithSeparator = TrimPath(root) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static int CountRestorableFiles(
        RepositoryVersionSummary version,
        IReadOnlyList<RepositoryVersionSummary> versions)
    {
        if (version.EntryKind == RepositoryEntryKind.File)
        {
            return version.IsDeleted && string.IsNullOrWhiteSpace(version.DeletedFromVersionId) ? 0 : 1;
        }

        return (version.FolderEntries ?? [])
            .Where(entry => !entry.IsDeleted)
            .Sum(entry =>
            {
                var child = versions.FirstOrDefault(version =>
                    string.Equals(version.VersionId, entry.VersionId, StringComparison.OrdinalIgnoreCase));
                return child is null ? 0 : CountRestorableFiles(child, versions);
            });
    }

    private static int CountRestoreConflicts(
        RepositoryVersionSummary version,
        IReadOnlyList<RepositoryVersionSummary> versions,
        string destinationPath)
    {
        if (version.EntryKind == RepositoryEntryKind.File)
        {
            return File.Exists(destinationPath) ? 1 : 0;
        }

        return (version.FolderEntries ?? [])
            .Where(entry => !entry.IsDeleted)
            .Sum(entry =>
            {
                var child = versions.FirstOrDefault(version =>
                    string.Equals(version.VersionId, entry.VersionId, StringComparison.OrdinalIgnoreCase));
                return child is null
                    ? 0
                    : CountRestoreConflicts(child, versions, Path.Combine(destinationPath, entry.Name));
            });
    }

    private sealed record RestoreSelectionItem(
        string VersionId,
        string SourcePath,
        string DestinationPath,
        RepositoryEntryKind EntryKind,
        int FileCount,
        int ConflictCount);

    public async Task<RepositoryRetentionPreview> PreviewRetentionAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return await CreateRepository(configuration)
            .PreviewRetentionAsync(configuration.RetentionPolicy, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RepositoryRetentionResult> RunRetentionNowAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = await CreateRepository(configuration)
            .ApplyRetentionAsync(configuration.RetentionPolicy, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        InvalidateRecentVersionStatusCache();
        lastRetention = result;
        lastMessage = FormatRetentionSummary(result);
        return result;
    }

    public async Task<RepositoryHealthSnapshot> GetRepositoryHealthAsync(CancellationToken cancellationToken = default)
    {
        var state = await repositoryMaintenanceStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return state.LastHealth
            ?? BuildRepositoryHealthSnapshot(
                state.LastScrub,
                state.LastRestoreRehearsal,
                state.LastMirrorRepair,
                state.LastMirrorRebalance,
                "Repository health has not run yet.");
    }

    public async Task<RepositoryScrubReport> RunRepositoryScrubAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var policy = configuration.RepositoryMaintenancePolicy ?? RepositoryMaintenancePolicy.CreateDefault();
        var report = await CreateRepository(configuration)
            .ScrubAsync(policy.AutoRepairFromMirror, cancellationToken)
            .ConfigureAwait(false);
        await SaveRepositoryMaintenanceResultAsync(report, null, null, null, cancellationToken).ConfigureAwait(false);
        lastMessage = FormatScrubSummary(report);
        return report;
    }

    public async Task<MirrorRepairReport> PreviewMirrorRepairAsync(
        string? mirrorNodeId = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var report = await CreateRepository(configuration)
            .PreviewMirrorRepairAsync(mirrorNodeId, cancellationToken)
            .ConfigureAwait(false);
        await SaveRepositoryMaintenanceResultAsync(null, null, report, null, cancellationToken).ConfigureAwait(false);
        lastMessage = FormatMirrorRepairSummary(report);
        return report;
    }

    public async Task<MirrorRepairReport> RunMirrorRepairAsync(
        string? mirrorNodeId = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var report = await CreateRepository(configuration)
            .RunMirrorRepairAsync(mirrorNodeId, cancellationToken)
            .ConfigureAwait(false);
        await SaveRepositoryMaintenanceResultAsync(null, null, report, null, cancellationToken).ConfigureAwait(false);
        lastMessage = FormatMirrorRepairSummary(report);
        return report;
    }

    public async Task<RestoreRehearsalReport> RunRestoreRehearsalAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var policy = configuration.RepositoryMaintenancePolicy ?? RepositoryMaintenancePolicy.CreateDefault();
        var report = await CreateRepository(configuration)
            .RunRestoreRehearsalAsync(restoreRehearsalRoot, policy.RestoreRehearsalVersionCount, cancellationToken)
            .ConfigureAwait(false);
        await SaveRepositoryMaintenanceResultAsync(null, report, null, null, cancellationToken).ConfigureAwait(false);
        lastMessage = FormatRestoreRehearsalSummary(report);
        return report;
    }

    public async Task<MirrorRebalancePreviewReport> PreviewMirrorRebalanceAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var report = await CreateRepository(configuration)
            .PreviewMirrorRebalanceAsync(cancellationToken)
            .ConfigureAwait(false);
        await SaveRepositoryMaintenanceResultAsync(null, null, null, report, cancellationToken).ConfigureAwait(false);
        lastMessage = FormatMirrorRebalanceSummary(report);
        return report;
    }

    public async Task<MirrorRebalancePreviewReport> RunMirrorRebalanceAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var report = await CreateRepository(configuration)
            .RunMirrorRebalanceAsync(cancellationToken)
            .ConfigureAwait(false);
        await SaveRepositoryMaintenanceResultAsync(null, null, null, report, cancellationToken).ConfigureAwait(false);
        lastMessage = FormatMirrorRebalanceSummary(report);
        return report;
    }

    public async Task<MirrorRebalancePreviewReport> PreviewMirrorDrainAsync(
        string mirrorNodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorNodeId);
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var report = await CreateRepository(configuration)
            .PreviewMirrorDrainAsync(mirrorNodeId, cancellationToken)
            .ConfigureAwait(false);
        await SaveRepositoryMaintenanceResultAsync(null, null, null, report, cancellationToken).ConfigureAwait(false);
        lastMessage = FormatMirrorRebalanceSummary(report);
        return report;
    }

    public async Task<MirrorRebalancePreviewReport> RunMirrorDrainAsync(
        string mirrorNodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorNodeId);
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var report = await CreateRepository(configuration)
            .RunMirrorDrainAsync(mirrorNodeId, cancellationToken)
            .ConfigureAwait(false);
        if (IsDrainComplete(report))
        {
            await configurationStore.SaveAsync(DisableMirrorNode(configuration, mirrorNodeId), cancellationToken)
                .ConfigureAwait(false);
        }

        await SaveRepositoryMaintenanceResultAsync(null, null, null, report, cancellationToken).ConfigureAwait(false);
        lastMessage = FormatMirrorRebalanceSummary(report);
        return report;
    }

    public async Task<string> ExportDiagnosticsAsync(string exportPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportPath);
        Directory.CreateDirectory(exportPath);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var filePath = Path.Combine(exportPath, $"fluxvault-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        await File.WriteAllBytesAsync(filePath, JsonSerializer.SerializeToUtf8Bytes(status, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        return filePath;
    }

    public IReadOnlyList<FluxVaultActivityEvent> GetActivity()
    {
        var events = GetCaptureStatuses()
            .Select(ToActivityEvent)
            .ToList();
        if (lastRetention is not null)
        {
            events.Add(new FluxVaultActivityEvent(
                DateTimeOffset.UtcNow,
                FluxVaultActivityKind.Retention,
                "Retention",
                FormatRetentionSummary(lastRetention)));
        }

        if (events.Count == 0)
        {
            events.Add(new FluxVaultActivityEvent(DateTimeOffset.UtcNow, FluxVaultActivityKind.Info, "Ready", lastMessage));
        }

        return events
            .OrderByDescending(value => value.TimestampUtc)
            .Take(100)
            .ToArray();
    }

    public IReadOnlyList<CaptureRuntimeStatus> ListBlockedFiles()
    {
        return GetCaptureStatuses()
            .Where(status => status.State == CaptureRuntimeState.Blocked)
            .ToArray();
    }

    public async Task SetProtectionPausedAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var updated = configuration with { IsEnabled = !configuration.IsEnabled };
        await configurationStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        lastMessage = updated.IsEnabled ? "Protection resumed." : "Protection paused.";
    }

    public void UpdateDurableChangeStatus(DurableChangeRuntimeStatus status)
    {
        durableChange = status;
    }

    public void UpdateCaptureRuntimeStatus(
        string sourcePath,
        string watchedFolderId,
        CaptureRuntimeState state,
        DateTimeOffset? lastEventUtc,
        DateTimeOffset? nextForcedCaptureUtc,
        string? delayReason = null,
        string? blockedReason = null,
        CaptureConsistency? consistency = null,
        string? consistencyDetail = null)
    {
        var normalisedPath = Path.GetFullPath(sourcePath);
        lock (runtimeGate)
        {
            captureStatuses.TryGetValue(normalisedPath, out var previous);
            captureStatuses[normalisedPath] = new CaptureRuntimeStatus(
                SourcePath: normalisedPath,
                WatchedFolderId: watchedFolderId,
                State: state,
                LastEventUtc: lastEventUtc ?? previous?.LastEventUtc,
                NextForcedCaptureUtc: nextForcedCaptureUtc,
                LastCaptureAttemptUtc: state is CaptureRuntimeState.Capturing
                    or CaptureRuntimeState.ForcedHotFileSnapshot
                    or CaptureRuntimeState.SkippedUnchanged
                    ? DateTimeOffset.UtcNow
                    : previous?.LastCaptureAttemptUtc,
                DelayReason: delayReason,
                BlockedReason: blockedReason,
                Consistency: consistency ?? previous?.Consistency,
                AttemptCount: state is CaptureRuntimeState.Capturing
                    or CaptureRuntimeState.ForcedHotFileSnapshot
                    or CaptureRuntimeState.SkippedUnchanged
                    ? (previous?.AttemptCount ?? 0) + 1
                    : previous?.AttemptCount ?? 0,
                ConsistencyDetail: consistencyDetail ?? previous?.ConsistencyDetail);
        }
    }

    public void UpdateWatcherRuntimeStatus(
        string watchedFolderId,
        string path,
        int backlogCount,
        double eventsPerMinute,
        DateTimeOffset? lastEventUtc,
        string lastCatchUpSource,
        bool isBacklogOverflowed)
    {
        var normalisedPath = Path.GetFullPath(path);
        lock (runtimeGate)
        {
            watcherStatuses[watchedFolderId] = new WatcherRuntimeStatus(
                watchedFolderId,
                normalisedPath,
                backlogCount,
                eventsPerMinute,
                lastEventUtc,
                lastCatchUpSource,
                isBacklogOverflowed);
        }
    }

    public void RecordSuppressedFullScan(string reason, DateTimeOffset nextFallbackScanUtc)
    {
        lock (runtimeGate)
        {
            backupRuntime = backupRuntime with
            {
                SuppressedFullScanCount = backupRuntime.SuppressedFullScanCount + 1,
                NextFallbackScanUtc = nextFallbackScanUtc,
                LastFullScanReason = reason
            };
        }
    }

    private void BeginBackup(string trigger, string phase, DateTimeOffset startedUtc, int effectiveWorkerCount)
    {
        lock (runtimeGate)
        {
            backupRuntime = backupRuntime with
            {
                IsRunning = true,
                Phase = phase,
                Trigger = trigger,
                StartedAtUtc = startedUtc,
                CompletedAtUtc = null,
                EnumeratedFileCount = 0,
                CapturedFileCount = 0,
                SkippedUnchangedFileCount = 0,
                FailedFileCount = 0,
                RecordedDeletionCount = 0,
                ActiveWorkers = 0,
                EffectiveWorkerCount = effectiveWorkerCount
            };
        }
    }

    private void PublishBackupProgress(
        string phase,
        int captured,
        int failed,
        int enumerated,
        int skipped,
        int effectiveWorkerCount)
    {
        lock (runtimeGate)
        {
            backupRuntime = backupRuntime with
            {
                Phase = phase,
                EnumeratedFileCount = enumerated,
                CapturedFileCount = captured,
                SkippedUnchangedFileCount = skipped,
                FailedFileCount = failed,
                ActiveWorkers = Math.Min(effectiveWorkerCount, Math.Max(0, enumerated - skipped - captured - failed)),
                EffectiveWorkerCount = effectiveWorkerCount
            };
        }
    }

    private BackupRuntimeStatus GetBackupRuntime()
    {
        lock (runtimeGate)
        {
            return backupRuntime;
        }
    }

    private async Task<IReadOnlyList<RepositoryVersionSummary>> GetRecentVersionsForStatusAsync(
        FluxVaultConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(configuration.RepositoryPath))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        lock (runtimeGate)
        {
            if (recentVersionStatusCache is not null
                && now - recentVersionStatusCacheUtc <= recentVersionStatusCacheDuration)
            {
                return recentVersionStatusCache;
            }
        }

        var versions = (await CreateRepository(configuration).ListVersionsAsync(cancellationToken).ConfigureAwait(false))
            .Take(50)
            .ToArray();
        lock (runtimeGate)
        {
            recentVersionStatusCache = versions;
            recentVersionStatusCacheUtc = now;
        }

        return versions;
    }

    private IReadOnlyList<RepositoryVersionSummary> GetCachedRecentVersionsForFastStatus()
    {
        lock (runtimeGate)
        {
            return recentVersionStatusCache ?? [];
        }
    }

    private async Task<IReadOnlyList<RepositoryVersionSummary>> GetTrackedEntriesForStatusAsync(
        FluxVaultConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(configuration.RepositoryPath))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        lock (runtimeGate)
        {
            if (trackedEntryStatusCache is not null
                && now - trackedEntryStatusCacheUtc <= recentVersionStatusCacheDuration)
            {
                return trackedEntryStatusCache;
            }
        }

        var entries = await CreateRepository(configuration).ListLatestEntriesAsync(cancellationToken).ConfigureAwait(false);
        lock (runtimeGate)
        {
            trackedEntryStatusCache = entries;
            trackedEntryStatusCacheUtc = now;
        }

        return entries;
    }

    private void InvalidateRecentVersionStatusCache()
    {
        lock (runtimeGate)
        {
            recentVersionStatusCache = null;
            recentVersionStatusCacheUtc = default;
            trackedEntryStatusCache = null;
            trackedEntryStatusCacheUtc = default;
        }
    }

    private async Task<MetadataStoreRuntimeStatus> GetMetadataStoreStatusAsync(
        FluxVaultConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            return await metadataStoreFactory(configuration)
                .GetRuntimeStatusAsync(configuration.MetadataStore.ExportLagWarningThreshold, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            return new MetadataStoreRuntimeStatus(
                Provider: configuration.MetadataStore.Provider,
                Endpoint: $"{configuration.MetadataStore.Host}:{configuration.MetadataStore.Port}/{configuration.MetadataStore.DatabaseName}",
                SchemaInitialized: false,
                LastError: exception.Message,
                PendingOutboxCount: 0,
                OldestUnexportedUtc: null,
                OldestUnexportedAge: null,
                IsExportLagExceeded: false);
        }
    }

    public Task<FluxVaultServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetStatusAsync(FluxVaultStatusDetailLevel.Full, cancellationToken);
    }

    public async Task<FluxVaultServiceStatus> GetStatusAsync(
        FluxVaultStatusDetailLevel detailLevel,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var isFast = detailLevel == FluxVaultStatusDetailLevel.Fast;
        var versions = isFast
            ? GetCachedRecentVersionsForFastStatus()
            : await GetRecentVersionsForStatusAsync(configuration, cancellationToken).ConfigureAwait(false);
        var trackedEntries = isFast
            ? null
            : await GetTrackedEntriesForStatusAsync(configuration, cancellationToken).ConfigureAwait(false);

        return new FluxVaultServiceStatus(
            IsServiceRunning: true,
            Configuration: configuration,
            LastMessage: lastMessage,
            LastCaptureUtc: lastCaptureUtc,
            WatchedFolders: configuration.WatchedFolders
                .Select(folder => new WatchedFolderRuntimeStatus(
                    folder.Id,
                    folder.Path,
                    Directory.Exists(folder.Path),
                    folder.IsEnabled,
                    Directory.Exists(folder.Path) ? "Ready" : "Folder missing",
                    durableChange?.Status ?? "Using reconciliation scan"))
                .ToArray(),
            RecentVersions: versions.Take(50).ToArray(),
            LastRetention: lastRetention,
            DurableChange: durableChange,
            CaptureStatuses: GetCaptureStatuses(),
            RepositoryHealth: await GetRepositoryHealthAsync(cancellationToken).ConfigureAwait(false),
            MirrorWarnings: lastMirrorWarnings,
            DeviceIdentity: BuildDeviceIdentityStatus(configuration.Sync),
            Sync: await BuildSyncStatusAsync(configuration, cancellationToken).ConfigureAwait(false),
            PerformanceWorkspace: BuildPerformanceWorkspaceStatus(configuration.PerformanceWorkspace),
            ShellIntegration: BuildShellIntegrationStatus(configuration.ShellIntegration),
            DirectCloud: BuildDirectCloudStatus(configuration.DirectCloud),
            SecurityPosture: BuildSecurityPostureStatus(configuration.SecurityPosture),
            Fleet: BuildFleetStatus(configuration.Fleet),
            Watchers: GetWatcherStatuses(),
            TrackedEntries: trackedEntries,
            BackupRuntime: GetBackupRuntime(),
            MetadataStore: isFast
                ? null
                : await GetMetadataStoreStatusAsync(configuration, cancellationToken).ConfigureAwait(false));
    }

    public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
    {
        if (AllowsConcurrentRequest(request.Command))
        {
            return await HandleCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }

        await mutatingOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HandleCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutatingOperationGate.Release();
        }
    }

    private async Task<FluxVaultIpcResponse> HandleCoreAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            FluxVaultIpcCommand.GetStatus => FluxVaultIpcResponse.WithStatus(await GetStatusAsync(request.StatusDetailLevel, cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.SaveConfiguration => await SaveConfigurationResponseAsync(request, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.RunBackupNow => FluxVaultIpcResponse.WithBackup(await RunBackupNowAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.ListVersions => FluxVaultIpcResponse.WithVersions(await ListRepositoryHistoryAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.InspectVersion => FluxVaultIpcResponse.WithInspection(await InspectVersionAsync(Require(request.VersionId, "version id"), cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.RestoreVersion => await RestoreVersionResponseAsync(request, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.RestoreVersionPreview => await RestoreVersionPreviewResponseAsync(request, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.PreviewRestoreSelection => await RestoreSelectionResponseAsync(request, execute: false, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.RunRestoreSelection => await RestoreSelectionResponseAsync(request, execute: true, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.ExportDiagnostics => FluxVaultIpcResponse.WithOutputPath(await ExportDiagnosticsAsync(Require(request.ExportPath, "export path"), cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.PreviewRetention => FluxVaultIpcResponse.WithRetentionPreview(await PreviewRetentionAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.RunRetentionNow => FluxVaultIpcResponse.WithRetentionResult(await RunRetentionNowAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.GetActivity => FluxVaultIpcResponse.WithActivity(GetActivity()),
            FluxVaultIpcCommand.ListBlockedFiles => FluxVaultIpcResponse.WithBlockedFiles(ListBlockedFiles()),
            FluxVaultIpcCommand.SetProtectionPaused => await SetProtectionPausedResponseAsync(cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.GetRepositoryHealth => FluxVaultIpcResponse.WithRepositoryHealth(await GetRepositoryHealthAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.RunRepositoryScrub => FluxVaultIpcResponse.WithRepositoryScrub(await RunRepositoryScrubAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.RunRestoreRehearsal => FluxVaultIpcResponse.WithRestoreRehearsal(await RunRestoreRehearsalAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.PreviewMirrorRebalance => FluxVaultIpcResponse.WithMirrorRebalance(await PreviewMirrorRebalanceAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.RunMirrorRebalance => FluxVaultIpcResponse.WithMirrorRebalance(await RunMirrorRebalanceAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.PreviewMirrorRepair => FluxVaultIpcResponse.WithMirrorRepair(await PreviewMirrorRepairAsync(request.MirrorNodeId, cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.RunMirrorRepair => FluxVaultIpcResponse.WithMirrorRepair(await RunMirrorRepairAsync(request.MirrorNodeId, cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.PreviewMirrorDrain => FluxVaultIpcResponse.WithMirrorRebalance(await PreviewMirrorDrainAsync(Require(request.MirrorNodeId, "mirror node id"), cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.RunMirrorDrain => FluxVaultIpcResponse.WithMirrorRebalance(await RunMirrorDrainAsync(Require(request.MirrorNodeId, "mirror node id"), cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.GetSyncStatus => FluxVaultIpcResponse.WithStatus(await GetStatusAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.ResolveConflict => await ResolveConflictResponseAsync(request, cancellationToken).ConfigureAwait(false),
            _ => FluxVaultIpcResponse.Failure($"Unsupported command: {request.Command}")
        };
    }

    private static bool AllowsConcurrentRequest(FluxVaultIpcCommand command)
    {
        return command is FluxVaultIpcCommand.GetStatus
            or FluxVaultIpcCommand.GetSyncStatus
            or FluxVaultIpcCommand.GetActivity
            or FluxVaultIpcCommand.ListBlockedFiles
            or FluxVaultIpcCommand.ListVersions
            or FluxVaultIpcCommand.InspectVersion
            or FluxVaultIpcCommand.GetRepositoryHealth
            or FluxVaultIpcCommand.PreviewRestoreSelection;
    }

    private async Task<FluxVaultIpcResponse> SaveConfigurationResponseAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Configuration is null)
        {
            return FluxVaultIpcResponse.Failure("Configuration payload is required.");
        }

        await SaveConfigurationAsync(request.Configuration, cancellationToken).ConfigureAwait(false);
        return FluxVaultIpcResponse.Ok();
    }

    private async Task<FluxVaultIpcResponse> RestoreVersionResponseAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken)
    {
        var versionId = Require(request.VersionId, "version id");
        var outputPath = Require(request.OutputPath, "output path");
        try
        {
            await RestoreVersionAsync(versionId, outputPath, cancellationToken).ConfigureAwait(false);
            return FluxVaultIpcResponse.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return FluxVaultIpcResponse.Failure($"Restore failed for {outputPath}: {ex.Message}");
        }
    }

    private async Task<FluxVaultIpcResponse> RestoreVersionPreviewResponseAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken)
    {
        var versionId = Require(request.VersionId, "version id");
        try
        {
            return FluxVaultIpcResponse.WithOutputPath(await RestoreVersionPreviewAsync(versionId, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return FluxVaultIpcResponse.Failure($"Preview restore failed for {versionId}: {ex.Message}");
        }
    }

    private async Task<FluxVaultIpcResponse> RestoreSelectionResponseAsync(
        FluxVaultIpcRequest request,
        bool execute,
        CancellationToken cancellationToken)
    {
        var sourcePath = Require(request.SourcePath, "source path");
        var destinationMode = request.DestinationMode ?? RestoreSelectionDestinationMode.Elsewhere;
        try
        {
            var summary = execute
                ? await RunRestoreSelectionAsync(
                        sourcePath,
                        request.IsDirectory,
                        destinationMode,
                        request.DestinationPath,
                        request.OverwriteConfirmed,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await PreviewRestoreSelectionAsync(
                        sourcePath,
                        request.IsDirectory,
                        destinationMode,
                        request.DestinationPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            return FluxVaultIpcResponse.WithRestoreSelection(summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return FluxVaultIpcResponse.Failure($"Restore selection failed for {sourcePath}: {ex.Message}");
        }
    }

    private string BuildVersionPreviewPath(FileVersionManifest manifest)
    {
        var fileName = Path.GetFileName(manifest.SourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{manifest.VersionId}.preview";
        }

        return Path.Combine(
            versionPreviewRoot,
            SafePathSegment(manifest.VersionId),
            SafePathSegment(fileName));
    }

    private void CleanOldVersionPreviews(DateTimeOffset olderThanUtc)
    {
        try
        {
            if (!Directory.Exists(versionPreviewRoot))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(versionPreviewRoot, "*", SearchOption.AllDirectories))
            {
                var lastWrite = File.GetLastWriteTimeUtc(file);
                if (lastWrite < olderThanUtc.UtcDateTime)
                {
                    File.Delete(file);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(versionPreviewRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "preview" : safe;
    }

    private async Task<FluxVaultIpcResponse> SetProtectionPausedResponseAsync(CancellationToken cancellationToken)
    {
        await SetProtectionPausedAsync(cancellationToken).ConfigureAwait(false);
        return FluxVaultIpcResponse.Ok();
    }

    private async Task<FluxVaultIpcResponse> ResolveConflictResponseAsync(
        FluxVaultIpcRequest request,
        CancellationToken cancellationToken)
    {
        var conflictId = Require(request.ConflictId, "conflict id");
        if (request.ConflictAction is null)
        {
            return FluxVaultIpcResponse.Failure("Conflict action is required.");
        }

        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var conflicts = new FileSyncConflictStore(configuration.RepositoryPath);
        await conflicts.ResolveConflictAsync(conflictId, request.ConflictAction.Value, cancellationToken).ConfigureAwait(false);
        return FluxVaultIpcResponse.Ok();
    }

    private BackupRunSummary CompleteBackup(
        bool success,
        string message,
        int captured,
        int failed,
        int enumerated = 0,
        int skipped = 0,
        int recordedDeletions = 0,
        DateTimeOffset? startedUtc = null)
    {
        lastCaptureUtc = DateTimeOffset.UtcNow;
        lastMessage = message;
        InvalidateRecentVersionStatusCache();
        TimeSpan? elapsed = startedUtc is null ? null : lastCaptureUtc.Value - startedUtc.Value;
        lock (runtimeGate)
        {
            backupRuntime = backupRuntime with
            {
                IsRunning = false,
                Phase = "Idle",
                CompletedAtUtc = lastCaptureUtc,
                EnumeratedFileCount = enumerated,
                CapturedFileCount = captured,
                SkippedUnchangedFileCount = skipped,
                FailedFileCount = failed,
                RecordedDeletionCount = recordedDeletions,
                ActiveWorkers = 0,
                LastElapsed = elapsed
            };
        }

        return new BackupRunSummary(
            success,
            message,
            captured,
            failed,
            lastCaptureUtc.Value,
            enumerated,
            skipped,
            recordedDeletions,
            elapsed);
    }

    private async Task<string> ApplyRetentionAfterSuccessfulBackupAsync(
        IChunkRepository repository,
        FluxVaultConfiguration configuration,
        string message,
        CancellationToken cancellationToken)
    {
        if (!configuration.RetentionPolicy.IsEnabled)
        {
            return message;
        }

        var retention = await repository.ApplyRetentionAsync(
                configuration.RetentionPolicy,
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        lastRetention = retention;
        return $"{message} {FormatRetentionSummary(retention)}";
    }

    private static string FormatRetentionSummary(RepositoryRetentionResult result)
    {
        return $"Retention kept {result.KeptVersionCount} version(s), pruned {result.PrunedVersionCount}, reclaimed {FormatBytes(result.ReclaimedBytes)}.";
    }

    private async Task SaveRepositoryMaintenanceResultAsync(
        RepositoryScrubReport? scrub,
        RestoreRehearsalReport? rehearsal,
        MirrorRepairReport? mirrorRepair,
        MirrorRebalancePreviewReport? mirrorRebalance,
        CancellationToken cancellationToken)
    {
        var state = await repositoryMaintenanceStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var nextScrub = scrub ?? state.LastScrub;
        var nextRehearsal = rehearsal ?? state.LastRestoreRehearsal;
        var nextMirrorRepair = mirrorRepair ?? state.LastMirrorRepair;
        var nextMirrorRebalance = mirrorRebalance ?? state.LastMirrorRebalance;
        var health = BuildRepositoryHealthSnapshot(nextScrub, nextRehearsal, nextMirrorRepair, nextMirrorRebalance);
        await repositoryMaintenanceStateStore.SaveAsync(
            state with
            {
                LastHealth = health,
                LastScrub = nextScrub,
                LastRestoreRehearsal = nextRehearsal,
                LastMirrorRepair = nextMirrorRepair,
                LastMirrorRebalance = nextMirrorRebalance
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static RepositoryHealthSnapshot BuildRepositoryHealthSnapshot(
        RepositoryScrubReport? scrub,
        RestoreRehearsalReport? rehearsal,
        MirrorRepairReport? mirrorRepair,
        MirrorRebalancePreviewReport? mirrorRebalance,
        string? summaryOverride = null)
    {
        var state = CombineHealth(scrub?.HealthState, rehearsal?.HealthState, mirrorRepair?.HealthState, mirrorRebalance?.HealthState);
        return new RepositoryHealthSnapshot(
            CheckedAtUtc: DateTimeOffset.UtcNow,
            OverallState: state,
            Summary: summaryOverride ?? BuildRepositoryHealthSummary(scrub, rehearsal, mirrorRepair, mirrorRebalance, state),
            LastScrub: scrub,
            LastRestoreRehearsal: rehearsal,
            LastMirrorRepair: mirrorRepair,
            LastMirrorRebalance: mirrorRebalance);
    }

    private static RepositoryHealthState CombineHealth(params RepositoryHealthState?[] states)
    {
        var actual = states.Where(state => state is not null).Select(state => state!.Value).ToArray();
        if (actual.Length == 0)
        {
            return RepositoryHealthState.Warning;
        }

        if (actual.Contains(RepositoryHealthState.Critical))
        {
            return RepositoryHealthState.Critical;
        }

        return actual.Contains(RepositoryHealthState.Warning)
            ? RepositoryHealthState.Warning
            : RepositoryHealthState.Healthy;
    }

    private static string BuildRepositoryHealthSummary(
        RepositoryScrubReport? scrub,
        RestoreRehearsalReport? rehearsal,
        MirrorRepairReport? mirrorRepair,
        MirrorRebalancePreviewReport? mirrorRebalance,
        RepositoryHealthState state)
    {
        var parts = new List<string>();
        if (scrub is not null)
        {
            parts.Add($"scrub {scrub.HealthState.ToString().ToLowerInvariant()}, repaired {scrub.RepairedIssueCount} issue(s)");
        }

        if (rehearsal is not null)
        {
            parts.Add($"restore rehearsal {rehearsal.HealthState.ToString().ToLowerInvariant()}, failed {rehearsal.FailedVersionCount}");
        }

        if (mirrorRepair is not null)
        {
            var operation = mirrorRepair.IsPreview ? "mirror repair preview" : "mirror repair";
            parts.Add($"{operation} {mirrorRepair.HealthState.ToString().ToLowerInvariant()}, repaired {mirrorRepair.RepairedIssueCount} issue(s)");
        }

        if (mirrorRebalance is not null)
        {
            parts.Add($"mirror placement preview {mirrorRebalance.HealthState.ToString().ToLowerInvariant()}, actions {mirrorRebalance.ActionCount}");
        }

        return parts.Count == 0
            ? "Repository health has not run yet."
            : $"Repository health {state.ToString().ToLowerInvariant()}: {string.Join("; ", parts)}.";
    }

    private static string FormatScrubSummary(RepositoryScrubReport report)
    {
        return $"Repository scrub completed: checked {report.CheckedChunkCount} chunk(s), repaired {report.RepairedIssueCount}, unresolved {report.Issues.Count(issue => issue.RepairAction == RepositoryRepairAction.Unresolved)}.";
    }

    private static string FormatRestoreRehearsalSummary(RestoreRehearsalReport report)
    {
        return $"Restore rehearsal completed: passed {report.RehearsedVersionCount}, failed {report.FailedVersionCount}.";
    }

    private static string FormatMirrorRepairSummary(MirrorRepairReport report)
    {
        var action = report.IsPreview ? "Mirror repair preview" : "Mirror repair";
        return $"{action} completed: {report.IssueCount} issue(s), repaired {report.RepairedIssueCount}.";
    }

    private static string FormatMirrorRebalanceSummary(MirrorRebalancePreviewReport report)
    {
        var operation = report.Operation == MirrorRebalanceOperation.Drain ? "Mirror drain" : "Mirror placement";
        var phase = report.IsPreview ? "preview" : "apply";
        return $"{operation} {phase} completed: {report.ActionCount} action(s), copy {FormatBytes(report.EstimatedCopyBytes)}, delete {FormatBytes(report.EstimatedDeleteBytes)}.";
    }

    private static bool IsDrainComplete(MirrorRebalancePreviewReport report)
    {
        return report.Operation == MirrorRebalanceOperation.Drain
               && !report.IsPreview
               && report.HealthState == RepositoryHealthState.Healthy
               && report.ActionCount == 0
               && !string.IsNullOrWhiteSpace(report.RequestedMirrorNodeId);
    }

    private static FluxVaultConfiguration DisableMirrorNode(
        FluxVaultConfiguration configuration,
        string mirrorNodeId)
    {
        var mirrorSet = (configuration.MirrorSet ?? MirrorSetConfiguration.FromLegacyPath(configuration.MirrorPath)).Normalise();
        var updatedNodes = mirrorSet.Nodes
            .Select(node => string.Equals(node.Id, mirrorNodeId, StringComparison.OrdinalIgnoreCase)
                ? node with { IsEnabled = false }
                : node)
            .ToArray();
        return configuration with
        {
            MirrorPath = null,
            MirrorSet = new MirrorSetConfiguration(updatedNodes, mirrorSet.PlacementPolicy).Normalise()
        };
    }

    private static DeviceIdentityRuntimeStatus BuildDeviceIdentityStatus(SyncConfiguration sync)
    {
        var normalised = sync.Normalise(AppContext.BaseDirectory);
        return new DeviceIdentityRuntimeStatus(
            normalised.LocalDevice.DeviceId,
            normalised.LocalDevice.DisplayName,
            normalised.TrustedDevices
                .Select(device => new TrustedDeviceRuntimeStatus(
                    device.DeviceId,
                    device.DisplayName,
                    device.TrustState,
                    device.TrustedAtUtc,
                    device.LastSeenAtUtc))
                .ToArray());
    }

    private static async Task<SyncRuntimeStatus> BuildSyncStatusAsync(
        FluxVaultConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var journal = new FilePeerSyncJournal(configuration.RepositoryPath);
        var mappings = new FileSyncMappingStore(configuration.RepositoryPath);
        var applied = new FileSyncApplicationStore(configuration.RepositoryPath);
        var hydrations = new FileSyncHydrationStore(configuration.RepositoryPath);
        var conflicts = new FileSyncConflictStore(configuration.RepositoryPath);
        return new SyncRuntimeStatus(
            configuration.Sync.LocalDevice.DeviceId,
            await journal.ListPeerHeadsAsync(cancellationToken).ConfigureAwait(false),
            await journal.ListCursorsAsync(cancellationToken).ConfigureAwait(false),
            await mappings.ListMappingsAsync(cancellationToken).ConfigureAwait(false),
            await applied.ListAppliedVersionsAsync(cancellationToken).ConfigureAwait(false),
            await hydrations.ListHydrationsAsync(cancellationToken).ConfigureAwait(false),
            await conflicts.ListConflictsAsync(cancellationToken).ConfigureAwait(false));
    }

    private static PerformanceWorkspaceRuntimeStatus BuildPerformanceWorkspaceStatus(
        PerformanceWorkspaceConfiguration configuration)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "eng", "winfsp", "Register-FluxVaultWinFspWorkspace.ps1");
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "eng", "winfsp", "FluxVault.WinFsp.Workspace.manifest.json");
        return new PerformanceWorkspaceRuntimeStatus(
            configuration.IsEnabled,
            configuration.Mode,
            configuration.WorkspacePath,
            configuration.CacheSizeMegabytes,
            configuration.MountName,
            scriptPath,
            manifestPath,
            configuration.IsEnabled
                ? "Prepared; WinFsp driver install not executed."
                : "Disabled; WinFsp driver install not executed.",
            IsDriverCheckDeferred: true);
    }

    private static ShellIntegrationRuntimeStatus BuildShellIntegrationStatus(
        ShellIntegrationConfiguration configuration)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "eng", "shell-integration", "Register-FluxVaultShellIntegration.ps1");
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "eng", "shell-integration", "FluxVault.CloudFiles.ProjFs.manifest.json");
        return new ShellIntegrationRuntimeStatus(
            configuration.IsEnabled,
            configuration.Mode,
            configuration.SyncRootPath,
            configuration.DisplayName,
            configuration.HydrationPolicy,
            configuration.PlaceholderStatePath,
            scriptPath,
            manifestPath,
            configuration.IsEnabled
                ? "Prepared; shell registration not executed."
                : "Disabled; shell registration not executed.",
            IsRegistrationDeferred: true,
            IsPlaceholderCreationDeferred: true);
    }

    private static DirectCloudRuntimeStatus BuildDirectCloudStatus(DirectCloudConfiguration configuration)
    {
        var providers = DirectCloudAdapterCatalog.Descriptors
            .Select(descriptor => new DirectCloudProviderRuntimeStatus(
                descriptor.Provider,
                descriptor.PackageId,
                descriptor.SdkClientType.FullName ?? descriptor.SdkClientType.Name,
                IsSdkAvailable: true))
            .ToArray();
        var adapters = configuration.Adapters
            .Select(adapter => new DirectCloudAdapterRuntimeStatus(
                adapter.Id,
                adapter.Provider,
                adapter.DisplayName,
                adapter.IsEnabled,
                adapter.IsEnabled
                    ? "Ready; credentials not loaded during status refresh."
                    : "Disabled; credentials not loaded during status refresh."))
            .ToArray();
        var enabled = adapters.Count(adapter => adapter.IsEnabled);
        var status = configuration.IsEnabled
            ? $"Direct cloud adapters configured; {enabled} enabled adapter(s); live validation deferred."
            : "Direct cloud adapters disabled; live validation deferred.";
        return new DirectCloudRuntimeStatus(
            configuration.IsEnabled,
            status,
            IsLiveValidationDeferred: true,
            providers,
            adapters);
    }

    private static SecurityPostureRuntimeStatus BuildSecurityPostureStatus(SecurityPostureConfiguration configuration)
    {
        var encryption = configuration.Normalise().ClientSideEncryption;
        var status = encryption.IsEnabled
            ? "Client-side encryption planned; execution deferred."
            : "Client-side encryption disabled; repository artefacts remain plain.";
        return new SecurityPostureRuntimeStatus(
            encryption.IsEnabled,
            encryption.Algorithm,
            encryption.MetadataMode,
            encryption.ActiveKeyReferenceId,
            encryption.KeyReferences.Count,
            status,
            IsEncryptionExecutionDeferred: true);
    }

    private static FleetRuntimeStatus BuildFleetStatus(EnterpriseFleetConfiguration configuration)
    {
        var normalised = configuration.Normalise();
        var status = normalised.IsEnabled
            ? "Local fleet policy configured; remote management deferred."
            : "Fleet policy disabled; local-only operation.";
        return new FleetRuntimeStatus(
            normalised.IsEnabled,
            normalised.Mode,
            normalised.PolicySource,
            normalised.Assignments.Count,
            normalised.LocalStatuses.Count,
            status,
            IsRemoteManagementDeferred: true);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }

    private static string FormatBackupCaptureMessage(
        int captured,
        int recordedDeletions,
        string capturedUnit = "file")
    {
        var message = $"Captured {captured} {capturedUnit}(s).";
        return recordedDeletions == 0
            ? message
            : $"{message} Recorded {recordedDeletions} deletion(s).";
    }

    private async Task<DeletionRecordSummary> RecordMissingTrackedEntriesAsync(
        IChunkRepository repository,
        FluxVaultConfiguration configuration,
        IReadOnlyList<RepositoryVersionSummary> latestEntries,
        CancellationToken cancellationToken)
    {
        var liveLatestEntries = latestEntries
            .Where(entry => !entry.IsDeleted)
            .OrderBy(entry => PathDepth(entry.SourcePath))
            .ThenByDescending(entry => entry.EntryKind)
            .ToArray();
        var deletedFolders = new List<string>();
        var recorded = 0;
        var failed = 0;
        var messages = new List<string>();
        var mirrorWarnings = new List<string>();

        foreach (var entry in liveLatestEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deletedFolders.Any(folder => PathEqualsOrUnder(entry.SourcePath, folder)))
            {
                continue;
            }

            if (!TryFindTrackedEntryFolder(configuration, entry, out var folder))
            {
                continue;
            }

            var missing = entry.EntryKind == RepositoryEntryKind.Folder
                ? !Directory.Exists(entry.SourcePath)
                : !File.Exists(entry.SourcePath);
            if (!missing)
            {
                continue;
            }

            var result = await RecordDeletionAsync(repository, folder, entry, cancellationToken).ConfigureAwait(false);
            recorded += result.Recorded;
            failed += result.Failed;
            messages.AddRange(result.Messages);
            mirrorWarnings.AddRange(result.MirrorWarnings);
            if (result.Recorded > 0 && entry.EntryKind == RepositoryEntryKind.Folder)
            {
                deletedFolders.Add(entry.SourcePath);
            }
        }

        return new DeletionRecordSummary(recorded, failed, messages, mirrorWarnings);
    }

    private async Task<DeletionRecordSummary> RecordTargetedDeletionsAsync(
        IChunkRepository repository,
        FluxVaultConfiguration configuration,
        IReadOnlyList<string> requestedPaths,
        IReadOnlyList<RepositoryVersionSummary> latestEntries,
        CancellationToken cancellationToken)
    {
        var missingPaths = requestedPaths
            .Where(path => !File.Exists(path) && !Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingPaths.Length == 0)
        {
            return DeletionRecordSummary.Empty;
        }

        var recorded = 0;
        var failed = 0;
        var messages = new List<string>();
        var mirrorWarnings = new List<string>();

        foreach (var path in missingPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = latestEntries
                .Where(entry => !entry.IsDeleted)
                .Where(entry => string.Equals(TrimPath(entry.SourcePath), TrimPath(path), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.EntryKind)
                .ThenByDescending(entry => entry.CapturedAtUtc)
                .ThenByDescending(entry => entry.VersionId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (entry is null || !TryFindTrackedEntryFolder(configuration, entry, out var folder))
            {
                continue;
            }

            var result = await RecordDeletionAsync(repository, folder, entry, cancellationToken).ConfigureAwait(false);
            recorded += result.Recorded;
            failed += result.Failed;
            messages.AddRange(result.Messages);
            mirrorWarnings.AddRange(result.MirrorWarnings);
        }

        return new DeletionRecordSummary(recorded, failed, messages, mirrorWarnings);
    }

    private static async Task<DeletionRecordSummary> RecordDeletionAsync(
        IChunkRepository repository,
        WatchedFolderConfiguration folder,
        RepositoryVersionSummary entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await repository.RecordDeletionAsync(new RepositoryDeletionRequest(
                    WatchedFolderId: folder.Id,
                    WatchedFolderPath: folder.Path,
                    SourcePath: entry.SourcePath,
                    IsDirectory: entry.EntryKind == RepositoryEntryKind.Folder,
                    DeletedAtUtc: DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            return result is null
                ? DeletionRecordSummary.Empty
                : new DeletionRecordSummary(1, 0, [], result.MirrorWarnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new DeletionRecordSummary(
                0,
                1,
                [$"{entry.SourcePath}: deletion tracking failed: {ex.Message}"],
                []);
        }
    }

    private static bool TryFindTrackedEntryFolder(
        FluxVaultConfiguration configuration,
        RepositoryVersionSummary entry,
        out WatchedFolderConfiguration folder)
    {
        foreach (var candidate in configuration.WatchedFolders.Where(folder => folder.IsEnabled && Directory.Exists(folder.Path)))
        {
            if (!PathEqualsOrUnder(entry.SourcePath, candidate.Path))
            {
                continue;
            }

            if (entry.EntryKind == RepositoryEntryKind.File)
            {
                if (!MatchesPatterns(candidate, entry.SourcePath)
                    || ProtectionExclusionMatcher.IsFileExcluded(entry.SourcePath, configuration.ExclusionRules)
                    || !ProtectionSelectionRegexMatcher.IsFileIncluded(entry.SourcePath, configuration.SelectionRules))
                {
                    continue;
                }
            }
            else if (ProtectionExclusionMatcher.IsFolderExcluded(entry.SourcePath, configuration.ExclusionRules)
                     || ProtectionSelectionRegexMatcher.IsFolderExcluded(entry.SourcePath, configuration.SelectionRules))
            {
                continue;
            }

            folder = candidate;
            return true;
        }

        folder = null!;
        return false;
    }

    private static bool PathEqualsOrUnder(string path, string root)
    {
        var trimmedPath = TrimPath(path);
        var trimmedRoot = TrimPath(root);
        return string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase)
               || IsUnderPath(trimmedPath, trimmedRoot);
    }

    private static int PathDepth(string path)
    {
        return TrimPath(path).Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);
    }

    private static async Task<IReadOnlyList<RepositoryVersionSummary>> GetTrackedEntriesForBackupAsync(
        IChunkRepository repository,
        CancellationToken cancellationToken)
    {
        return await repository.ListLatestEntriesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, RepositoryVersionSummary> BuildLatestFileLookup(
        IReadOnlyList<RepositoryVersionSummary> latestEntries)
    {
        return latestEntries
            .Where(entry => entry.EntryKind == RepositoryEntryKind.File && !entry.IsDeleted)
            .GroupBy(entry => TrimPath(entry.SourcePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(entry => entry.CapturedAtUtc)
                    .ThenByDescending(entry => entry.VersionId, StringComparer.Ordinal)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUnchangedTarget(
        FileBackupTarget target,
        IReadOnlyDictionary<string, RepositoryVersionSummary> latestByPath,
        TimeSpan deepVerificationInterval)
    {
        return latestByPath.TryGetValue(TrimPath(target.Path), out var latest)
            && latest.SourceLastWriteUtc is not null
            && latest.CapturedAtUtc + deepVerificationInterval > DateTimeOffset.UtcNow
            && latest.LogicalLength == target.Length
            && latest.SourceLastWriteUtc.Value.UtcDateTime == target.LastWriteUtc.UtcDateTime;
    }

    private void UpdateSkippedUnchangedStatus(FileBackupTarget target)
    {
        UpdateCaptureRuntimeStatus(
            target.Path,
            target.Folder.Id,
            CaptureRuntimeState.SkippedUnchanged,
            lastEventUtc: null,
            nextForcedCaptureUtc: null,
            consistencyDetail: "Skipped; source length and last write time are unchanged.");
    }

    private static int GetEffectiveMaximumConcurrentCaptures(int configuredMaximum)
    {
        return Math.Clamp(configuredMaximum, 1, 64);
    }

    private static IEnumerable<FileBackupTarget> EnumerateBackupTargets(
        FluxVaultConfiguration configuration,
        List<string> messages,
        Action addFailure)
    {
        foreach (var folder in configuration.WatchedFolders.Where(folder => folder.IsEnabled))
        {
            if (!Directory.Exists(folder.Path))
            {
                addFailure();
                messages.Add($"Watched folder does not exist: {folder.Path}");
                continue;
            }

            foreach (var file in EnumerateIncludedFiles(folder, configuration))
            {
                if (TryCreateTarget(configuration, folder, file, out var target))
                {
                    yield return target;
                }
            }
        }
    }

    private static IEnumerable<FileBackupTarget> EnumerateBackupTargets(
        FluxVaultConfiguration configuration,
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken)
    {
        foreach (var path in filePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                continue;
            }

            if (TryFindIncludedFolder(configuration, path, out var folder)
                && TryCreateTarget(configuration, folder, path, out var target))
            {
                yield return target;
            }
        }
    }

    private async Task<(int Captured, int Failed, int Enumerated, int Skipped, IReadOnlyList<string> Messages, IReadOnlyList<string> MirrorWarnings)> CaptureTargetsAsync(
        IChunkRepository repository,
        IEnumerable<FileBackupTarget> targets,
        int maximumConcurrentCaptures,
        IReadOnlyDictionary<string, RepositoryVersionSummary> latestByPath,
        TimeSpan deepVerificationInterval,
        bool allowUnchangedSkip,
        CancellationToken cancellationToken)
    {
        var captured = 0;
        var failed = 0;
        var enumerated = 0;
        var skipped = 0;
        var messages = new List<string>();
        var mirrorWarnings = new List<string>();
        var workerCount = Math.Clamp(maximumConcurrentCaptures, 1, 64);
        if (workerCount > 1)
        {
            var parallelMessages = new System.Collections.Concurrent.ConcurrentBag<string>();
            var parallelMirrorWarnings = new System.Collections.Concurrent.ConcurrentBag<string>();
            using var enumerator = targets.GetEnumerator();
            var enumeratorGate = new Lock();
            var tasks = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
            {
                while (true)
                {
                    FileBackupTarget target;
                    lock (enumeratorGate)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!enumerator.MoveNext())
                        {
                            return;
                        }

                        target = enumerator.Current;
                    }

                    Interlocked.Increment(ref enumerated);
                    if (allowUnchangedSkip && IsUnchangedTarget(target, latestByPath, deepVerificationInterval))
                    {
                        Interlocked.Increment(ref skipped);
                        UpdateSkippedUnchangedStatus(target);
                        PublishBackupProgress("Skipping unchanged files", captured, failed, enumerated, skipped, workerCount);
                        continue;
                    }

                    PublishBackupProgress("Capturing", captured, failed, enumerated, skipped, workerCount);
                    var result = await CaptureTargetAsync(repository, target, cancellationToken).ConfigureAwait(false);
                    if (result.Success)
                    {
                        Interlocked.Increment(ref captured);
                        foreach (var warning in result.MirrorWarnings)
                        {
                            parallelMirrorWarnings.Add(warning);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        if (!string.IsNullOrWhiteSpace(result.Message))
                        {
                            parallelMessages.Add(result.Message);
                        }
                    }
                }
            }, cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return (
                captured,
                failed,
                enumerated,
                skipped,
                parallelMessages.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                parallelMirrorWarnings.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            enumerated++;
            if (allowUnchangedSkip && IsUnchangedTarget(target, latestByPath, deepVerificationInterval))
            {
                skipped++;
                UpdateSkippedUnchangedStatus(target);
                PublishBackupProgress("Skipping unchanged files", captured, failed, enumerated, skipped, workerCount);
                continue;
            }

            PublishBackupProgress("Capturing", captured, failed, enumerated, skipped, workerCount);
            var result = await CaptureTargetAsync(repository, target, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                captured++;
                mirrorWarnings.AddRange(result.MirrorWarnings);
            }
            else
            {
                failed++;
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    messages.Add(result.Message);
                }
            }
        }

        return (captured, failed, enumerated, skipped, messages, mirrorWarnings);
    }

    private async Task<CaptureTargetResult> CaptureTargetAsync(
        IChunkRepository repository,
        FileBackupTarget target,
        CancellationToken cancellationToken)
    {
        UpdateCaptureRuntimeStatus(
            target.Path,
            target.Folder.Id,
            CaptureRuntimeState.Capturing,
            lastEventUtc: null,
            nextForcedCaptureUtc: null);
        await using var capture = await captureProvider.CaptureAsync(new FileCaptureRequest(target.Path), cancellationToken)
            .ConfigureAwait(false);
        if (!capture.Success || capture.Content is null)
        {
            UpdateCaptureRuntimeStatus(
                target.Path,
                target.Folder.Id,
                IsBlockedFailure(capture.Message) ? CaptureRuntimeState.Blocked : CaptureRuntimeState.Failed,
                lastEventUtc: null,
                nextForcedCaptureUtc: null,
                blockedReason: IsBlockedFailure(capture.Message) ? capture.Message : null);
            return new CaptureTargetResult(false, $"{target.Path}: {capture.Message}", []);
        }

        var commit = await repository.CommitAsync(new FileCommitRequest(
            WatchedFolderId: target.Folder.Id,
            SourcePath: Path.GetFullPath(target.Path),
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Consistency: capture.Consistency,
            Compression: target.Compression,
            MinimumCompressionBytes: target.MinimumCompressionBytes,
            Content: capture.Content,
            WatchedFolderPath: target.Folder.Path,
            SourceLastWriteUtc: target.LastWriteUtc), cancellationToken).ConfigureAwait(false);
        UpdateCaptureRuntimeStatus(
            target.Path,
            target.Folder.Id,
            CaptureRuntimeState.Captured,
            lastEventUtc: null,
            nextForcedCaptureUtc: null,
            consistency: capture.Consistency,
            consistencyDetail: capture.Message);
        return new CaptureTargetResult(true, null, commit.MirrorWarnings);
    }

    private static IEnumerable<string> EnumerateIncludedFiles(
        WatchedFolderConfiguration folder,
        FluxVaultConfiguration configuration)
    {
        foreach (var file in EnumerateCandidateFiles(folder.Path, folder.Recursive, configuration))
        {
            var name = Path.GetFileName(file);
            var included = folder.IncludePatterns.Count == 0
                || folder.IncludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
            var excluded = folder.ExcludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
            if (included
                && !excluded
                && !ProtectionExclusionMatcher.IsFileExcluded(file, configuration.ExclusionRules)
                && ProtectionSelectionRegexMatcher.IsFileIncluded(file, configuration.SelectionRules))
            {
                yield return file;
            }
        }
    }

    private static bool TryFindIncludedFolder(
        FluxVaultConfiguration configuration,
        string filePath,
        out WatchedFolderConfiguration folder)
    {
        foreach (var candidate in configuration.WatchedFolders.Where(folder => folder.IsEnabled && Directory.Exists(folder.Path)))
        {
            if (IsUnderWatchedFolder(candidate, filePath)
                && MatchesPatterns(candidate, filePath)
                && !ProtectionExclusionMatcher.IsFileExcluded(filePath, configuration.ExclusionRules)
                && ProtectionSelectionRegexMatcher.IsFileIncluded(filePath, configuration.SelectionRules))
            {
                folder = candidate;
                return true;
            }
        }

        folder = null!;
        return false;
    }

    private static bool IsUnderWatchedFolder(WatchedFolderConfiguration folder, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var root = Path.GetFullPath(folder.Path);
        if (!folder.Recursive)
        {
            return string.Equals(
                Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPatterns(WatchedFolderConfiguration folder, string filePath)
    {
        var name = Path.GetFileName(filePath);
        var included = folder.IncludePatterns.Count == 0
            || folder.IncludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
        var excluded = folder.ExcludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
        return included && !excluded;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(
        string folderPath,
        bool recursive,
        FluxVaultConfiguration configuration)
    {
        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }

        if (!recursive)
        {
            yield break;
        }

        foreach (var childFolder in Directory.EnumerateDirectories(folderPath))
        {
            if (ProtectionExclusionMatcher.IsFolderExcluded(childFolder, configuration.ExclusionRules)
                || ProtectionSelectionRegexMatcher.IsFolderExcluded(childFolder, configuration.SelectionRules))
            {
                continue;
            }

            foreach (var file in EnumerateCandidateFiles(childFolder, recursive: true, configuration: configuration))
            {
                yield return file;
            }
        }
    }

    private IChunkRepository CreateRepository(FluxVaultConfiguration configuration)
    {
        return repositoryFactory(configuration);
    }

    private static IRepositoryMetadataStore CreateMetadataStore(FluxVaultConfiguration configuration)
    {
        if (configuration.MetadataStore.Provider != MetadataStoreProvider.PostgreSql)
        {
            throw new InvalidOperationException($"Unsupported metadata store provider: {configuration.MetadataStore.Provider}.");
        }

        return new PostgreSqlRepositoryMetadataStore(
            configuration.MetadataStore,
            configuration.Sync.LocalDevice.DeviceId);
    }

    private static IChunkRepository CreateRepository(
        FluxVaultConfiguration configuration,
        IRepositoryMetadataStore metadataStore)
    {
        return new FileSystemChunkRepository(
            configuration.RepositoryPath,
            new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            configuration.MirrorSet,
            metadataStore);
    }

    private static string Require(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.")
            : value;
    }

    private static bool TryCreateTarget(
        FluxVaultConfiguration configuration,
        WatchedFolderConfiguration folder,
        string path,
        out FileBackupTarget target)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            target = null!;
            return false;
        }

        var resolved = WorkloadPolicyResolver.Resolve(
            configuration,
            folder,
            path,
            fileInfo.Length,
            isHotFile: false);
        if (resolved.IsExcluded)
        {
            target = null!;
            return false;
        }

        target = new FileBackupTarget(
            folder,
            Path.GetFullPath(path),
            fileInfo.Length,
            new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
            resolved.Compression,
            resolved.MinimumCompressionBytes);
        return true;
    }

    private sealed record FileBackupTarget(
        WatchedFolderConfiguration Folder,
        string Path,
        long Length,
        DateTimeOffset LastWriteUtc,
        CompressionPreference Compression,
        int MinimumCompressionBytes);
    private sealed record CaptureTargetResult(bool Success, string? Message, IReadOnlyList<string> MirrorWarnings);
    private sealed record DeletionRecordSummary(
        int Recorded,
        int Failed,
        IReadOnlyList<string> Messages,
        IReadOnlyList<string> MirrorWarnings)
    {
        public static DeletionRecordSummary Empty { get; } = new(0, 0, [], []);
    }

    private IReadOnlyList<CaptureRuntimeStatus> GetCaptureStatuses()
    {
        lock (runtimeGate)
        {
            return captureStatuses.Values
                .OrderByDescending(status => status.LastCaptureAttemptUtc ?? status.LastEventUtc ?? DateTimeOffset.MinValue)
                .Take(200)
                .ToArray();
        }
    }

    private IReadOnlyList<WatcherRuntimeStatus> GetWatcherStatuses()
    {
        lock (runtimeGate)
        {
            return watcherStatuses.Values
                .OrderBy(status => status.WatchedFolderId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static bool IsBlockedFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || message.Contains("access", StringComparison.OrdinalIgnoreCase)
            || message.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("locked", StringComparison.OrdinalIgnoreCase);
    }

    private static FluxVaultActivityEvent ToActivityEvent(CaptureRuntimeStatus status)
    {
        var kind = status.State switch
        {
            CaptureRuntimeState.WaitingForQuietWindow => FluxVaultActivityKind.Pending,
            CaptureRuntimeState.ForcedHotFileSnapshot => FluxVaultActivityKind.Pending,
            CaptureRuntimeState.Capturing => FluxVaultActivityKind.Capturing,
            CaptureRuntimeState.Captured => FluxVaultActivityKind.Captured,
            CaptureRuntimeState.Blocked => FluxVaultActivityKind.Blocked,
            CaptureRuntimeState.Failed => FluxVaultActivityKind.Failed,
            CaptureRuntimeState.SkippedUnchanged => FluxVaultActivityKind.Info,
            _ => FluxVaultActivityKind.Info
        };
        var title = status.State switch
        {
            CaptureRuntimeState.WaitingForQuietWindow => "Waiting for quiet window",
            CaptureRuntimeState.ForcedHotFileSnapshot => "Forced hot-file snapshot",
            CaptureRuntimeState.Capturing => "Capturing",
            CaptureRuntimeState.Captured => "Captured",
            CaptureRuntimeState.Blocked => "Blocked",
            CaptureRuntimeState.Failed => "Failed",
            CaptureRuntimeState.SkippedUnchanged => "Skipped unchanged",
            _ => "Activity"
        };
        var detail = status.BlockedReason
            ?? status.DelayReason
            ?? status.ConsistencyDetail
            ?? Path.GetFileName(status.SourcePath);
        return new FluxVaultActivityEvent(
            status.LastCaptureAttemptUtc ?? status.LastEventUtc ?? DateTimeOffset.UtcNow,
            kind,
            title,
            detail,
            status.SourcePath);
    }

    private sealed class InMemoryRepositoryMaintenanceStateStore : IRepositoryMaintenanceStateStore
    {
        private RepositoryMaintenanceState state = RepositoryMaintenanceState.Empty;

        public Task<RepositoryMaintenanceState> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(state);
        }

        public Task SaveAsync(RepositoryMaintenanceState state, CancellationToken cancellationToken = default)
        {
            this.state = state;
            return Task.CompletedTask;
        }
    }
}
