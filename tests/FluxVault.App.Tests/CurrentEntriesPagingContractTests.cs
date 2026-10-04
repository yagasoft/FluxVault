using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class CurrentEntriesPagingContractTests
{
    [Fact]
    public async Task Complete_pages_publish_once_and_preserve_deleted_files_and_pending_edits()
    {
        var client = new Client(); var model = Model(client);
        await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        Assert.Equal(new[] { "next.dwg", "old.dwg" }, model.FileBrowser.Files.Select(f => f.Name).Order());
        Assert.Equal(2, client.Queries.Count);
        Assert.All(client.Queries, r => { Assert.Equal(client.Id, r.VaultId); Assert.Equal(9, r.ExpectedVaultRevision); });
        Assert.Equal(1, client.Queries[1].CurrentEntriesQuery!.Cursor!.PathId);
        model.RepositoryPath = @"C:\PendingDraft";
        client.NewSnapshot = true;
        await model.RefreshAsync();
        Assert.Equal(@"C:\PendingDraft", model.RepositoryPath);
        Assert.Equal(new[] { "new.dwg" }, model.FileBrowser.Files.Select(f => f.Name));
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("changed")]
    [InlineData("null")]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("identity")]
    [InlineData("duplicate_path")]
    [InlineData("duplicate_id")]
    [InlineData("cursor")]
    [InlineData("echo")]
    [InlineData("cancelled")]
    [InlineData("json")]
    public async Task Incomplete_or_invalid_refresh_retains_the_previous_complete_browser_and_draft(string failure)
    {
        var client = new Client(); var model = Model(client);
        await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        Assert.Equal(2, model.FileBrowser.Files.Count);
        model.RepositoryPath = @"C:\PendingDraft";
        client.Failure = failure;
        await model.RefreshAsync();
        Assert.Equal(@"C:\PendingDraft", model.RepositoryPath);
        Assert.Equal(new[] { "next.dwg", "old.dwg" }, model.FileBrowser.Files.Select(f => f.Name).Order());
        Assert.Contains("inventory", model.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Queries.Select(r => r.Command));
    }

    [Fact]
    public async Task Authoritative_empty_page_clears_prior_browser_only_after_successful_completion()
    {
        var client = new Client(); var model = Model(client); await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single()); Assert.Equal(2, model.FileBrowser.Files.Count);
        client.Empty = true; await model.RefreshAsync(); Assert.Empty(model.FileBrowser.Files);
    }

    [Fact]
    public async Task Short_pages_with_continuations_are_not_treated_as_complete()
    {
        var client = new Client { PageSize = 3 }; var model = Model(client); await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        Assert.Equal(2, client.Queries.Count); Assert.Equal(2, model.FileBrowser.Files.Count);
    }

    [Fact]
    public async Task Edits_and_previous_entries_survive_while_a_new_sweep_is_in_flight()
    {
        var client = new Client(); var model = Model(client); await model.RefreshAsync();
        model.FileBrowser.SelectFolder(model.FileBrowser.Roots.Single());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforePage = async (query, token) => { if (query.Cursor is not null) { started.TrySetResult(); await release.Task.WaitAsync(token); } };
        var refresh = model.RefreshAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, model.FileBrowser.Files.Count);
            model.RepositoryPath = @"C:\NewerDraft";
        }
        finally { release.TrySetResult(); }
        await refresh;
        Assert.Equal(@"C:\NewerDraft", model.RepositoryPath);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("startup")]
    [InlineData("automatic")]
    public async Task Shutdown_cancels_and_joins_paging_and_refuses_new_refreshes(string entryPoint)
    {
        var client = new Client(); var model = Model(client,TimeSpan.FromMilliseconds(20));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforePage = async (query, token) =>
        {
            if (query.Cursor is null) return;
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); await release.Task; throw; }
        };
        var refresh = entryPoint=="startup" ? model.ApplyStartupRequestAsync(new(AppStartupRequestAction.ShowVersions,@"C:\Work\old.dwg")) :
            entryPoint=="manual" ? model.RefreshAsync() : Task.CompletedTask;
        if(entryPoint=="automatic")model.StartAutoRefresh();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = model.StopRepositoryReadsAsync();
        try
        {
            Assert.Same(stop, model.StopRepositoryReadsAsync());
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted); if(entryPoint!="automatic")Assert.False(refresh.IsCompleted);
            var requests = client.Queries.Count; await model.RefreshAsync(); Assert.Equal(requests, client.Queries.Count);
        }
        finally { release.TrySetResult(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(5)); await refresh;
        Assert.True(model.StopRepositoryReadsAsync().IsCompleted);
    }

    private static MainWindowViewModel Model(Client client,TimeSpan? refreshInterval=null) => new(client, refreshInterval??TimeSpan.FromHours(1),
        new FileBrowserViewModel(new Files()), new Controller(), new Destination(), new Overwrite());
    private sealed class Files : IFileBrowserFileSystem
    {
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots() => [new(@"C:\Work", "Work", true, null)];
        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path) => [];
        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path) => [];
    }
    private sealed class Client : IFluxVaultServiceClient
    {
        public VaultId Id { get; } = VaultId.New();
        public List<FluxVaultIpcRequest> Queries { get; } = [];
        public bool NewSnapshot { get; set; }
        public bool Empty { get; set; }
        public string? Failure { get; set; }
        public int PageSize { get; set; } = 1;
        public Func<RepositoryCurrentEntriesQuery,CancellationToken,Task>? BeforePage { get; set; }
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            FluxVaultIpcResponse response;
            if (request.Command == FluxVaultIpcCommand.GetStatus)
                response = FluxVaultIpcResponse.WithStatus(new(true, FluxVaultConfiguration.CreateDefault(@"C:\Repository") with
                    { RepositoryBrowse = new(PageSize) }, "Fixture", null, [], [], UsesPagedCurrentEntries: true));
            else if (request.Command == FluxVaultIpcCommand.ListCurrentEntriesPage)
            {
                Queries.Add(request); var query = request.CurrentEntriesQuery!;
                if (BeforePage is not null) await BeforePage(query,cancellationToken);
                var second = query.Cursor is not null;
                if (second && Failure == "cancelled") throw new OperationCanceledException("Cancelled fixture");
                if (second && Failure == "json") System.Text.Json.JsonSerializer.Deserialize<FluxVaultIpcResponse>("{ malformed }");
                if (second && Failure is "denied" or "changed")
                    return FluxVaultIpcResponse.Failure("Current inventory " + Failure) with
                        { ErrorCode = Failure == "changed" ? FluxVaultIpcErrorCode.HistoryChanged : FluxVaultIpcErrorCode.Denied };
                var rowId = second ? 2 : 1;
                var name = NewSnapshot ? "new.dwg" : second ? "next.dwg" : "old.dwg";
                var row = new RepositoryCurrentEntry(rowId, new(rowId.ToString("x32"), @"C:\Work\" + name,
                    DateTimeOffset.UtcNow, CaptureConsistency.BestEffort, 0, 0, IsDeleted: true));
                if (second && Failure == "duplicate_path") row = row with { Version = row.Version with { SourcePath = @"c:\WORK\OLD.DWG" } };
                if (second && Failure == "duplicate_id") row = row with { PathId = 1 };
                var page = new RepositoryCurrentEntriesPage(query, second && Failure == "generation" ? 2 : 1,
                    Empty ? [] : [row], second || NewSnapshot || Empty ? null : new(Id, query.PageSize, 1, 1));
                if (second && Failure == "cursor") page = page with { NextCursor = new(Id, 1, 1, 1) };
                if (second && Failure == "echo") page = page with { Query = query with { PageSize = 2 } };
                response = FluxVaultIpcResponse.Ok() with { CurrentEntriesPage = second && Failure == "null" ? null : page };
                if (second && Failure == "revision") return response with { VaultId = Id, VaultRevision = 10 };
                if (second && Failure == "identity") return response with { VaultId = VaultId.New(), VaultRevision = 9 };
            }
            else throw new InvalidOperationException("Unexpected command " + request.Command);
            return response with { VaultId = Id, VaultRevision = 9 };
        }
    }
    private sealed class Controller : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FluxVaultWindowsServiceStatus("Fixture", FluxVaultWindowsServiceState.Running, "Fixture"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class Destination : IRestoreDestinationPicker { public string? PickDestination(VersionRow version) => throw new InvalidOperationException(); }
    private sealed class Overwrite : IRestoreOverwriteConfirmation { public bool ConfirmOverwrite(string destinationPath) => throw new InvalidOperationException(); }
}
