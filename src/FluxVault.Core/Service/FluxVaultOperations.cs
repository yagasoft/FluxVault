using System.IO.Enumeration;
using System.Text.Json;
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
        var captured = 0;
        var failed = 0;
        var messages = new List<string>();

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
                cancellationToken.ThrowIfCancellationRequested();
                await using var capture = await captureProvider.CaptureAsync(new FileCaptureRequest(file), cancellationToken)
                    .ConfigureAwait(false);
                if (!capture.Success || capture.Content is null)
                {
                    failed++;
                    messages.Add($"{file}: {capture.Message}");
                    continue;
                }

                await repository.CommitAsync(new FileCommitRequest(
                    WatchedFolderId: folder.Id,
                    SourcePath: Path.GetFullPath(file),
                    CapturedAtUtc: DateTimeOffset.UtcNow,
                    Consistency: capture.Consistency,
                    Compression: folder.Compression,
                    MinimumCompressionBytes: 256 * 1024,
                    Content: capture.Content), cancellationToken).ConfigureAwait(false);
                captured++;
            }
        }

        var success = failed == 0;
        var message = messages.Count == 0
            ? $"Captured {captured} file(s)."
            : string.Join(" ", messages);
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
                    Directory.Exists(folder.Path) ? "Ready" : "Folder missing"))
                .ToArray(),
            RecentVersions: versions.Take(50).ToArray());
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
}
