using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxVault.Core.Tests;

public sealed class ConfigurationStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

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
        Assert.False(configuration.RepositoryMaintenancePolicy.RunAutomatically);
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
        Assert.Equal(MetadataStoreProvider.PostgreSql, configuration.MetadataStore.Provider);
        Assert.Equal("localhost", configuration.MetadataStore.Host);
        Assert.Equal(5432, configuration.MetadataStore.Port);
        Assert.Equal("fluxvault_metadata", configuration.MetadataStore.DatabaseName);
        Assert.Equal("fluxvault", configuration.MetadataStore.Username);
        Assert.Equal(Path.Combine(programData, "db-backups"), configuration.MetadataStore.BackupDirectory);
        Assert.Equal(30, configuration.MetadataStore.BackupRetentionDays);
        Assert.Equal(4, configuration.MetadataStore.MaxCaptureWorkers);
        Assert.Equal(8, configuration.MetadataStore.MaxDbWriterConcurrency);
        Assert.Equal(TimeSpan.FromMinutes(15), configuration.MetadataStore.ExportLagWarningThreshold);
        Assert.True(configuration.DiagnosticsPolicy.IsFileLoggingEnabled);
        Assert.Equal(DiagnosticLogLevel.Warning, configuration.DiagnosticsPolicy.FileLogLevel);
        Assert.Equal(Path.Combine(programData, "logs"), configuration.DiagnosticsPolicy.LogDirectory);
        Assert.Equal(25, configuration.DiagnosticsPolicy.MaxLogFileMegabytes);
        Assert.Equal(8, configuration.DiagnosticsPolicy.RetainedLogFileCount);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.DiagnosticsPolicy.TelemetrySampleInterval);
        Assert.Equal(4320, configuration.DiagnosticsPolicy.RetainedTelemetrySampleCount);
    }

    [Fact]
    public async Task Profile_set_store_migrates_legacy_single_profile_configuration()
    {
        using var workspace = TemporaryWorkspace.Create();
        var configPath = Path.Combine(workspace.RootPath, "config.json");
        var legacy = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            IsEnabled = true
        };
        Directory.CreateDirectory(workspace.RootPath);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(legacy, JsonOptions));
        var store = new FileFluxVaultProfileSetStore(configPath, workspace.RootPath);

        var profileSet = await store.LoadAsync();

        Assert.Equal(FluxVaultProfileConfiguration.DefaultProfileId, profileSet.ActiveProfileId);
        var profile = Assert.Single(profileSet.Profiles);
        Assert.Equal("Default", profile.DisplayName);
        Assert.True(profile.IsEnabled);
        Assert.Equal(workspace.RepositoryPath, profile.Configuration.RepositoryPath);
    }

    [Fact]
    public async Task Profile_configuration_store_saves_only_selected_profile()
    {
        using var workspace = TemporaryWorkspace.Create();
        var configPath = Path.Combine(workspace.RootPath, "config.json");
        var profileSetStore = new FileFluxVaultProfileSetStore(configPath, workspace.RootPath);
        var first = FluxVaultProfileConfiguration.CreateDefault(workspace.RootPath);
        var second = new FluxVaultProfileConfiguration(
            "archive",
            "Archive",
            IsEnabled: true,
            FluxVaultConfiguration.CreateDefault(Path.Combine(workspace.RootPath, "archive")) with
            {
                RepositoryPath = Path.Combine(workspace.RootPath, "archive-repository")
            });
        await profileSetStore.SaveAsync(new FluxVaultProfileSetConfiguration(first.Id, [first, second]));
        var selectedStore = new FluxVaultProfileConfigurationStore(profileSetStore, "archive");
        var updated = second.Configuration with { RepositoryPath = Path.Combine(workspace.RootPath, "archive-repository-2") };

        await selectedStore.SaveAsync(updated);

        var loaded = await profileSetStore.LoadAsync();
        Assert.Equal(first.Configuration.RepositoryPath, loaded.Profiles.Single(profile => profile.Id == first.Id).Configuration.RepositoryPath);
        Assert.Equal(updated.RepositoryPath, loaded.Profiles.Single(profile => profile.Id == "archive").Configuration.RepositoryPath);
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
    public async Task Legacy_repository_maintenance_policy_does_not_run_automatically()
    {
        using var workspace = TemporaryWorkspace.Create();
        var configPath = Path.Combine(workspace.RootPath, "config.json");
        var json = """
        {
          "repositoryPath": "repository",
          "mirrorPath": null,
          "isEnabled": true,
          "watchedFolders": [],
          "repositoryMaintenancePolicy": {
            "isEnabled": true,
            "interval": "01:00:00",
            "autoRepairFromMirror": true,
            "restoreRehearsalVersionCount": 3
          }
        }
        """;
        Directory.CreateDirectory(workspace.RootPath);
        await File.WriteAllTextAsync(configPath, json);
        var store = new FileFluxVaultConfigurationStore(configPath, workspace.RootPath);

        var configuration = await store.LoadAsync();

        Assert.True(configuration.RepositoryMaintenancePolicy.IsEnabled);
        Assert.False(configuration.RepositoryMaintenancePolicy.RunAutomatically);
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
    public async Task Save_and_load_round_trips_diagnostics_policy()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var expected = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            DiagnosticsPolicy = new DiagnosticsPolicy(
                IsFileLoggingEnabled: true,
                FileLogLevel: DiagnosticLogLevel.Trace,
                LogDirectory: Path.Combine(workspace.RootPath, "trace-logs"),
                MaxLogFileMegabytes: 3,
                RetainedLogFileCount: 4,
                TelemetrySampleInterval: TimeSpan.FromSeconds(2),
                RetainedTelemetrySampleCount: 12)
        };

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.DiagnosticsPolicy, actual.DiagnosticsPolicy);
    }

    [Fact]
    public async Task Save_clamps_diagnostics_policy_caps()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            DiagnosticsPolicy = new DiagnosticsPolicy(
                IsFileLoggingEnabled: true,
                FileLogLevel: DiagnosticLogLevel.Trace,
                LogDirectory: " ",
                MaxLogFileMegabytes: 0,
                RetainedLogFileCount: 0,
                TelemetrySampleInterval: TimeSpan.Zero,
                RetainedTelemetrySampleCount: 0)
        };

        await store.SaveAsync(configuration);

        var actual = await store.LoadAsync();
        Assert.Equal(Path.Combine(workspace.RootPath, "logs"), actual.DiagnosticsPolicy.LogDirectory);
        Assert.Equal(1, actual.DiagnosticsPolicy.MaxLogFileMegabytes);
        Assert.Equal(1, actual.DiagnosticsPolicy.RetainedLogFileCount);
        Assert.Equal(DiagnosticsPolicy.DefaultTelemetrySampleInterval, actual.DiagnosticsPolicy.TelemetrySampleInterval);
        Assert.Equal(1, actual.DiagnosticsPolicy.RetainedTelemetrySampleCount);
    }

    [Fact]
    public async Task Save_and_load_round_trips_metadata_store_configuration()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var expected = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            MetadataStore = new MetadataStoreConfiguration(
                Provider: MetadataStoreProvider.PostgreSql,
                Host: "127.0.0.1",
                Port: 15432,
                DatabaseName: "fluxvault_lab",
                Username: "fluxvault_writer",
                ServiceName: "postgresql-x64-18",
                BackupDirectory: Path.Combine(workspace.RootPath, "backups"),
                BackupRetentionDays: 14,
                MaxCaptureWorkers: 12,
                MaxDbWriterConcurrency: 24,
                ExportLagWarningThreshold: TimeSpan.FromMinutes(5))
        };

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.MetadataStore, actual.MetadataStore);
    }

    [Fact]
    public async Task Save_rejects_invalid_metadata_writer_concurrency()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(
            Path.Combine(workspace.RootPath, "config.json"),
            workspace.RootPath);
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            MetadataStore = MetadataStoreConfiguration.CreateDefault(workspace.RootPath) with
            {
                MaxDbWriterConcurrency = 0
            }
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(configuration));

        Assert.Contains("Metadata database writer concurrency", error.Message);
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
    public async Task Default_configuration_has_disabled_cloud_files_shell_integration()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);

        var configuration = await store.LoadAsync();

        Assert.False(configuration.ShellIntegration.IsEnabled);
        Assert.Equal(ShellIntegrationMode.CloudFilesApi, configuration.ShellIntegration.Mode);
        Assert.Equal(ShellHydrationPolicy.OnDemand, configuration.ShellIntegration.HydrationPolicy);
        Assert.Equal(Path.Combine(workspace.RootPath, "shell-integration", "sync-root"), configuration.ShellIntegration.SyncRootPath);
        Assert.Equal(Path.Combine(workspace.RootPath, "shell-integration", "state"), configuration.ShellIntegration.PlaceholderStatePath);
    }

    [Fact]
    public async Task Default_configuration_has_disabled_direct_cloud_adapters()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);

        var configuration = await store.LoadAsync();

        Assert.False(configuration.DirectCloud.IsEnabled);
        Assert.True(configuration.DirectCloud.BlockOnMeteredNetwork);
        Assert.Null(configuration.DirectCloud.BandwidthLimitBytesPerSecond);
        Assert.Empty(configuration.DirectCloud.Adapters);
    }

    [Fact]
    public async Task Default_configuration_has_disabled_security_and_fleet_foundations()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);

        var configuration = await store.LoadAsync();

        Assert.False(configuration.SecurityPosture.ClientSideEncryption.IsEnabled);
        Assert.Equal(ClientSideEncryptionAlgorithm.Aes256Gcm, configuration.SecurityPosture.ClientSideEncryption.Algorithm);
        Assert.Equal(EncryptionMetadataMode.PlainMetadata, configuration.SecurityPosture.ClientSideEncryption.MetadataMode);
        Assert.Null(configuration.SecurityPosture.ClientSideEncryption.ActiveKeyReferenceId);
        Assert.Empty(configuration.SecurityPosture.ClientSideEncryption.KeyReferences);
        Assert.False(configuration.Fleet.IsEnabled);
        Assert.Equal(FleetPolicyMode.LocalOnly, configuration.Fleet.Mode);
        Assert.Null(configuration.Fleet.PolicySource);
        Assert.Empty(configuration.Fleet.Assignments);
        Assert.Empty(configuration.Fleet.LocalStatuses);
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
    public async Task Save_and_load_round_trips_cloud_files_shell_integration()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var expected = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            ShellIntegration = new ShellIntegrationConfiguration(
                IsEnabled: true,
                Mode: ShellIntegrationMode.ProjFs,
                SyncRootPath: Path.Combine(workspace.RootPath, "native-root"),
                DisplayName: "FluxVault Native",
                HydrationPolicy: ShellHydrationPolicy.Manual,
                PlaceholderStatePath: Path.Combine(workspace.RootPath, "placeholder-state"))
        };

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.ShellIntegration, actual.ShellIntegration);
    }

    [Fact]
    public async Task Save_and_load_round_trips_direct_cloud_adapters_for_all_required_providers()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var expected = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            DirectCloud = new DirectCloudConfiguration(
                IsEnabled: true,
                Adapters:
                [
                    Adapter("azure", DirectCloudProvider.AzureBlob, "Azure archive", "https://acct.blob.core.windows.net", "vault", "fv-azure-secret"),
                    Adapter("s3", DirectCloudProvider.S3Compatible, "S3 archive", "https://s3.example.test", "fluxvault", "fv-s3-secret"),
                    Adapter("dropbox", DirectCloudProvider.Dropbox, "Dropbox archive", null, "/FluxVault", "fv-dropbox-oauth"),
                    Adapter("google", DirectCloudProvider.GoogleDrive, "Google Drive archive", null, "FluxVault", "fv-google-oauth"),
                    Adapter("onedrive", DirectCloudProvider.OneDrive, "OneDrive archive", null, "FluxVault", "fv-onedrive-oauth")
                ],
                BlockOnMeteredNetwork: true,
                BandwidthLimitBytesPerSecond: 2_000_000)
        };

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.True(actual.DirectCloud.IsEnabled);
        Assert.True(actual.DirectCloud.BlockOnMeteredNetwork);
        Assert.Equal(2_000_000, actual.DirectCloud.BandwidthLimitBytesPerSecond);
        Assert.Equal(expected.DirectCloud.Adapters, actual.DirectCloud.Adapters);
    }

    [Fact]
    public async Task Save_and_load_round_trips_security_and_fleet_foundations()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var now = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
        var expected = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            SecurityPosture = new SecurityPostureConfiguration(
                ClientSideEncryption: new ClientSideEncryptionConfiguration(
                    IsEnabled: true,
                    Algorithm: ClientSideEncryptionAlgorithm.Aes256Gcm,
                    MetadataMode: EncryptionMetadataMode.ProtectedMetadata,
                    ActiveKeyReferenceId: "local-key",
                    KeyReferences:
                    [
                        new EncryptionKeyReferenceConfiguration(
                            Id: "local-key",
                            Provider: EncryptionKeyProvider.WindowsDpapi,
                            ReferenceName: "FluxVault\\Keys\\Local",
                            Purpose: EncryptionKeyPurpose.RepositoryContent)
                    ])),
            Fleet = new EnterpriseFleetConfiguration(
                IsEnabled: true,
                Mode: FleetPolicyMode.LocalManaged,
                PolicySource: "file://fleet-policy.json",
                Assignments:
                [
                    new FleetPolicyAssignmentConfiguration(
                        Id: "default",
                        PolicyId: "policy-2026-05",
                        TargetDeviceId: "device-local",
                        AssignedAtUtc: now)
                ],
                LocalStatuses:
                [
                    new FleetDeviceStatusConfiguration(
                        DeviceId: "device-local",
                        PolicyId: "policy-2026-05",
                        State: FleetPolicyComplianceState.Compliant,
                        CheckedAtUtc: now,
                        Detail: "Local policy accepted.")
                ])
        };

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.SecurityPosture.ClientSideEncryption.IsEnabled, actual.SecurityPosture.ClientSideEncryption.IsEnabled);
        Assert.Equal(expected.SecurityPosture.ClientSideEncryption.Algorithm, actual.SecurityPosture.ClientSideEncryption.Algorithm);
        Assert.Equal(expected.SecurityPosture.ClientSideEncryption.MetadataMode, actual.SecurityPosture.ClientSideEncryption.MetadataMode);
        Assert.Equal(expected.SecurityPosture.ClientSideEncryption.ActiveKeyReferenceId, actual.SecurityPosture.ClientSideEncryption.ActiveKeyReferenceId);
        Assert.Equal(
            expected.SecurityPosture.ClientSideEncryption.KeyReferences,
            actual.SecurityPosture.ClientSideEncryption.KeyReferences);
        Assert.Equal(expected.Fleet.IsEnabled, actual.Fleet.IsEnabled);
        Assert.Equal(expected.Fleet.Mode, actual.Fleet.Mode);
        Assert.Equal(expected.Fleet.PolicySource, actual.Fleet.PolicySource);
        Assert.Equal(expected.Fleet.Assignments, actual.Fleet.Assignments);
        Assert.Equal(expected.Fleet.LocalStatuses, actual.Fleet.LocalStatuses);
    }

    [Fact]
    public async Task Save_rejects_inline_encryption_key_material()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            SecurityPosture = new SecurityPostureConfiguration(
                ClientSideEncryption: new ClientSideEncryptionConfiguration(
                    IsEnabled: true,
                    ActiveKeyReferenceId: "bad-key",
                    KeyReferences:
                    [
                        new EncryptionKeyReferenceConfiguration(
                            Id: "bad-key",
                            Provider: EncryptionKeyProvider.ExternalSecret,
                            ReferenceName: "-----BEGIN PRIVATE KEY-----abc",
                            Purpose: EncryptionKeyPurpose.RepositoryContent)
                    ]))
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(configuration));
        Assert.Contains("key reference", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inline", error.Message, StringComparison.OrdinalIgnoreCase);
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
                MaximumConcurrentCaptures: 2,
                WatcherEventBacklogLimit: 4096,
                UsnFallbackFullScanCooldown: TimeSpan.FromMinutes(30),
                SourceDeepVerificationInterval: TimeSpan.FromDays(7)));

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

    private static DirectCloudAdapterConfiguration Adapter(
        string id,
        DirectCloudProvider provider,
        string displayName,
        string? endpoint,
        string rootOrContainer,
        string credentialReference)
    {
        return new DirectCloudAdapterConfiguration(
            Id: id,
            Provider: provider,
            DisplayName: displayName,
            Endpoint: endpoint,
            ContainerOrBucket: rootOrContainer,
            RootPrefix: "fluxvault/repository",
            CredentialReference: credentialReference,
            IsEnabled: true);
    }
}
