using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluxVault.Abstractions.ChangeTracking;
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
    public async Task App_consistent_capture_is_committed_and_reported_with_consistency_detail()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var source = Path.Combine(watched, "draft.txt");
        await File.WriteAllTextAsync(source, "live content");
        var configuration = NewConfiguration(workspace, watched);
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var operations = new FluxVaultOperations(
            store,
            new StubCaptureProvider(FileCaptureResult.Captured(
                new MemoryStream(Encoding.UTF8.GetBytes("snapshot content")),
                CaptureConsistency.AppConsistent,
                "SqlServerWriter covered D:\\Work\\db.mdf.")));
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();
        var version = Assert.Single(await operations.ListVersionsAsync());
        var status = await operations.GetStatusAsync();
        var activity = operations.GetActivity().Single(item => item.Kind == FluxVaultActivityKind.Captured);

        Assert.True(backup.Success);
        Assert.Equal(CaptureConsistency.AppConsistent, version.Consistency);
        var captureStatus = Assert.Single(status.CaptureStatuses!);
        Assert.Equal(CaptureConsistency.AppConsistent, captureStatus.Consistency);
        Assert.Contains("SqlServerWriter", captureStatus.ConsistencyDetail);
        Assert.Contains("SqlServerWriter", activity.Detail);
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

    [Fact]
    public async Task File_browser_recursive_folder_selection_backs_up_nested_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        var nested = Path.Combine(watched, "nested");
        Directory.CreateDirectory(nested);
        var source = Path.Combine(nested, "draft.txt");
        await File.WriteAllTextAsync(source, "nested");
        var configuration = NewSelectionConfiguration(
            workspace,
            [
                SelectionRule("docs", watched, ProtectionSelectionMode.RecursiveFolder)
            ]);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();

        var version = Assert.Single(await operations.ListVersionsAsync());
        Assert.True(backup.Success);
        Assert.Equal(source, version.SourcePath);
    }

    [Fact]
    public async Task File_browser_immediate_folder_selection_excludes_nested_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        var nested = Path.Combine(watched, "nested");
        Directory.CreateDirectory(nested);
        var top = Path.Combine(watched, "top.txt");
        var child = Path.Combine(nested, "child.txt");
        await File.WriteAllTextAsync(top, "top");
        await File.WriteAllTextAsync(child, "child");
        var configuration = NewSelectionConfiguration(
            workspace,
            [
                SelectionRule("docs", watched, ProtectionSelectionMode.ImmediateFiles)
            ]);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();

        var version = Assert.Single(await operations.ListVersionsAsync());
        Assert.True(backup.Success);
        Assert.Equal(top, version.SourcePath);
    }

    [Fact]
    public async Task File_browser_selected_file_rule_backs_up_only_that_file()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var selected = Path.Combine(watched, "selected.txt");
        var ignored = Path.Combine(watched, "ignored.txt");
        await File.WriteAllTextAsync(selected, "selected");
        await File.WriteAllTextAsync(ignored, "ignored");
        var configuration = NewSelectionConfiguration(
            workspace,
            [
                SelectionRule("selected", selected, ProtectionSelectionMode.File)
            ]);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();

        var version = Assert.Single(await operations.ListVersionsAsync());
        Assert.True(backup.Success);
        Assert.Equal(selected, version.SourcePath);
    }

    [Fact]
    public async Task Recursive_selection_skips_files_under_excluded_folder_regex()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        var includedFolder = Path.Combine(watched, "included");
        var excludedFolder = Path.Combine(watched, "node_modules");
        Directory.CreateDirectory(includedFolder);
        Directory.CreateDirectory(excludedFolder);
        var included = Path.Combine(includedFolder, "draft.txt");
        var excluded = Path.Combine(excludedFolder, "package.txt");
        await File.WriteAllTextAsync(included, "included");
        await File.WriteAllTextAsync(excluded, "excluded");
        var configuration = NewSelectionConfiguration(
            workspace,
            [SelectionRule("docs", watched, ProtectionSelectionMode.RecursiveFolder)],
            [ExclusionRule("node", excludedFolder, ProtectionExclusionTarget.Folder)]);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();

        var version = Assert.Single(await operations.ListVersionsAsync());
        Assert.True(backup.Success);
        Assert.Equal(included, version.SourcePath);
    }

    [Fact]
    public async Task Immediate_selection_applies_file_exclusion_regex()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var included = Path.Combine(watched, "draft.txt");
        var excluded = Path.Combine(watched, "draft.tmp");
        await File.WriteAllTextAsync(included, "included");
        await File.WriteAllTextAsync(excluded, "excluded");
        var configuration = NewSelectionConfiguration(
            workspace,
            [SelectionRule("docs", watched, ProtectionSelectionMode.ImmediateFiles)],
            [new ProtectionExclusionRule("tmp", @"\.tmp$", ProtectionExclusionTarget.File, IsEnabled: true)]);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();

        var version = Assert.Single(await operations.ListVersionsAsync());
        Assert.True(backup.Success);
        Assert.Equal(included, version.SourcePath);
    }

    [Fact]
    public async Task Selected_file_rule_is_skipped_when_path_matches_exclusion_regex()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var selected = Path.Combine(watched, "selected.txt");
        await File.WriteAllTextAsync(selected, "selected");
        var configuration = NewSelectionConfiguration(
            workspace,
            [SelectionRule("selected", selected, ProtectionSelectionMode.File)],
            [ExclusionRule("selected", selected, ProtectionExclusionTarget.File)]);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupNowAsync();

        Assert.True(backup.Success);
        Assert.Empty(await operations.ListVersionsAsync());
    }

    [Fact]
    public async Task Targeted_backup_skips_excluded_paths()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var included = Path.Combine(watched, "included.txt");
        var excluded = Path.Combine(watched, "excluded.txt");
        await File.WriteAllTextAsync(included, "included");
        await File.WriteAllTextAsync(excluded, "excluded");
        var configuration = NewSelectionConfiguration(
            workspace,
            [SelectionRule("docs", watched, ProtectionSelectionMode.ImmediateFiles)],
            [ExclusionRule("excluded", excluded, ProtectionExclusionTarget.File)]);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);

        var backup = await operations.RunBackupForFilesAsync([included, excluded]);

        var version = Assert.Single(await operations.ListVersionsAsync());
        Assert.True(backup.Success);
        Assert.Equal(included, version.SourcePath);
    }

    [Fact]
    public async Task Export_diagnostics_includes_durable_change_details()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var operations = CreateOperations(workspace, NewConfiguration(workspace, watched));
        var detail = new DurableChangeDetail(
            WatchedFolderId: "docs",
            Path: watched,
            VolumeRoot: @"D:\",
            Operation: "FSCTL_READ_USN_JOURNAL",
            Reason: "FSCTL_READ_USN_JOURNAL failed.",
            Win32ErrorCode: 5);
        operations.UpdateDurableChangeStatus(new DurableChangeRuntimeStatus(
            DateTimeOffset.UtcNow,
            "USN unavailable.",
            "FSCTL_READ_USN_JOURNAL failed on D:.",
            []) with
        {
            Details = [detail]
        });
        var exportFolder = Path.Combine(workspace.RootPath, "diagnostics");

        var diagnosticsPath = await operations.ExportDiagnosticsAsync(exportFolder);
        var json = await File.ReadAllTextAsync(diagnosticsPath);

        using var document = JsonDocument.Parse(json);
        var durableChange = document.RootElement.GetProperty("durableChange");
        Assert.Equal("FSCTL_READ_USN_JOURNAL failed on D:.", durableChange.GetProperty("fallbackReason").GetString());
        Assert.Equal("FSCTL_READ_USN_JOURNAL", durableChange.GetProperty("details")[0].GetProperty("operation").GetString());
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

    private static FluxVaultConfiguration NewSelectionConfiguration(
        TemporaryWorkspace workspace,
        IReadOnlyList<ProtectionSelectionRule> selectionRules,
        IReadOnlyList<ProtectionExclusionRule>? exclusionRules = null)
    {
        return new FluxVaultConfiguration(
            RepositoryPath: workspace.RepositoryPath,
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            SelectionRules: selectionRules,
            ExclusionRules: exclusionRules ?? []);
    }

    private static ProtectionSelectionRule SelectionRule(
        string id,
        string path,
        ProtectionSelectionMode mode)
    {
        return new ProtectionSelectionRule(
            id,
            path,
            mode,
            CompressionPreference.Zstd,
            ResourceProfile.Fast,
            IsEnabled: true);
    }

    private static ProtectionExclusionRule ExclusionRule(
        string id,
        string path,
        ProtectionExclusionTarget target)
    {
        return new ProtectionExclusionRule(
            id,
            Regex.Escape(Path.GetFullPath(path)) + @"(\\|$)",
            target,
            IsEnabled: true);
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
