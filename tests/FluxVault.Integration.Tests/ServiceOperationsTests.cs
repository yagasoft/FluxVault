using System.Security.Cryptography;
using System.Text;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
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

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
