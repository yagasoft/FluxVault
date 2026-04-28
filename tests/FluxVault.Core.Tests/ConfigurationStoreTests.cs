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
        Assert.Empty(configuration.SelectionRules);
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
            MirrorPath: Path.Combine(workspace.RootPath, "mirror"),
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
            ]);

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.RepositoryPath, actual.RepositoryPath);
        Assert.Equal(expected.MirrorPath, actual.MirrorPath);
        var watchedFolder = Assert.Single(actual.WatchedFolders);
        Assert.Equal("docs", watchedFolder.Id);
        Assert.Equal(ResourceProfile.Fast, watchedFolder.ResourceProfile);
        Assert.Equal(["*.txt", "*.docx"], watchedFolder.IncludePatterns);
        Assert.True(actual.RetentionPolicy.IsEnabled);
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
                    IsEnabled: true),
                new ProtectionSelectionRule(
                    Id: "file",
                    Path: Path.Combine(workspace.RootPath, "docs", "draft.txt"),
                    Mode: ProtectionSelectionMode.File,
                    Compression: CompressionPreference.Lz4,
                    ResourceProfile: ResourceProfile.Fast,
                    IsEnabled: true)
            ]);

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.SelectionRules, actual.SelectionRules);
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
