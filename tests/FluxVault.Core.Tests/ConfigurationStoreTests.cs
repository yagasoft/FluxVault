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
                MinimumVersionsPerFile: 10));

        await store.SaveAsync(expected);

        var actual = await store.LoadAsync();
        Assert.Equal(expected.RetentionPolicy, actual.RetentionPolicy);
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
