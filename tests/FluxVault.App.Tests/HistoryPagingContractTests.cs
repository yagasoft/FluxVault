using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.App.Tests;

public sealed class HistoryPagingContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Initial_unavailable_or_denied_status_does_not_escape_history_entry_points(bool startup, bool denied)
    {
        var client = new HistoryClient { StatusFailure = denied ? "denied" : "offline" };
        var model = new MainWindowViewModel(client, TimeSpan.FromHours(1), new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new FixtureController(), new NoDestination(), new NoOverwrite());
        model.FileBrowser.SelectedFile = new(client.Path, "drawing.dwg", 0, true);
        var requested = false;
        model.VersionInventoryRequested += (_, _) => requested = true;

        if (startup) await model.ApplyStartupRequestAsync(new(AppStartupRequestAction.ShowVersions, client.Path));
        else await model.ShowSelectedBrowserItemVersionsCommand.ExecuteAsync(null);

        Assert.False(requested);
        Assert.DoesNotContain(FluxVaultIpcCommand.ListHistoryPage, client.Commands);
        Assert.DoesNotContain(FluxVaultIpcCommand.GetSnapshotPage, client.Commands);
        Assert.Contains("History unavailable", model.ServiceStatus);
        Assert.DoesNotContain("no restorable versions", model.ServiceStatus);
    }

    [Fact]
    public async Task Older_snapshot_response_cannot_replace_a_new_selection()
    {
        var id = VaultId.New();
        var query = RepositoryHistoryPaging.Validate(new RepositoryHistoryQuery(id, @"C:\History", true));
        var now = DateTimeOffset.UtcNow;
        var rows = new[] { 2, 1 }.Select(i => new RepositoryVersionSummary(i.ToString("x32"), @"C:\History", now.AddTicks(i),
            CaptureConsistency.BestEffort, 0, 0, EntryKind: RepositoryEntryKind.Folder)).ToArray();
        var delayed = new TaskCompletionSource<FluxVaultIpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedQuery = new RepositorySnapshotQuery(id, rows[0].VersionId);
        FluxVaultIpcResponse Snapshot(RepositorySnapshotQuery q) => FluxVaultIpcResponse.Ok() with { SnapshotPage = new(q,
            rows.Single(r => r.VersionId == q.VersionId), [new(q.VersionId, @"C:\History\" + q.VersionId, RepositoryEntryKind.File,
                3.ToString("x32"), false, 0, now)], null) };
        await using var inventory = new VersionInventoryViewModel(@"C:\History", query,
            (q, _) => Task.FromResult(FluxVaultIpcResponse.Ok() with { HistoryPage = RepositoryHistoryPaging.Page(q, 1, rows, false, false) }),
            (q, _) => { if (q.VersionId != rows[0].VersionId) return Task.FromResult(Snapshot(q)); delayedQuery = q; entered.TrySetResult(); return delayed.Task; },
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        var load = inventory.InitialiseAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { inventory.SelectedVersion = inventory.Versions[1]; }
        finally { delayed.TrySetResult(Snapshot(delayedQuery)); await load.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(rows[1].VersionId, Assert.Single(inventory.SnapshotEntries).Name);
        Assert.Contains(rows[1].VersionId, inventory.SnapshotStatus);
    }

    [Fact]
    public async Task Recorded_child_recovery_fetches_the_exact_old_version_outside_the_visible_page_and_refuses_pruned_child()
    {
        var id = VaultId.New();
        IRepositoryMetadataStore store = new InMemoryRepositoryMetadataStore();
        var now = DateTimeOffset.UtcNow;
        var old = new FileVersionManifest(1.ToString("x32"), "root", @"C:\History\file.dwg", now.AddDays(-1), CaptureConsistency.BestEffort, 0, [], VaultId: id);
        var newer = old with { VersionId = 2.ToString("x32"), CapturedAtUtc = now };
        var folder = new FileVersionManifest(3.ToString("x32"), "root", @"C:\History", now.AddTicks(1), CaptureConsistency.BestEffort, 0, [],
            EntryKind: RepositoryEntryKind.Folder, FolderEntries: [new("file.dwg", old.SourcePath, RepositoryEntryKind.File, old.VersionId, false, 0, old.CapturedAtUtc)], VaultId: id);
        await store.RecordVersionsAsync([old, newer, folder]);
        var restored = new List<string>();
        await using var inventory = new VersionInventoryViewModel(@"C:\History", new(id, @"C:\History", true, PageSize: 1),
            async (q, ct) => FluxVaultIpcResponse.Ok() with { HistoryPage = await store.ListHistoryPageAsync(q, ct) },
            async (q, ct) => FluxVaultIpcResponse.Ok() with { SnapshotPage = await store.GetSnapshotPageAsync(q, ct) },
            row => { restored.Add(row.VersionId); return Task.CompletedTask; }, _ => Task.CompletedTask);
        await inventory.InitialiseAsync();
        Assert.Equal(folder.VersionId, Assert.Single(inventory.Versions).VersionId);
        inventory.SelectedSnapshotEntry = Assert.Single(inventory.SnapshotEntries);
        await inventory.RestoreSelectedSnapshotEntryCommand.ExecuteAsync(null);
        Assert.Equal(old.VersionId, Assert.Single(restored));
        await store.DeleteVersionsAsync([old.VersionId]);
        await inventory.RestoreSelectedSnapshotEntryCommand.ExecuteAsync(null);
        Assert.Single(restored);
        Assert.Contains("Recorded child unavailable", inventory.SnapshotStatus);
    }
    [Theory]
    [InlineData("null-collection")]
    [InlineData("null-item")]
    [InlineData("wrong-cursor-field")]
    [InlineData("overlap")]
    public async Task Malformed_following_page_is_refused_and_keeps_the_existing_page(string defect)
    {
        var id = VaultId.New();
        var query = RepositoryHistoryPaging.Validate(new RepositoryHistoryQuery(id, @"C:\History", true, PageSize: 2));
        var captured = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(1, 3).Reverse().Select(v => new RepositoryVersionSummary(v.ToString("x32"),
            @"C:\History\file.dwg", captured.AddTicks(v), CaptureConsistency.BestEffort, 0, 0)).ToArray();
        await using var inventory = new VersionInventoryViewModel(@"C:\History", query,
            (q, _) =>
            {
                var page = RepositoryHistoryPaging.Page(q, 1, q.Cursor is null ? rows.Take(2).ToArray() : rows.Skip(2).ToArray(), q.Cursor is null, q.Cursor is not null);
                if (q.Cursor is not null) page = defect switch
                {
                    "null-collection" => page with { Versions = null! },
                    "null-item" => page with { Versions = [null!] },
                    "wrong-cursor-field" => page with { NewerCursor = RepositoryHistoryPaging.Page(q, 1, page.Versions, true, false).OlderCursor },
                    _ => RepositoryHistoryPaging.Page(q, 1, rows.Take(2).ToArray(), true, true)
                };
                return Task.FromResult(FluxVaultIpcResponse.Ok() with { HistoryPage = page });
            },
            (q, _) => Task.FromResult(FluxVaultIpcResponse.Ok() with { SnapshotPage = new(q, rows.Single(v => v.VersionId == q.VersionId), [], null) }),
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        await inventory.InitialiseAsync();
        var original = inventory.Versions.ToArray();
        await inventory.LoadOlderCommand.ExecuteAsync(null);
        Assert.Equal(original, inventory.Versions);
        Assert.Contains("could not be loaded", inventory.HistoryStatus);
    }
    [Fact]
    public async Task Exact_identifier_ties_can_replace_a_page_without_partial_application()
    {
        var id = VaultId.New();
        var query = RepositoryHistoryPaging.Validate(new RepositoryHistoryQuery(id, @"C:\History", true));
        var captured = DateTimeOffset.UtcNow;
        var rows = new[] { "a" + new string('0', 31), "A" + new string('0', 31) }.Select(v => new RepositoryVersionSummary(v,
            @"C:\History\file.dwg", captured, CaptureConsistency.BestEffort, 0, 0)).ToArray();
        await using var inventory = new VersionInventoryViewModel(@"C:\History", query,
            (q, _) => Task.FromResult(FluxVaultIpcResponse.Ok() with { HistoryPage = RepositoryHistoryPaging.Page(q, 1, rows, false, false) }),
            (q, _) => Task.FromResult(FluxVaultIpcResponse.Ok() with { SnapshotPage = new(q, rows.Single(v => v.VersionId == q.VersionId), [], null) }),
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        await inventory.InitialiseAsync();
        Assert.Equal(rows.Select(v => v.VersionId), inventory.Versions.Select(v => v.VersionId));
        Assert.NotNull(inventory.SelectedVersion);
        Assert.StartsWith("Showing", inventory.HistoryStatus);
    }

    [Fact]
    public async Task Repeated_disposal_waits_for_the_same_outstanding_read_join()
    {
        var client = new HistoryClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeSnapshot = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var inventory = new VersionInventoryViewModel(client.Path, new(client.Id, client.Path), client.ReadHistory, client.ReadSnapshot,
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        var load = inventory.InitialiseAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = inventory.DisposeAsync().AsTask();
        var second = inventory.DisposeAsync().AsTask();
        try { Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(); await Task.WhenAll(first, second, load).WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Fact]
    public async Task Actual_recovery_flow_does_not_request_unbounded_history()
    {
        var client = new HistoryClient();
        var model = new MainWindowViewModel(client, TimeSpan.FromHours(1),
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new FixtureController(), new NoDestination(), new NoOverwrite());
        await model.RefreshAsync();
        await using var inventory = await model.CreateVersionInventoryForPathAsync(client.Path);
        Assert.DoesNotContain(FluxVaultIpcCommand.ListVersions, client.Commands);
        Assert.InRange(inventory.Versions.Count, 1, 100);
        var first = inventory.Versions.Select(v => v.VersionId).ToArray();
        await inventory.LoadOlderCommand.ExecuteAsync(null);
        Assert.Equal(100, inventory.Versions.Count);
        Assert.Empty(first.Intersect(inventory.Versions.Select(v => v.VersionId)));
        await inventory.LoadNewerCommand.ExecuteAsync(null);
        Assert.Equal(first, inventory.Versions.Select(v => v.VersionId));
    }

    [Fact]
    public async Task Actual_flow_uses_saved_page_size_and_keeps_rows_on_failure_or_changed_history()
    {
        var client = new HistoryClient { PageSize = 7 };
        var model = new MainWindowViewModel(client, TimeSpan.FromHours(1), new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new FixtureController(), new NoDestination(), new NoOverwrite());
        await model.RefreshAsync();
        await using var inventory = await model.CreateVersionInventoryForPathAsync(client.Path);
        Assert.Equal(7, inventory.Versions.Count);
        var original = inventory.Versions.ToArray();
        client.FailHistory = true;
        await inventory.LoadOlderCommand.ExecuteAsync(null);
        Assert.Equal(original, inventory.Versions);
        Assert.Contains("could not be loaded", inventory.HistoryStatus);
        client.FailHistory = false;
        await client.Store.DeleteVersionsAsync([205.ToString("x32")]);
        await inventory.LoadOlderCommand.ExecuteAsync(null);
        Assert.Equal(original, inventory.Versions);
        Assert.Contains("History changed", inventory.HistoryStatus);
        await inventory.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.DoesNotContain("History changed", inventory.HistoryStatus);
    }

    [Fact]
    public async Task Failed_initial_load_is_not_presented_as_authoritative_empty_history()
    {
        var client = new HistoryClient { FailHistory = true };
        var model = new MainWindowViewModel(client, TimeSpan.FromHours(1), new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
            new FixtureController(), new NoDestination(), new NoOverwrite());
        await model.RefreshAsync();
        await using var inventory = await model.CreateVersionInventoryForPathAsync(client.Path);
        Assert.False(inventory.HasAuthoritativeHistory);
        Assert.Contains("could not be loaded", inventory.HistoryStatus);
        Assert.Empty(inventory.Versions);
    }

    [Fact]
    public async Task Disposal_cancels_and_joins_pending_actual_snapshot_flow()
    {
        var client = new HistoryClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeSnapshot = async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } finally { exited.TrySetResult(); }
        };
        var inventory = new VersionInventoryViewModel(client.Path, new(client.Id, client.Path), client.ReadHistory, client.ReadSnapshot,
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        var load = inventory.InitialiseAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await inventory.DisposeAsync();
        await load;
        Assert.True(exited.Task.IsCompletedSuccessfully);
        Assert.Empty(inventory.SnapshotEntries);
        Assert.False(inventory.CanLoadOlder);
    }

    private sealed class HistoryClient : IFluxVaultServiceClient
    {
        public string Path { get; } = System.IO.Path.GetFullPath(@"C:\History\drawing.dwg");
        public List<FluxVaultIpcCommand> Commands { get; } = [];
        private readonly VaultId id = VaultId.New();
        public VaultId Id => id;
        public IRepositoryMetadataStore Store { get; } = new InMemoryRepositoryMetadataStore();
        public int PageSize { get; set; } = 100;
        public bool FailHistory { get; set; }
        public string? StatusFailure { get; set; }
        public Func<RepositorySnapshotQuery, CancellationToken, Task>? BeforeSnapshot { get; set; }
        public HistoryClient()
        {
            var now = DateTimeOffset.UtcNow;
            Store.RecordVersionsAsync(Enumerable.Range(1, 205).Select(i => new FileVersionManifest(i.ToString("x32"), "root", Path,
                now.AddMinutes(-i), CaptureConsistency.BestEffort, 0, [], VaultId: id)).ToArray()).GetAwaiter().GetResult();
        }
        public Task<FluxVaultIpcResponse> ReadHistory(RepositoryHistoryQuery query, CancellationToken token) =>
            SendAsync(new(FluxVaultIpcCommand.ListHistoryPage, null, null, null, null, HistoryQuery: query), token);
        public Task<FluxVaultIpcResponse> ReadSnapshot(RepositorySnapshotQuery query, CancellationToken token) =>
            SendAsync(new(FluxVaultIpcCommand.GetSnapshotPage, null, null, null, null, SnapshotQuery: query), token);
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            if (request.Command == FluxVaultIpcCommand.GetStatus && StatusFailure is { } failure)
            {
                if (failure == "offline") throw new IOException("Fixture offline");
                return FluxVaultIpcResponse.Failure("Fixture denied") with { ErrorCode = FluxVaultIpcErrorCode.Denied };
            }
            var response = request.Command == FluxVaultIpcCommand.GetStatus
                ? FluxVaultIpcResponse.WithStatus(new FluxVaultServiceStatus(true,
                    FluxVaultConfiguration.CreateDefault(@"C:\HistoryRepository") with { RepositoryBrowse = new(PageSize) }, "Fixture", null, [], []))
                : FluxVaultIpcResponse.WithVersions([]);
            if (request.HistoryQuery is { } query)
            {
                if (FailHistory) response = FluxVaultIpcResponse.Failure("Fixture unavailable");
                else
                {
                    try { response = FluxVaultIpcResponse.Ok() with { HistoryPage = await Store.ListHistoryPageAsync(query, cancellationToken) }; }
                    catch (RepositoryHistoryChangedException) { response = FluxVaultIpcResponse.Failure("Changed") with { ErrorCode = FluxVaultIpcErrorCode.HistoryChanged }; }
                }
            }
            if (request.SnapshotQuery is { } snapshot)
            {
                if (BeforeSnapshot is not null) await BeforeSnapshot(snapshot, cancellationToken);
                response = FluxVaultIpcResponse.Ok() with { SnapshotPage = await Store.GetSnapshotPageAsync(snapshot, cancellationToken) };
            }
            return response with { VaultId = id, VaultRevision = 1 };
        }
    }
    private sealed class FixtureController : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FluxVaultWindowsServiceStatus("Fixture", FluxVaultWindowsServiceState.Running, "Fixture"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class NoDestination : IRestoreDestinationPicker
    {
        public string? PickDestination(VersionRow version) => throw new InvalidOperationException();
    }
    private sealed class NoOverwrite : IRestoreOverwriteConfirmation
    {
        public bool ConfirmOverwrite(string destinationPath) => throw new InvalidOperationException();
    }
}
