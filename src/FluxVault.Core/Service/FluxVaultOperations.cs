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
using FluxVault.Core.Sync;

namespace FluxVault.Core.Service;

public sealed class FluxVaultOperations(
    IFluxVaultConfigurationStore configurationStore,
    IFileCaptureProvider captureProvider,
    IRepositoryMaintenanceStateStore? repositoryMaintenanceStateStore = null,
    string? maintenanceStateRoot = null) : IFluxVaultRequestHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IRepositoryMaintenanceStateStore repositoryMaintenanceStateStore =
        repositoryMaintenanceStateStore ?? new InMemoryRepositoryMaintenanceStateStore();
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

    public async Task SaveConfigurationAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await configurationStore.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        lastMirrorWarnings = [];
        lastMessage = "Configuration saved.";
    }

    public async Task<BackupRunSummary> RunBackupNowAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!configuration.IsEnabled)
        {
            lastMirrorWarnings = [];
            return CompleteBackup(true, "Protection is disabled.", 0, 0);
        }

        var repository = CreateRepository(configuration);
        var failed = 0;
        var messages = new List<string>();
        var targets = new List<FileBackupTarget>();

        foreach (var folder in configuration.WatchedFolders.Where(folder => folder.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder.Path))
            {
                failed++;
                messages.Add($"Watched folder does not exist: {folder.Path}");
                continue;
            }

            foreach (var file in EnumerateIncludedFiles(folder, configuration))
            {
                if (TryCreateTarget(configuration, folder, file, out var target))
                {
                    targets.Add(target);
                }
            }
        }

        var (captured, captureFailed, captureMessages, mirrorWarnings) = await CaptureTargetsAsync(
                repository,
                targets,
                configuration.CaptureCadencePolicy.MaximumConcurrentCaptures,
                cancellationToken)
            .ConfigureAwait(false);
        failed += captureFailed;
        messages.AddRange(captureMessages);
        var success = failed == 0;
        var message = messages.Count == 0
            ? $"Captured {captured} file(s)."
            : string.Join(" ", messages);
        var distinctMirrorWarnings = mirrorWarnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinctMirrorWarnings.Length > 0)
        {
            message = $"{message} Mirror warning(s): {distinctMirrorWarnings.Length} mirror write issue(s).";
        }

        lastMirrorWarnings = distinctMirrorWarnings;
        if (success)
        {
            message = await ApplyRetentionAfterSuccessfulBackupAsync(repository, configuration, message, cancellationToken)
                .ConfigureAwait(false);
        }

        return CompleteBackup(success, message, captured, failed);
    }

    public async Task<BackupRunSummary> RunBackupForFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!configuration.IsEnabled)
        {
            lastMirrorWarnings = [];
            return CompleteBackup(true, "Protection is disabled.", 0, 0);
        }

        var targets = new List<FileBackupTarget>();
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

            if (TryFindIncludedFolder(configuration, path, out var folder))
            {
                if (TryCreateTarget(configuration, folder, path, out var target))
                {
                    targets.Add(target);
                }
            }
        }

        var (captured, failed, messages, mirrorWarnings) = await CaptureTargetsAsync(
                CreateRepository(configuration),
                targets,
                configuration.CaptureCadencePolicy.MaximumConcurrentCaptures,
                cancellationToken)
            .ConfigureAwait(false);
        var success = failed == 0;
        var message = messages.Count == 0
            ? $"Captured {captured} changed file(s)."
            : string.Join(" ", messages);
        var distinctMirrorWarnings = mirrorWarnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinctMirrorWarnings.Length > 0)
        {
            message = $"{message} Mirror warning(s): {distinctMirrorWarnings.Length} mirror write issue(s).";
        }

        lastMirrorWarnings = distinctMirrorWarnings;
        if (success)
        {
            message = await ApplyRetentionAfterSuccessfulBackupAsync(CreateRepository(configuration), configuration, message, cancellationToken)
                .ConfigureAwait(false);
        }

        return CompleteBackup(success, message, captured, failed);
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
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
        lastMessage = $"Restored {versionId} to {outputPath}.";
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
                LastCaptureAttemptUtc: state is CaptureRuntimeState.Capturing or CaptureRuntimeState.ForcedHotFileSnapshot
                    ? DateTimeOffset.UtcNow
                    : previous?.LastCaptureAttemptUtc,
                DelayReason: delayReason,
                BlockedReason: blockedReason,
                Consistency: consistency ?? previous?.Consistency,
                AttemptCount: state is CaptureRuntimeState.Capturing or CaptureRuntimeState.ForcedHotFileSnapshot
                    ? (previous?.AttemptCount ?? 0) + 1
                    : previous?.AttemptCount ?? 0,
                ConsistencyDetail: consistencyDetail ?? previous?.ConsistencyDetail);
        }
    }

    public async Task<FluxVaultServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RepositoryVersionSummary> versions = [];
        if (Directory.Exists(configuration.RepositoryPath))
        {
            versions = await CreateRepository(configuration).ListVersionsAsync(cancellationToken).ConfigureAwait(false);
        }

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
            Fleet: BuildFleetStatus(configuration.Fleet));
    }

    public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
    {
        return request.Command switch
        {
            FluxVaultIpcCommand.GetStatus => FluxVaultIpcResponse.WithStatus(await GetStatusAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.SaveConfiguration => await SaveConfigurationResponseAsync(request, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.RunBackupNow => FluxVaultIpcResponse.WithBackup(await RunBackupNowAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.ListVersions => FluxVaultIpcResponse.WithVersions(await ListVersionsAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.InspectVersion => FluxVaultIpcResponse.WithInspection(await InspectVersionAsync(Require(request.VersionId, "version id"), cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.RestoreVersion => await RestoreVersionResponseAsync(request, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.RestoreVersionPreview => await RestoreVersionPreviewResponseAsync(request, cancellationToken).ConfigureAwait(false),
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

    private BackupRunSummary CompleteBackup(bool success, string message, int captured, int failed)
    {
        lastCaptureUtc = DateTimeOffset.UtcNow;
        lastMessage = message;
        return new BackupRunSummary(success, message, captured, failed, lastCaptureUtc.Value);
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

    private async Task<(int Captured, int Failed, IReadOnlyList<string> Messages, IReadOnlyList<string> MirrorWarnings)> CaptureTargetsAsync(
        IChunkRepository repository,
        IReadOnlyList<FileBackupTarget> targets,
        int maximumConcurrentCaptures,
        CancellationToken cancellationToken)
    {
        var captured = 0;
        var failed = 0;
        var messages = new List<string>();
        var mirrorWarnings = new List<string>();
        if (maximumConcurrentCaptures > 1)
        {
            using var semaphore = new SemaphoreSlim(maximumConcurrentCaptures);
            var parallelMessages = new System.Collections.Concurrent.ConcurrentBag<string>();
            var parallelMirrorWarnings = new System.Collections.Concurrent.ConcurrentBag<string>();
            var tasks = targets.Select(async target =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
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
                finally
                {
                    semaphore.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return (
                captured,
                failed,
                parallelMessages.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                parallelMirrorWarnings.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        return (captured, failed, messages, mirrorWarnings);
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
            Content: capture.Content), cancellationToken).ConfigureAwait(false);
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

    private static FileSystemChunkRepository CreateRepository(FluxVaultConfiguration configuration)
    {
        return new FileSystemChunkRepository(
            configuration.RepositoryPath,
            new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            configuration.MirrorSet);
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
        var resolved = WorkloadPolicyResolver.Resolve(
            configuration,
            folder,
            path,
            new FileInfo(path).Length,
            isHotFile: false);
        if (resolved.IsExcluded)
        {
            target = null!;
            return false;
        }

        target = new FileBackupTarget(folder, path, resolved.Compression, resolved.MinimumCompressionBytes);
        return true;
    }

    private sealed record FileBackupTarget(
        WatchedFolderConfiguration Folder,
        string Path,
        CompressionPreference Compression,
        int MinimumCompressionBytes);
    private sealed record CaptureTargetResult(bool Success, string? Message, IReadOnlyList<string> MirrorWarnings);

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
