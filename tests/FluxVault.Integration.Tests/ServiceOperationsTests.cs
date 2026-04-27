using System.Security.Cryptography;
using System.Text;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Capture;
using FluxVault.Core.Configuration;
using FluxVault.Core.Service;

namespace FluxVault.Integration.Tests;

public sealed class ServiceOperationsTests
{
    [Fact]
    public async Task Run_backup_now_commits_watched_files_and_restore_round_trips_bytes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var source = Path.Combine(watched, "draft.txt");
        await File.WriteAllTextAsync(source, "first version");
        var configuration = NewConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();
        var versions = await operations.ListVersionsAsync();
        var version = Assert.Single(versions);
        var restored = Path.Combine(workspace.RootPath, "restored.txt");
        await operations.RestoreVersionAsync(version.VersionId, restored);

        Assert.True(backup.Success);
        Assert.Equal(await Sha256Async(source), await Sha256Async(restored));
    }

    [Fact]
    public async Task Repeated_edits_create_multiple_versions_for_same_file()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var source = Path.Combine(watched, "draft.txt");
        var configuration = NewConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        await File.WriteAllTextAsync(source, "first version");
        await operations.RunBackupNowAsync();
        await File.WriteAllTextAsync(source, "second version");
        await operations.RunBackupNowAsync();

        var versions = await operations.ListVersionsAsync();
        Assert.Equal(2, versions.Count(version => version.SourcePath == source));
    }

    [Fact]
    public async Task Targeted_backup_commits_only_matching_existing_files_once()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var included = Path.Combine(watched, "draft.txt");
        var excluded = Path.Combine(watched, "draft.tmp");
        var missing = Path.Combine(watched, "missing.txt");
        await File.WriteAllTextAsync(included, "targeted version");
        await File.WriteAllTextAsync(excluded, "excluded");
        var configuration = NewConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupForFilesAsync([included, included, excluded, missing]);

        var versions = await operations.ListVersionsAsync();
        Assert.True(backup.Success);
        Assert.Equal(1, backup.CapturedFileCount);
        Assert.Equal(0, backup.FailedFileCount);
        Assert.Equal(included, Assert.Single(versions).SourcePath);
    }

    [Fact]
    public async Task Mirror_receives_complete_artifacts_without_temporary_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        await File.WriteAllTextAsync(Path.Combine(watched, "draft.txt"), string.Concat(Enumerable.Repeat("mirror ", 600)));
        var mirror = Path.Combine(workspace.RootPath, "mirror");
        var configuration = NewConfiguration(workspace, watched) with { MirrorPath = mirror };
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();

        Assert.True(backup.Success);
        Assert.True(Directory.EnumerateFiles(Path.Combine(mirror, "manifests"), "*.json").Any());
        Assert.Empty(Directory.EnumerateFiles(mirror, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Run_retention_now_returns_summary_and_updates_status()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var configuration = NewConfiguration(workspace, watched) with
        {
            RetentionPolicy = ImmediatePrunePolicy()
        };
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        await SeedRepositoryVersionsAsync(workspace, 3);

        var result = await operations.RunRetentionNowAsync();
        var status = await operations.GetStatusAsync();

        Assert.Equal(2, result.PrunedVersionCount);
        Assert.NotNull(status.LastRetention);
        Assert.Equal(2, status.LastRetention.PrunedVersionCount);
        Assert.Contains("Retention", status.LastMessage);
    }

    [Fact]
    public async Task Successful_backup_triggers_enabled_retention()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var source = Path.Combine(watched, "draft.txt");
        var configuration = NewConfiguration(workspace, watched) with
        {
            RetentionPolicy = ImmediatePrunePolicy()
        };
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        await File.WriteAllTextAsync(source, "first version");
        await operations.RunBackupNowAsync();
        await Task.Delay(5);
        await File.WriteAllTextAsync(source, "second version");
        await operations.RunBackupNowAsync();

        var versions = await operations.ListVersionsAsync();
        var status = await operations.GetStatusAsync();
        Assert.Single(versions);
        Assert.NotNull(status.LastRetention);
        Assert.Equal(1, status.LastRetention.PrunedVersionCount);
    }

    [Fact]
    public async Task Deleted_watched_folder_fails_cleanly()
    {
        using var workspace = TemporaryWorkspace.Create();
        var missing = Path.Combine(workspace.RootPath, "missing");
        var configuration = NewConfiguration(workspace, missing);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();

        Assert.False(backup.Success);
        Assert.Contains("does not exist", backup.Message);
    }

    [Fact]
    public async Task Blocked_capture_is_reported_in_status()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var source = Path.Combine(watched, "draft.txt");
        await File.WriteAllTextAsync(source, "content");
        var configuration = NewConfiguration(workspace, watched);
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var operations = new FluxVaultOperations(
            store,
            new StubCaptureProvider(FileCaptureResult.Failed("The process cannot access the file because it is locked.")));
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();
        var status = await operations.GetStatusAsync();

        Assert.False(backup.Success);
        var captureStatus = Assert.Single(status.CaptureStatuses!);
        Assert.Equal(CaptureRuntimeState.Blocked, captureStatus.State);
        Assert.Contains("locked", captureStatus.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Set_protection_paused_toggles_enabled_state()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var configuration = NewConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        await operations.SetProtectionPausedAsync();
        var paused = await operations.GetStatusAsync();
        await operations.SetProtectionPausedAsync();
        var resumed = await operations.GetStatusAsync();

        Assert.False(paused.Configuration.IsEnabled);
        Assert.True(resumed.Configuration.IsEnabled);
    }

    private static FluxVaultOperations CreateOperations(TemporaryWorkspace workspace, FluxVaultConfiguration configuration)
    {
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        return new FluxVaultOperations(store, new FallbackFileCaptureProvider(new NormalFileCaptureProvider(), new UnavailableVssCaptureProvider()));
    }

    private static FluxVaultConfiguration NewConfiguration(TemporaryWorkspace workspace, string watched)
    {
        return new FluxVaultConfiguration(
            RepositoryPath: workspace.RepositoryPath,
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders:
            [
                new WatchedFolderConfiguration(
                    Id: "docs",
                    Path: watched,
                    Recursive: true,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: ["~$*"],
                    Compression: CompressionPreference.Zstd,
                    ResourceProfile: ResourceProfile.Fast,
                    IsEnabled: true)
            ]);
    }

    private static RetentionPolicy ImmediatePrunePolicy()
    {
        return new RetentionPolicy(
            IsEnabled: true,
            KeepAllFor: TimeSpan.Zero,
            KeepHourlyFor: TimeSpan.Zero,
            KeepDailyFor: TimeSpan.Zero,
            MinimumVersionsPerFile: 1);
    }

    private static async Task SeedRepositoryVersionsAsync(TemporaryWorkspace workspace, int count)
    {
        var repository = new FluxVault.Core.Storage.FileSystemChunkRepository(
            workspace.RepositoryPath,
            new FluxVault.Core.Chunking.FastCdcChunker(new FluxVault.Core.Chunking.ChunkingOptions(128, 256, 512)),
            new FluxVault.Core.Content.Blake3ContentHasher(),
            new FluxVault.Core.Content.ZstdChunkCodec());

        for (var index = 0; index < count; index++)
        {
            var payload = Encoding.UTF8.GetBytes($"seed version {index}");
            await repository.CommitAsync(new FluxVault.Abstractions.Storage.FileCommitRequest(
                WatchedFolderId: "docs",
                SourcePath: Path.Combine(workspace.RootPath, "watched", "draft.txt"),
                CapturedAtUtc: DateTimeOffset.UtcNow.AddDays(-200).AddMinutes(index),
                Consistency: FluxVault.Abstractions.Storage.CaptureConsistency.CrashConsistent,
                Compression: CompressionPreference.Off,
                MinimumCompressionBytes: 128,
                Content: new MemoryStream(payload)));
        }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class StubCaptureProvider(FileCaptureResult result) : IFileCaptureProvider
    {
        public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }
}
