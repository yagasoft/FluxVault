using System.Text;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Capture;
using FluxVault.Core.Configuration;
using FluxVault.Core.Service;

namespace FluxVault.Core.Tests;

public sealed class FluxVaultOperationsPerformanceTests
{
    [Fact]
    public async Task Backup_now_respects_maximum_concurrent_capture_workers()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        Directory.CreateDirectory(source);
        for (var index = 0; index < 12; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, $"file-{index:00}.txt"), $"content-{index}");
        }

        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders =
            [
                new WatchedFolderConfiguration(
                    "source",
                    source,
                    Recursive: false,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ],
            CaptureCadencePolicy = CaptureCadencePolicy.CreateDefault() with
            {
                MaximumConcurrentCaptures = 2
            }
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        var captureProvider = new CountingCaptureProvider();
        var operations = new FluxVaultOperations(store, captureProvider);

        var summary = await operations.RunBackupNowAsync();

        Assert.True(summary.Success);
        Assert.Equal(12, summary.CapturedFileCount);
        Assert.Equal(12, captureProvider.TotalCaptures);
        Assert.InRange(captureProvider.MaximumActiveCaptures, 1, 2);
    }

    [Fact]
    public async Task Folder_restore_latest_elsewhere_preserves_relative_paths_and_uses_latest_versions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        var nested = Path.Combine(source, "nested");
        Directory.CreateDirectory(nested);
        var firstFile = Path.Combine(source, "a.txt");
        var nestedFile = Path.Combine(nested, "b.txt");
        await File.WriteAllTextAsync(firstFile, "old");
        await File.WriteAllTextAsync(nestedFile, "nested");
        var operations = await CreateOperationsAsync(workspace, source, recursive: true);

        var firstSummary = await operations.RunBackupNowAsync();
        Assert.True(firstSummary.Success);
        await Task.Delay(20);
        await File.WriteAllTextAsync(firstFile, "new");
        var secondSummary = await operations.RunBackupNowAsync();
        Assert.True(secondSummary.Success);

        var destination = Path.Combine(workspace.RootPath, "restore");
        var preview = await operations.PreviewRestoreSelectionAsync(
            source,
            isDirectory: true,
            RestoreSelectionDestinationMode.Elsewhere,
            destination);
        Assert.Equal(2, preview.FileCount);
        Assert.Equal(0, preview.ConflictCount);
        Assert.Equal(0, preview.RestoredCount);

        var restore = await operations.RunRestoreSelectionAsync(
            source,
            isDirectory: true,
            RestoreSelectionDestinationMode.Elsewhere,
            destination,
            overwriteConfirmed: false);

        Assert.Equal(2, restore.FileCount);
        Assert.Equal(2, restore.RestoredCount);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")));
        Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "b.txt")));
    }

    [Fact]
    public async Task Restore_selection_refuses_conflicts_until_overwrite_is_confirmed()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source");
        Directory.CreateDirectory(source);
        var sourceFile = Path.Combine(source, "a.txt");
        await File.WriteAllTextAsync(sourceFile, "source");
        var operations = await CreateOperationsAsync(workspace, source, recursive: false);
        var backup = await operations.RunBackupNowAsync();
        Assert.True(backup.Success);
        var destinationFile = Path.Combine(workspace.RootPath, "restore", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        await File.WriteAllTextAsync(destinationFile, "existing");

        var previewResponse = await operations.HandleAsync(FluxVaultIpcRequest.PreviewRestoreSelection(
            sourceFile,
            isDirectory: false,
            RestoreSelectionDestinationMode.Elsewhere,
            destinationFile));
        Assert.True(previewResponse.Success);
        Assert.Equal(1, previewResponse.RestoreSelection?.ConflictCount);

        var deniedResponse = await operations.HandleAsync(FluxVaultIpcRequest.RunRestoreSelection(
            sourceFile,
            isDirectory: false,
            RestoreSelectionDestinationMode.Elsewhere,
            destinationFile,
            overwriteConfirmed: false));
        Assert.False(deniedResponse.Success);
        Assert.Contains("Confirm overwrite", deniedResponse.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("existing", await File.ReadAllTextAsync(destinationFile));

        var confirmedResponse = await operations.HandleAsync(FluxVaultIpcRequest.RunRestoreSelection(
            sourceFile,
            isDirectory: false,
            RestoreSelectionDestinationMode.Elsewhere,
            destinationFile,
            overwriteConfirmed: true));
        Assert.True(confirmedResponse.Success);
        Assert.Equal(1, confirmedResponse.RestoreSelection?.RestoredCount);
        Assert.Equal("source", await File.ReadAllTextAsync(destinationFile));
    }

    private static async Task<FluxVaultOperations> CreateOperationsAsync(
        TemporaryWorkspace workspace,
        string source,
        bool recursive)
    {
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders =
            [
                new WatchedFolderConfiguration(
                    "source",
                    source,
                    recursive,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    CompressionPreference.Off,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ],
            CaptureCadencePolicy = CaptureCadencePolicy.CreateDefault() with
            {
                MaximumConcurrentCaptures = 2
            }
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        return new FluxVaultOperations(store, new NormalFileCaptureProvider());
    }

    private sealed class CountingCaptureProvider : IFileCaptureProvider
    {
        private int activeCaptures;
        private int maximumActiveCaptures;
        private int totalCaptures;

        public int MaximumActiveCaptures => maximumActiveCaptures;

        public int TotalCaptures => totalCaptures;

        public async Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref activeCaptures);
            try
            {
                Interlocked.Increment(ref totalCaptures);
                UpdateMaximumActiveCaptures(active);
                await Task.Delay(20, cancellationToken);
                return FileCaptureResult.Captured(
                    new MemoryStream(Encoding.UTF8.GetBytes(Path.GetFileName(request.SourcePath))),
                    CaptureConsistency.BestEffort,
                    "Captured.");
            }
            finally
            {
                Interlocked.Decrement(ref activeCaptures);
            }
        }

        private void UpdateMaximumActiveCaptures(int active)
        {
            while (true)
            {
                var observed = maximumActiveCaptures;
                if (active <= observed)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref maximumActiveCaptures, active, observed) == observed)
                {
                    return;
                }
            }
        }
    }
}
