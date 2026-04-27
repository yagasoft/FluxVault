using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.ChangeTracking;

namespace FluxVault.Core.Tests;

public sealed class UsnCatchUpServiceTests
{
    [Fact]
    public async Task Unavailable_reader_returns_full_scan_decision_without_throwing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var service = CreateService(
            workspace,
            UsnChangeJournalReadResult.Unavailable("USN is not available on this volume."));
        var configuration = NewConfiguration(workspace);
        Directory.CreateDirectory(configuration.WatchedFolders.Single().Path);

        var result = await service.CatchUpAsync(configuration);

        Assert.True(result.RequiresFullScan);
        Assert.Empty(result.ChangedFiles);
        Assert.Contains("unavailable", result.Status.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USN is not available", result.Status.FallbackReason);
    }

    [Fact]
    public async Task Journal_reset_saves_new_checkpoint_and_requests_full_scan()
    {
        using var workspace = TemporaryWorkspace.Create();
        var checkpoint = new UsnJournalCheckpoint("docs", @"D:\", 2, 500, 1, DateTimeOffset.UtcNow);
        var service = CreateService(
            workspace,
            UsnChangeJournalReadResult.FullScanRequired("USN journal reset.", [checkpoint]));
        var configuration = NewConfiguration(workspace);
        Directory.CreateDirectory(configuration.WatchedFolders.Single().Path);

        var result = await service.CatchUpAsync(configuration);

        Assert.True(result.RequiresFullScan);
        Assert.Equal(checkpoint, Assert.Single(result.Checkpoints));
        Assert.Equal(checkpoint, Assert.Single(await service.CheckpointStore.LoadAsync()));
    }

    [Fact]
    public async Task Active_reader_returns_distinct_changed_file_paths()
    {
        using var workspace = TemporaryWorkspace.Create();
        var changed = Path.Combine(workspace.RootPath, "watched", "draft.txt");
        var checkpoint = new UsnJournalCheckpoint("docs", @"D:\", 2, 600, 1, DateTimeOffset.UtcNow);
        var service = CreateService(
            workspace,
            UsnChangeJournalReadResult.Active(
                "USN active.",
                [
                    new UsnChangedFile("docs", changed, 1, false),
                    new UsnChangedFile("docs", changed, 2, false)
                ],
                [checkpoint]));
        var configuration = NewConfiguration(workspace);
        Directory.CreateDirectory(configuration.WatchedFolders.Single().Path);

        var result = await service.CatchUpAsync(configuration);

        Assert.False(result.RequiresFullScan);
        Assert.Equal(changed, Assert.Single(result.ChangedFiles));
        Assert.Equal(checkpoint, Assert.Single(await service.CheckpointStore.LoadAsync()));
    }

    [Fact]
    public async Task Missing_enabled_watched_folder_requests_reconciliation_scan()
    {
        using var workspace = TemporaryWorkspace.Create();
        var service = CreateService(
            workspace,
            UsnChangeJournalReadResult.Active("Should not be called.", [], []));
        var configuration = NewConfiguration(workspace);

        var result = await service.CatchUpAsync(configuration);

        Assert.True(result.RequiresFullScan);
        Assert.Contains("reconciliation", result.Status.Status, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Status.FallbackReason);
    }

    private static TestService CreateService(TemporaryWorkspace workspace, UsnChangeJournalReadResult result)
    {
        var checkpointStore = new FileUsnJournalCheckpointStore(Path.Combine(workspace.RootPath, "state", "usn-checkpoints.json"));
        return new TestService(new UsnCatchUpService(new FakeUsnChangeJournalReader(result), checkpointStore), checkpointStore);
    }

    private static FluxVaultConfiguration NewConfiguration(TemporaryWorkspace workspace)
    {
        return new FluxVaultConfiguration(
            RepositoryPath: workspace.RepositoryPath,
            MirrorPath: null,
            IsEnabled: true,
            WatchedFolders:
            [
                new WatchedFolderConfiguration(
                    Id: "docs",
                    Path: Path.Combine(workspace.RootPath, "watched"),
                    Recursive: true,
                    IncludePatterns: ["*.txt"],
                    ExcludePatterns: [],
                    Compression: CompressionPreference.Zstd,
                    ResourceProfile: ResourceProfile.Fast,
                    IsEnabled: true)
            ]);
    }

    private sealed record TestService(UsnCatchUpService Service, FileUsnJournalCheckpointStore CheckpointStore)
    {
        public Task<UsnCatchUpResult> CatchUpAsync(FluxVaultConfiguration configuration)
        {
            return Service.CatchUpAsync(configuration);
        }
    }

    private sealed class FakeUsnChangeJournalReader(UsnChangeJournalReadResult result) : IUsnChangeJournalReader
    {
        public Task<UsnChangeJournalReadResult> ReadChangesAsync(
            IReadOnlyList<UsnWatchedFolderScope> watchedFolders,
            IReadOnlyList<UsnJournalCheckpoint> checkpoints,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }
}
