using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Capture;
using FluxVault.Core.ChangeTracking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Service;

namespace FluxVault.Integration.Tests;

public sealed class ProtectionLoopUsnTests
{
    [Fact]
    public async Task Usn_cycle_backs_up_only_changed_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        var changed = Path.Combine(watched, "changed.txt");
        var unchanged = Path.Combine(watched, "unchanged.txt");
        await File.WriteAllTextAsync(changed, "changed content");
        await File.WriteAllTextAsync(unchanged, "unchanged content");
        var configuration = NewConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        var loop = CreateLoop(
            workspace,
            operations,
            UsnChangeJournalReadResult.Active(
                "USN active.",
                [new UsnChangedFile("docs", changed, 1, false)],
                [new UsnJournalCheckpoint("docs", Path.GetPathRoot(watched)!, 1, 200, 1, DateTimeOffset.UtcNow)]));

        await loop.RunCatchUpCycleAsync();

        var version = Assert.Single(await operations.ListVersionsAsync());
        Assert.Equal(changed, version.SourcePath);
        var status = await operations.GetStatusAsync();
        Assert.NotNull(status.DurableChange);
        Assert.Equal("USN active.", status.DurableChange.Status);
    }

    [Fact]
    public async Task Usn_unavailable_cycle_falls_back_to_full_scan()
    {
        using var workspace = TemporaryWorkspace.Create();
        var watched = Path.Combine(workspace.RootPath, "watched");
        Directory.CreateDirectory(watched);
        await File.WriteAllTextAsync(Path.Combine(watched, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(watched, "second.txt"), "second");
        var configuration = NewConfiguration(workspace, watched);
        var operations = CreateOperations(workspace, configuration);
        await operations.SaveConfigurationAsync(configuration);
        var loop = CreateLoop(
            workspace,
            operations,
            UsnChangeJournalReadResult.Unavailable("USN journal cannot be queried."));

        await loop.RunCatchUpCycleAsync();

        var versions = await operations.ListVersionsAsync();
        Assert.Equal(2, versions.Count);
        var status = await operations.GetStatusAsync();
        Assert.NotNull(status.DurableChange);
        Assert.Contains("unavailable", status.DurableChange.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USN journal cannot be queried", status.DurableChange.FallbackReason);
    }

    private static FileSystemProtectionLoop CreateLoop(
        TemporaryWorkspace workspace,
        FluxVaultOperations operations,
        UsnChangeJournalReadResult result)
    {
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var checkpointStore = new FileUsnJournalCheckpointStore(Path.Combine(workspace.RootPath, "state", "usn-checkpoints.json"));
        return new FileSystemProtectionLoop(
            operations,
            store,
            new UsnCatchUpService(new FakeUsnChangeJournalReader(result), checkpointStore));
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
                    ExcludePatterns: [],
                    Compression: CompressionPreference.Zstd,
                    ResourceProfile: ResourceProfile.Fast,
                    IsEnabled: true)
            ]);
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
