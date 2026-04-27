using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Core.ChangeTracking;

namespace FluxVault.Core.Tests;

public sealed class UsnJournalCheckpointStoreTests
{
    [Fact]
    public async Task Save_and_load_round_trips_checkpoints()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileUsnJournalCheckpointStore(Path.Combine(workspace.RootPath, "state", "usn-checkpoints.json"));
        var checkpoint = new UsnJournalCheckpoint(
            WatchedFolderId: "docs",
            VolumeRoot: @"D:\",
            JournalId: 42,
            NextUsn: 9001,
            ReasonMask: 0x80000001,
            CheckedAtUtc: new DateTimeOffset(2026, 4, 27, 10, 15, 0, TimeSpan.Zero));

        await store.SaveAsync([checkpoint]);

        var actual = await store.LoadAsync();
        Assert.Equal(checkpoint, Assert.Single(actual));
    }

    [Fact]
    public async Task Save_replaces_file_atomically_without_temporary_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var stateFile = Path.Combine(workspace.RootPath, "state", "usn-checkpoints.json");
        var store = new FileUsnJournalCheckpointStore(stateFile);
        await store.SaveAsync(
        [
            new UsnJournalCheckpoint("docs", @"D:\", 1, 100, 1, DateTimeOffset.UtcNow)
        ]);

        await store.SaveAsync(
        [
            new UsnJournalCheckpoint("docs", @"D:\", 2, 200, 2, DateTimeOffset.UtcNow)
        ]);

        var actual = Assert.Single(await store.LoadAsync());
        Assert.Equal((ulong)2, actual.JournalId);
        Assert.Equal((long)200, actual.NextUsn);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(stateFile)!, "*.tmp"));
    }
}
