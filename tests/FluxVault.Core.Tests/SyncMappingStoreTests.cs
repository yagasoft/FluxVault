using FluxVault.Abstractions.Sync;
using FluxVault.Core.Sync;

namespace FluxVault.Core.Tests;

public sealed class SyncMappingStoreTests
{
    [Fact]
    public async Task Propose_mapping_creates_pending_record_and_gate_blocks_hydration()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileSyncMappingStore(workspace.RepositoryPath);

        var mapping = await store.ProposeMappingAsync(
            sourceDeviceId: "device-laptop",
            sourcePath: @"D:\Work\Docs\brief.docx",
            localDeviceId: "device-local",
            proposedLocalPath: @"D:\Work\Docs\brief.docx");

        var stored = await store.ReadMappingAsync(mapping.MappingId);

        Assert.NotNull(stored);
        Assert.Equal(SyncMappingStatus.PendingConfirmation, stored.Status);
        Assert.Equal(@"D:\Work\Docs\brief.docx", stored.LocalPath);
        Assert.False(SyncMappingGate.CanHydrate(stored));
    }

    [Fact]
    public async Task Confirm_mapping_sets_local_path_and_gate_allows_hydration()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileSyncMappingStore(workspace.RepositoryPath);
        var mapping = await store.ProposeMappingAsync(
            "device-laptop",
            @"D:\Work\Docs\brief.docx",
            "device-local",
            proposedLocalPath: null);

        var confirmed = await store.ConfirmMappingAsync(
            mapping.MappingId,
            confirmedLocalPath: @"E:\Protected\brief.docx",
            confirmedByDeviceId: "device-local");

        Assert.Equal(SyncMappingStatus.Confirmed, confirmed.Status);
        Assert.Equal(@"E:\Protected\brief.docx", confirmed.LocalPath);
        Assert.Equal("device-local", confirmed.ConfirmedByDeviceId);
        Assert.NotNull(confirmed.ConfirmedAtUtc);
        Assert.True(SyncMappingGate.CanHydrate(confirmed));
    }

    [Fact]
    public async Task List_mappings_round_trips_multiple_records()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileSyncMappingStore(workspace.RepositoryPath);
        var first = await store.ProposeMappingAsync("device-a", @"D:\A\a.txt", "device-local", @"D:\A\a.txt");
        var second = await store.ProposeMappingAsync("device-b", @"D:\B\b.txt", "device-local", @"D:\B\b.txt");

        var mappings = await store.ListMappingsAsync();

        Assert.Equal([first.MappingId, second.MappingId], mappings.Select(mapping => mapping.MappingId).ToArray());
        Assert.All(mappings, mapping => Assert.Equal(SyncMappingStatus.PendingConfirmation, mapping.Status));
    }
}
