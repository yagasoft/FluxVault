using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Configuration;

namespace FluxVault.Core.Tests;

public sealed class ConfigurationStoreTests
{
    [Fact]
    public async Task Load_returns_default_configuration_when_file_is_missing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var programData = Path.Combine(workspace.RootPath, "ProgramData");
        var store = new FileFluxVaultConfigurationStore(Path.Combine(programData, "config.json"), programData);

        var configuration = await store.LoadAsync();

        Assert.True(configuration.IsEnabled);
        Assert.Equal(Path.Combine(programData, "repository"), configuration.RepositoryPath);
        Assert.Null(configuration.MirrorPath);
        Assert.Empty(configuration.WatchedFolders);
        Assert.True(configuration.RetentionPolicy.IsEnabled);
        Assert.Equal(TimeSpan.FromHours(24), configuration.RetentionPolicy.KeepAllFor);
        Assert.Equal(TimeSpan.FromDays(30), configuration.RetentionPolicy.KeepHourlyFor);
        Assert.Equal(TimeSpan.FromDays(180), configuration.RetentionPolicy.KeepDailyFor);
        Assert.Equal(20, configuration.RetentionPolicy.MinimumVersionsPerFile);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.CaptureCadencePolicy.WatcherPollInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), configuration.CaptureCadencePolicy.PeriodicReconciliationInterval);
        Assert.Equal(TimeSpan.FromSeconds(8), configuration.CaptureCadencePolicy.GetDebounce(ResourceProfile.Balanced));
        Assert.Equal(CodecProfile.Adaptive, configuration.CodecPolicy.Profile);
        Assert.Equal(CompressionPreference.Zstd, configuration.CodecPolicy.Codec);
        Assert.Equal(CompressionPreference.Lz4, configuration.CodecPolicy.HotFileOverride);
        Assert.True(configuration.RepositoryMaintenancePolicy.IsEnabled);
        Assert.Equal(TimeSpan.FromHours(24), configuration.RepositoryMaintenancePolicy.Interval);
        Assert.True(configuration.RepositoryMaintenancePolicy.AutoRepairFromMirror);
        Assert.Equal(3, configuration.RepositoryMaintenancePolicy.RestoreRehearsalVersionCount);
        Assert.Equal(WorkloadPolicyPresetId.GeneralPurpose, configuration.WorkloadPolicy.DefaultPreset);
        Assert.Empty(configuration.SelectionRules);
        Assert.Empty(configuration.ExclusionRules);
        Assert.Empty(configuration.MirrorSet.Nodes);
        Assert.Equal(MirrorPlacementProfile.FullCopy, configuration.MirrorSet.PlacementPolicy.Profile);
        Assert.Equal(1, configuration.MirrorSet.PlacementPolicy.MinimumMirrorCopies);
        Assert.False(string.IsNullOrWhiteSpace(configuration.Sync.LocalDevice.DeviceId));
        Assert.Equal(Environment.MachineName, configuration.Sync.LocalDevice.DisplayName);
        var localTrustedDevice = Assert.Single(configuration.Sync.TrustedDevices);
        Assert.Equal(configuration.Sync.LocalDevice.DeviceId, localTrustedDevice.DeviceId);
        Assert.Equal(DeviceTrustState.Local, localTrustedDevice.TrustState);
    }

    [Fact]
    public async Task Missing_configuration_uses_stable_local_device_identity()
    {
        using var workspace = TemporaryWorkspace.Create();
        var programData = Path.Combine(workspace.RootPath, "ProgramData");
        var store = new FileFluxVaultConfigurationStore(Path.Combine(programData, "config.json"), programData);

        var first = await store.LoadAsync();
        var second = await store.LoadAsync();

        Assert.Equal(first.Sync.LocalDevice.DeviceId, second.Sync.LocalDevice.DeviceId);
        Assert.StartsWith("fv-device-", first.Sync.LocalDevice.DeviceId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_and_load_round_trips_watched_folder_policy()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var expected = new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(workspace.RootPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders:
            [
                new WatchedFolderConfiguration(
                    Id: "docs",
                    Path: Path.Combine(workspace.RootPath, "docs"),
                    Recursive: true,
                    IncludePatterns: ["*.txt", "*.docx"],
                    ExcludePatterns: ["~$*"],
                    Compression: CompressionPreference.Zstd,
                    ResourceProfile: ResourceProfile.Fast,
                    IsEnabled: true)
            ],
            MirrorSet: new MirrorSetConfiguration(
            [
                new MirrorNodeConfiguration(
                    Id: "cloud",
                    Label: "Cloud copy",
                    Path: Path.Combine(workspace.RootPath, "mirror"),
                    IsEnabled: true)
            ]));

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.RepositoryPath, actual.RepositoryPath);
        Assert.Null(actual.MirrorPath);
        Assert.Equal(expected.MirrorSet.Nodes, actual.MirrorSet.Nodes);
        var watchedFolder = Assert.Single(actual.WatchedFolders);
        Assert.Equal("docs", watchedFolder.Id);
        Assert.Equal(ResourceProfile.Fast, watchedFolder.ResourceProfile);
        Assert.Equal(["*.txt", "*.docx"], watchedFolder.IncludePatterns);
        Assert.True(actual.RetentionPolicy.IsEnabled);
    }

    [Fact]
    public async Task Load_legacy_mirror_path_migrates_to_enabled_mirror_set_node()
    {
        using var workspace = TemporaryWorkspace.Create();
        var configPath = Path.Combine(workspace.RootPath, "config.json");
        var legacyMirrorPath = Path.Combine(workspace.RootPath, "legacy-mirror");
        await File.WriteAllTextAsync(
            configPath,
            $$"""
            {
              "repositoryPath": "{{Path.Combine(workspace.RootPath, "repository").Replace("\\", "\\\\")}}",
              "mirrorPath": "{{legacyMirrorPath.Replace("\\", "\\\\")}}",
              "isEnabled": true,
              "watchedFolders": []
            }
            """);
        var store = new FileFluxVaultConfigurationStore(configPath, workspace.RootPath);

        var actual = await store.LoadAsync();

        Assert.Null(actual.MirrorPath);
        var node = Assert.Single(actual.MirrorSet.Nodes);
        Assert.False(string.IsNullOrWhiteSpace(node.Id));
        Assert.Equal("Default mirror", node.Label);
        Assert.Equal(legacyMirrorPath, node.Path);
        Assert.True(node.IsEnabled);
        Assert.Equal(MirrorPlacementProfile.FullCopy, actual.MirrorSet.PlacementPolicy.Profile);
    }

    [Fact]
    public async Task Save_and_load_round_trips_multiple_mirror_set_nodes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var expected = new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(workspace.RootPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            MirrorSet: new MirrorSetConfiguration(
            [
                new MirrorNodeConfiguration(
                    Id: "cloud",
                    Label: "Cloud copy",
                    Path: Path.Combine(workspace.RootPath, "cloud"),
                    IsEnabled: true,
                    CapacityBudgetBytes: 1_000_000_000,
                    Priority: 200),
                new MirrorNodeConfiguration(
                    Id: "usb",
                    Label: "USB shelf copy",
                    Path: Path.Combine(workspace.RootPath, "usb"),
                    IsEnabled: false,
                    CapacityBudgetBytes: 500_000_000,
                    Priority: 50)
            ],
            new MirrorPlacementPolicyConfiguration(
                Profile: MirrorPlacementProfile.Redundant,
                MinimumMirrorCopies: 2)));

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();

        Assert.Null(actual.MirrorPath);
        Assert.Equal(expected.MirrorSet.Nodes, actual.MirrorSet.Nodes);
        Assert.Equal(expected.MirrorSet.PlacementPolicy, actual.MirrorSet.PlacementPolicy);
    }

    [Fact]
    public async Task Save_and_load_round_trips_device_identity_and_trusted_devices()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var expected = new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(workspace.RootPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            Sync: new SyncConfiguration(
                new DeviceIdentityConfiguration("device-local", "Studio PC", new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero)),
                [
                    new TrustedDeviceConfiguration("device-local", "Studio PC", DeviceTrustState.Local, new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero)),
                    new TrustedDeviceConfiguration("device-laptop", "Laptop", DeviceTrustState.Trusted, new DateTimeOffset(2026, 5, 1, 8, 5, 0, TimeSpan.Zero))
                ]));

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();

        Assert.Equal(expected.Sync.LocalDevice, actual.Sync.LocalDevice);
        Assert.Equal(expected.Sync.TrustedDevices, actual.Sync.TrustedDevices);
    }

    [Fact]
    public async Task Load_old_configuration_without_selection_rules_defaults_to_empty_rules()
    {
        using var workspace = TemporaryWorkspace.Create();
        var configPath = Path.Combine(workspace.RootPath, "config.json");
        await File.WriteAllTextAsync(
            configPath,
            $$"""
            {
              "repositoryPath": "{{Path.Combine(workspace.RootPath, "repository").Replace("\\", "\\\\")}}",
              "mirrorPath": null,
              "isEnabled": true,
              "watchedFolders": []
            }
            """);
        var store = new FileFluxVaultConfigurationStore(configPath, workspace.RootPath);

        var actual = await store.LoadAsync();

        Assert.Empty(actual.SelectionRules);
        Assert.Empty(actual.ExclusionRules);
        Assert.True(actual.RepositoryMaintenancePolicy.IsEnabled);
        Assert.Equal(TimeSpan.FromHours(24), actual.RepositoryMaintenancePolicy.Interval);
        Assert.Equal(WorkloadPolicyPresetId.GeneralPurpose, actual.WorkloadPolicy.DefaultPreset);
        Assert.False(string.IsNullOrWhiteSpace(actual.Sync.LocalDevice.DeviceId));
        Assert.Equal(actual.Sync.LocalDevice.DeviceId, Assert.Single(actual.Sync.TrustedDevices).DeviceId);
    }

    [Fact]
    public async Task Save_and_load_round_trips_file_browser_selection_rules()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var expected = new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(workspace.RootPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            SelectionRules:
            [
                new ProtectionSelectionRule(
                    Id: "root",
                    Path: Path.Combine(workspace.RootPath, "docs"),
                    Mode: ProtectionSelectionMode.RecursiveFolder,
                    Compression: CompressionPreference.Zstd,
                    ResourceProfile: ResourceProfile.Balanced,
                    IsEnabled: true,
                    WorkloadPreset: WorkloadPolicyPresetId.GeneralPurpose),
                new ProtectionSelectionRule(
                    Id: "file",
                    Path: Path.Combine(workspace.RootPath, "docs", "draft.txt"),
                    Mode: ProtectionSelectionMode.File,
                    Compression: CompressionPreference.Lz4,
                    ResourceProfile: ResourceProfile.Fast,
                    IsEnabled: true,
                    WorkloadPreset: WorkloadPolicyPresetId.DeveloperWorkspace)
            ]);

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.SelectionRules, actual.SelectionRules);
    }

    [Fact]
    public async Task Save_and_load_round_trips_workload_policy()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var expected = new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(workspace.RootPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            WorkloadPolicy: new WorkloadPolicyConfiguration(WorkloadPolicyPresetId.CadBim));

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();

        Assert.Equal(WorkloadPolicyPresetId.CadBim, actual.WorkloadPolicy.DefaultPreset);
    }

    [Fact]
    public async Task Save_and_load_round_trips_exclusion_rules()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var expected = new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(workspace.RootPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            ExclusionRules:
            [
                new ProtectionExclusionRule(
                    Id: "build",
                    Pattern: @"\\bin(\\|$)",
                    Target: ProtectionExclusionTarget.Folder,
                    IsEnabled: true,
                    Label: "Build output",
                    Description: "Skip generated build outputs.")
            ]);

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.ExclusionRules, actual.ExclusionRules);
    }

    [Fact]
    public async Task Save_rejects_invalid_exclusion_regex()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var configuration = new FluxVaultConfiguration(
            RepositoryPath: workspace.RepositoryPath,
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            ExclusionRules:
            [
                new ProtectionExclusionRule(
                    Id: "broken",
                    Pattern: "[",
                    Target: ProtectionExclusionTarget.Both,
                    IsEnabled: true)
            ]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(configuration));
        Assert.Contains("exclusion", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Default_configuration_has_disabled_winfsp_performance_workspace()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);

        var configuration = await store.LoadAsync();

        Assert.False(configuration.PerformanceWorkspace.IsEnabled);
        Assert.Equal(PerformanceWorkspaceMode.WinFsp, configuration.PerformanceWorkspace.Mode);
        Assert.Equal(Path.Combine(workspace.RootPath, "performance-workspace"), configuration.PerformanceWorkspace.WorkspacePath);
    }

    [Fact]
    public async Task Save_and_load_round_trips_winfsp_performance_workspace()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var expected = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            PerformanceWorkspace = new PerformanceWorkspaceConfiguration(
                IsEnabled: true,
                Mode: PerformanceWorkspaceMode.WinFsp,
                WorkspacePath: Path.Combine(workspace.RootPath, "fast-workspace"),
                CacheSizeMegabytes: 2048,
                MountName: "FluxVaultFast")
        };

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.PerformanceWorkspace, actual.PerformanceWorkspace);
    }

    [Fact]
    public async Task Save_and_load_round_trips_retention_policy()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var expected = new FluxVaultConfiguration(
            RepositoryPath: Path.Combine(workspace.RootPath, "repository"),
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: [],
            RetentionPolicy: new RetentionPolicy(
                IsEnabled: false,
                KeepAllFor: TimeSpan.FromHours(12),
                KeepHourlyFor: TimeSpan.FromDays(14),
                KeepDailyFor: TimeSpan.FromDays(90),
                MinimumVersionsPerFile: 10),
            CaptureCadencePolicy: new CaptureCadencePolicy(
                WatcherPollInterval: TimeSpan.FromSeconds(3),
                PeriodicReconciliationInterval: TimeSpan.FromMinutes(7),
                FastDebounce: TimeSpan.FromSeconds(1),
                BalancedDebounce: TimeSpan.FromSeconds(6),
                QuietDebounce: TimeSpan.FromSeconds(20),
                FastMaxHotFileDelay: TimeSpan.FromSeconds(20),
                BalancedMaxHotFileDelay: TimeSpan.FromMinutes(1),
                QuietMaxHotFileDelay: TimeSpan.FromMinutes(5),
                MinimumSameFileCaptureInterval: TimeSpan.FromSeconds(10),
                MaximumConcurrentCaptures: 2));

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.RetentionPolicy, actual.RetentionPolicy);
        Assert.Equal(expected.CaptureCadencePolicy, actual.CaptureCadencePolicy);
    }

    [Fact]
    public async Task Save_rejects_missing_repository_path()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var configuration = new FluxVaultConfiguration(
            RepositoryPath: "",
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders: []);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(configuration));
    }
}
