using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class RepositoryHistoryPagingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serialised_byte_budget_keeps_nearest_page_and_anchors_only_emitted_boundaries(bool newer)
    {
        var id = VaultId.New();
        var query = new RepositoryHistoryQuery(id, PageSize: 8);
        var time = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(1, 8).Reverse().Select(i => new RepositoryVersionSummary(i.ToString("x32"), @"C:\Root\file",
            time.AddTicks(i), CaptureConsistency.BestEffort, 0, 0, InheritedFromSourcePath: new string('"', 300))).ToArray();
        if (newer) query = query with { Cursor = new(id, null, false, null, 1, time.UtcTicks, 0.ToString("x32"), HistoryPageDirection.Newer, 8) };
        var response = FluxVault.Abstractions.Ipc.FluxVaultIpcResponse.Ok() with { VaultId = id, VaultRevision = 10,
            HistoryPage = RepositoryHistoryPaging.Page(query, 1, rows, true, true) };
        var bounded = RepositoryHistoryPaging.BoundResponse(response, 7000);
        Assert.True(bounded.Success);
        var page = Assert.IsType<RepositoryHistoryPage>(bounded.HistoryPage);
        Assert.InRange(page.Versions.Count, 1, 7);
        Assert.Equal(newer ? rows[^1].VersionId : rows[0].VersionId, newer ? page.Versions[^1].VersionId : page.Versions[0].VersionId);
        Assert.Equal(page.Versions[^1].VersionId, page.OlderCursor!.VersionId);
        Assert.Equal(page.Versions[0].VersionId, page.NewerCursor!.VersionId);
        Assert.InRange(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(bounded, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)).Length, 1, 7000);
    }

    [Fact]
    public void Oversized_single_item_returns_clean_failure_with_no_partial_page()
    {
        var id = VaultId.New();
        var row = new RepositoryVersionSummary(1.ToString("x32"), @"C:\Root\file", DateTimeOffset.UtcNow, CaptureConsistency.BestEffort, 0, 0,
            InheritedFromSourcePath: new string('"', 1000));
        var response = FluxVault.Abstractions.Ipc.FluxVaultIpcResponse.Ok() with { VaultId = id, VaultRevision = 1,
            HistoryPage = RepositoryHistoryPaging.Page(new(id), 1, [row], false, false) };
        var bounded = RepositoryHistoryPaging.BoundResponse(response, 1500);
        Assert.False(bounded.Success); Assert.Null(bounded.HistoryPage); Assert.Null(bounded.SnapshotPage); Assert.Equal(id, bounded.VaultId);
    }
    [Fact]
    public async Task Pages_preserve_exact_ties_scope_and_bidirectional_navigation_then_refuse_changed_history()
    {
        IRepositoryMetadataStore store = new InMemoryRepositoryMetadataStore();
        var id = VaultId.New();
        var time = DateTimeOffset.UtcNow;
        var manifests = Enumerable.Range(1, 9).Select(i => new FileVersionManifest(i.ToString("x32"), "root",
            Path.GetFullPath(i == 9 ? @"C:\Other\file" : @"C:\Root\file"), time.AddTicks(i / 2),
            CaptureConsistency.BestEffort, 0, [], VaultId: id)).ToArray();
        await store.RecordVersionsAsync(manifests);
        var query = new RepositoryHistoryQuery(id, @"C:\Root", true, PageSize: 3);
        var first = await store.ListHistoryPageAsync(query);
        Assert.Equal(new[] { manifests[7].VersionId, manifests[6].VersionId, manifests[5].VersionId }, first.Versions.Select(v => v.VersionId));
        var second = await store.ListHistoryPageAsync(query with { Cursor = first.OlderCursor });
        var back = await store.ListHistoryPageAsync(query with { Cursor = second.NewerCursor });
        Assert.Equal(first.Versions, back.Versions);
        var third = await store.ListHistoryPageAsync(query with { Cursor = second.OlderCursor });
        Assert.Null(third.OlderCursor);
        Assert.Equal(8, first.Versions.Concat(second.Versions).Concat(third.Versions).Select(v => v.VersionId).Distinct().Count());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListHistoryPageAsync(query with { SourcePath = @"C:\Other", Cursor = first.OlderCursor }));
        await store.DeleteVersionsAsync([manifests[0].VersionId]);
        await Assert.ThrowsAsync<RepositoryHistoryChangedException>(() => store.ListHistoryPageAsync(query with { Cursor = first.OlderCursor }));
    }

    [Fact]
    public async Task Snapshot_pages_are_bound_to_recorded_immutable_version_and_not_latest_child()
    {
        IRepositoryMetadataStore store = new InMemoryRepositoryMetadataStore();
        var id = VaultId.New();
        var entries = Enumerable.Range(1, 5).Reverse().Select(i => new FolderVersionEntry($"file{i}", @"C:\Root\file" + i,
            RepositoryEntryKind.File, i.ToString("x32"), false, 0, DateTimeOffset.UtcNow)).ToArray();
        var manifest = new FileVersionManifest(10.ToString("x32"), "root", @"C:\Root", DateTimeOffset.UtcNow,
            CaptureConsistency.BestEffort, 0, [], EntryKind: RepositoryEntryKind.Folder, FolderEntries: entries, VaultId: id);
        await store.RecordVersionAsync(manifest);
        var query = new RepositorySnapshotQuery(id, manifest.VersionId, PageSize: 2);
        var page = await store.GetSnapshotPageAsync(query);
        Assert.Equal(new[] { "file1", "file2" }, page.Entries.Select(e => e.Name));
        Assert.Equal(2, page.NextOffset);
        Assert.Null(page.Version.FolderEntries);
        var next = await store.GetSnapshotPageAsync(query with { Offset = page.NextOffset!.Value });
        Assert.Equal(new[] { "file3", "file4" }, next.Entries.Select(e => e.Name));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetSnapshotPageAsync(query with { RepositoryId = VaultId.New() }));
        await store.DeleteVersionsAsync([manifest.VersionId]);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.GetSnapshotPageAsync(query));
    }
}
