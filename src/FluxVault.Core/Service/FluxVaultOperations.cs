using System.IO.Enumeration;
using System.Text.Json;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Ipc;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Service;

public sealed class FluxVaultOperations(
    IFluxVaultConfigurationStore configurationStore,
    IFileCaptureProvider captureProvider) : IFluxVaultRequestHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private DateTimeOffset? lastCaptureUtc;
    private string lastMessage = "Ready";
    private DurableChangeRuntimeStatus? durableChange;
    private RepositoryRetentionResult? lastRetention;

    public async Task SaveConfigurationAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await configurationStore.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        lastMessage = "Configuration saved.";
    }

    public async Task<BackupRunSummary> RunBackupNowAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!configuration.IsEnabled)
        {
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

            foreach (var file in EnumerateIncludedFiles(folder))
            {
                targets.Add(new FileBackupTarget(folder, file));
            }
        }

        var (captured, captureFailed, captureMessages) = await CaptureTargetsAsync(repository, targets, cancellationToken)
            .ConfigureAwait(false);
        failed += captureFailed;
        messages.AddRange(captureMessages);
        var success = failed == 0;
        var message = messages.Count == 0
            ? $"Captured {captured} file(s)."
            : string.Join(" ", messages);
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
                targets.Add(new FileBackupTarget(folder, path));
            }
        }

        var (captured, failed, messages) = await CaptureTargetsAsync(CreateRepository(configuration), targets, cancellationToken)
            .ConfigureAwait(false);
        var success = failed == 0;
        var message = messages.Count == 0
            ? $"Captured {captured} changed file(s)."
            : string.Join(" ", messages);
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

    public void UpdateDurableChangeStatus(DurableChangeRuntimeStatus status)
    {
        durableChange = status;
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
            DurableChange: durableChange);
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
            FluxVaultIpcCommand.ExportDiagnostics => FluxVaultIpcResponse.WithOutputPath(await ExportDiagnosticsAsync(Require(request.ExportPath, "export path"), cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.PreviewRetention => FluxVaultIpcResponse.WithRetentionPreview(await PreviewRetentionAsync(cancellationToken).ConfigureAwait(false)),
            FluxVaultIpcCommand.RunRetentionNow => FluxVaultIpcResponse.WithRetentionResult(await RunRetentionNowAsync(cancellationToken).ConfigureAwait(false)),
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
        await RestoreVersionAsync(
            Require(request.VersionId, "version id"),
            Require(request.OutputPath, "output path"),
            cancellationToken).ConfigureAwait(false);
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

    private async Task<(int Captured, int Failed, IReadOnlyList<string> Messages)> CaptureTargetsAsync(
        IChunkRepository repository,
        IReadOnlyList<FileBackupTarget> targets,
        CancellationToken cancellationToken)
    {
        var captured = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var capture = await captureProvider.CaptureAsync(new FileCaptureRequest(target.Path), cancellationToken)
                .ConfigureAwait(false);
            if (!capture.Success || capture.Content is null)
            {
                failed++;
                messages.Add($"{target.Path}: {capture.Message}");
                continue;
            }

            await repository.CommitAsync(new FileCommitRequest(
                WatchedFolderId: target.Folder.Id,
                SourcePath: Path.GetFullPath(target.Path),
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Consistency: capture.Consistency,
                Compression: target.Folder.Compression,
                MinimumCompressionBytes: 256 * 1024,
                Content: capture.Content), cancellationToken).ConfigureAwait(false);
            captured++;
        }

        return (captured, failed, messages);
    }

    private static IEnumerable<string> EnumerateIncludedFiles(WatchedFolderConfiguration folder)
    {
        var searchOption = folder.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(folder.Path, "*", searchOption))
        {
            var name = Path.GetFileName(file);
            var included = folder.IncludePatterns.Count == 0
                || folder.IncludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
            var excluded = folder.ExcludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
            if (included && !excluded)
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
            if (IsUnderWatchedFolder(candidate, filePath) && MatchesPatterns(candidate, filePath))
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

    private static FileSystemChunkRepository CreateRepository(FluxVaultConfiguration configuration)
    {
        return new FileSystemChunkRepository(
            configuration.RepositoryPath,
            new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            string.IsNullOrWhiteSpace(configuration.MirrorPath) ? null : configuration.MirrorPath);
    }

    private static string Require(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.")
            : value;
    }

    private sealed record FileBackupTarget(WatchedFolderConfiguration Folder, string Path);
}
