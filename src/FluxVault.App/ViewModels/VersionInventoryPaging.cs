using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.App.ViewModels;

public sealed partial class VersionInventoryViewModel : IAsyncDisposable
{
    private RepositoryHistoryQuery? historyQuery;
    private RepositoryHistoryPage? historyPage;
    private RepositorySnapshotPage? snapshotPage;
    private Func<RepositoryHistoryQuery, CancellationToken, Task<FluxVaultIpcResponse>>? readHistory;
    private Func<RepositorySnapshotQuery, CancellationToken, Task<FluxVaultIpcResponse>>? readSnapshot;
    private readonly CancellationTokenSource readLifetime = new();
    private readonly List<Task> activeReads = [];
    private int snapshotRequest;
    private bool closed;
    private Task latestSnapshotRead = Task.CompletedTask;
    private Task? disposalTask;

    public VersionInventoryViewModel(string path, RepositoryHistoryQuery query,
        Func<RepositoryHistoryQuery, CancellationToken, Task<FluxVaultIpcResponse>> readHistory,
        Func<RepositorySnapshotQuery, CancellationToken, Task<FluxVaultIpcResponse>> readSnapshot,
        Func<VersionInventoryVersionRow, Task> restore, Func<VersionInventoryVersionRow, Task> preview)
        : this(path, [], restore, preview)
    {
        historyQuery = RepositoryHistoryPaging.Validate(query);
        this.readHistory = readHistory;
        this.readSnapshot = readSnapshot;
    }

    public bool IsPaged => historyQuery is not null;
    public bool HasAuthoritativeHistory => !IsPaged || historyPage is not null;
    public bool CanLoadOlder => !closed && !IsHistoryBusy && historyPage?.OlderCursor is not null;
    public bool CanLoadNewer => !closed && !IsHistoryBusy && historyPage?.NewerCursor is not null;
    public bool CanRefreshHistory => !closed && !IsHistoryBusy && IsPaged;
    public bool CanLoadMoreEntries => !closed && !IsSnapshotBusy && snapshotPage?.NextOffset is not null;
    public bool CanFirstEntries => !closed && !IsSnapshotBusy && snapshotPage?.Query.Offset > 0;

    [ObservableProperty] private bool isHistoryBusy;
    [ObservableProperty] private bool isSnapshotBusy;
    [ObservableProperty] private string historyStatus = string.Empty;
    [ObservableProperty] private string snapshotStatus = string.Empty;
    [ObservableProperty] private string snapshotPath = string.Empty;

    partial void OnIsHistoryBusyChanged(bool value) => NotifyPageCommands();
    partial void OnIsSnapshotBusyChanged(bool value) => NotifyPageCommands();
    private void NotifyPageCommands()
    {
        LoadOlderCommand.NotifyCanExecuteChanged(); LoadNewerCommand.NotifyCanExecuteChanged();
        RefreshHistoryCommand.NotifyCanExecuteChanged(); MoreEntriesCommand.NotifyCanExecuteChanged(); FirstEntriesCommand.NotifyCanExecuteChanged();
    }

    public Task InitialiseAsync() => TrackRead(LoadHistoryAsync(null));
    [RelayCommand(CanExecute = nameof(CanLoadOlder))]
    private Task LoadOlderAsync() => TrackRead(LoadHistoryAsync(historyPage?.OlderCursor));
    [RelayCommand(CanExecute = nameof(CanLoadNewer))]
    private Task LoadNewerAsync() => TrackRead(LoadHistoryAsync(historyPage?.NewerCursor));
    [RelayCommand(CanExecute = nameof(CanRefreshHistory))]
    private Task RefreshHistoryAsync() => TrackRead(LoadHistoryAsync(null));
    [RelayCommand(CanExecute = nameof(CanLoadMoreEntries))]
    private Task MoreEntriesAsync() => StartSnapshotAsync(snapshotPage!.Version, snapshotPage.NextOffset!.Value);
    [RelayCommand(CanExecute = nameof(CanFirstEntries))]
    private Task FirstEntriesAsync() => StartSnapshotAsync(snapshotPage!.Version, 0);
    [RelayCommand]
    private Task SelectedSnapshotAsync() => SelectedVersion is null ? Task.CompletedTask : StartSnapshotAsync(SelectedVersion);

    private Task TrackRead(Task task)
    {
        activeReads.RemoveAll(t => t.IsCompleted);
        activeReads.Add(task);
        return task;
    }
    private async Task LoadHistoryAsync(RepositoryHistoryCursor? cursor)
    {
        if (closed || readHistory is null || historyQuery is null || IsHistoryBusy) return;
        IsHistoryBusy = true; HistoryStatus = "Loading history…";
        try
        {
            var query = historyQuery with { Cursor = cursor };
            var response = await readHistory(query, readLifetime.Token).ConfigureAwait(true);
            if (closed) return;
            if (!response.Success || response.HistoryPage is not { } page)
            {
                HistoryStatus = response.ErrorCode == FluxVaultIpcErrorCode.HistoryChanged
                    ? "History changed. Refresh to browse the current history; the displayed page is kept."
                    : $"History could not be loaded: {response.ErrorMessage ?? "no page returned"}. Try Refresh; the displayed page is kept.";
                return;
            }
            ValidateHistoryResponse(query, page);
            var replacement = page.Versions.Select(ToVersionRow).ToArray();
            var replacementIndex = replacement.ToDictionary(r => r.VersionId, StringComparer.Ordinal);
            var replacementFiles = replacement.Where(v => v.EntryKind == RepositoryEntryKind.File)
                .GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(group =>
                {
                    var latest = group.First();
                    return new VersionInventoryFileRow(latest.File, latest.Path, latest.VersionId, latest.State, latest.CapturedAt, latest.Bytes, group.ToArray());
                }).ToArray();
            // Validate and prepare the complete replacement before changing displayed state.
            historyPage = page;
            snapshotRequest++;
            SelectedVersion = null;
            Versions.Clear(); Files.Clear(); versionsById.Clear();
            // Preserve the service's exact UTC tick/ordinal order; display strings are not keys.
            foreach (var row in replacement) Versions.Add(row);
            foreach (var item in replacementIndex) versionsById.Add(item.Key, item.Value);
            foreach (var item in replacementFiles) Files.Add(item);
            HistoryStatus = page.Versions.Count == 0 ? "No history found for this selection." : $"Showing {page.Versions.Count} version{(page.Versions.Count == 1 ? "" : "s")}. Use Older or Newer to browse.";
            OnPropertyChanged(nameof(HasAuthoritativeHistory));
            SelectedVersion = Versions.FirstOrDefault();
            await latestSnapshotRead.ConfigureAwait(true);
        }
        catch (OperationCanceledException) { if (!closed) HistoryStatus = "History loading was cancelled. The displayed page is kept."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { if (!closed) HistoryStatus = $"History could not be loaded: {ex.Message}. Try Refresh; the displayed page is kept."; }
        finally { IsHistoryBusy = false; NotifyPageCommands(); }
    }
    private static void ValidateHistoryResponse(RepositoryHistoryQuery query, RepositoryHistoryPage page)
    {
        if (page.Query != query || page.Generation < 0 || page.Versions is null || page.Versions.Count > query.PageSize ||
            query.Cursor is not null && page.Generation != query.Cursor.Generation)
            throw new InvalidDataException("The returned history page does not match this request.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        RepositoryVersionSummary? previous = null;
        foreach (var row in page.Versions)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.SourcePath) || !Path.IsPathFullyQualified(row.SourcePath) ||
                !Enum.IsDefined(row.EntryKind) || !Enum.IsDefined(row.Consistency) || !Enum.IsDefined(row.OperationType) || row.ChunkCount < 0 || row.LogicalLength < 0)
                throw new InvalidDataException("A history header is incomplete or invalid.");
            RepositoryHistoryPaging.ValidateVersionId(row.VersionId);
            if (!ids.Add(row.VersionId) || !RepositoryHistoryPaging.Matches(row, query) || row.FolderEntries is not null || row.ParentVersionIds is not null ||
                previous is not null && (row.CapturedAtUtc > previous.CapturedAtUtc || row.CapturedAtUtc == previous.CapturedAtUtc && StringComparer.Ordinal.Compare(row.VersionId, previous.VersionId) >= 0))
                throw new InvalidDataException("History headers are invalid or out of order.");
            if (query.Cursor is { } boundary && (boundary.Direction == HistoryPageDirection.Older
                    ? RepositoryHistoryPaging.Compare(row, boundary) >= 0 : RepositoryHistoryPaging.Compare(row, boundary) <= 0))
                throw new InvalidDataException("History rows overlap the requested cursor boundary.");
            previous = row;
        }
        if (page.OlderCursor is { Direction: not HistoryPageDirection.Older } || page.NewerCursor is { Direction: not HistoryPageDirection.Newer })
            throw new InvalidDataException("A history continuation has the wrong direction.");
        foreach (var cursor in new[] { page.OlderCursor, page.NewerCursor }.OfType<RepositoryHistoryCursor>())
        {
            RepositoryHistoryPaging.Validate(query with { Cursor = cursor });
            var row = cursor.Direction == HistoryPageDirection.Older ? page.Versions.LastOrDefault() : page.Versions.FirstOrDefault();
            if (cursor.Generation != page.Generation || row is null || RepositoryHistoryPaging.Compare(row, cursor) != 0)
                throw new InvalidDataException("History continuation does not match its emitted boundary.");
        }
    }
    private Task StartSnapshotAsync(VersionInventoryVersionRow row) => StartSnapshotAsync(new RepositoryVersionSummary(
        row.VersionId, row.Path, default, CaptureConsistency.BestEffort, row.Bytes, row.ChunkCount, EntryKind: row.EntryKind));
    private Task StartSnapshotAsync(RepositoryVersionSummary version, int offset = 0)
    {
        latestSnapshotRead = TrackRead(LoadSnapshotAsync(version, offset));
        return latestSnapshotRead;
    }
    private async Task LoadSnapshotAsync(RepositoryVersionSummary version, int offset)
    {
        if (closed || historyQuery is null || readSnapshot is null) return;
        var requestIdentity = ++snapshotRequest;
        var sameTarget = snapshotPage?.Version.VersionId == version.VersionId;
        if (!sameTarget) { snapshotPage = null; SnapshotEntries.Clear(); SelectedSnapshotEntry = null; }
        SnapshotPath = version.SourcePath; IsSnapshotBusy = true; SnapshotStatus = "Loading recorded snapshot…";
        try
        {
            var query = new RepositorySnapshotQuery(historyQuery.RepositoryId, version.VersionId, offset, historyQuery.PageSize);
            var response = await readSnapshot(query, readLifetime.Token).ConfigureAwait(true);
            if (closed || requestIdentity != snapshotRequest) return;
            if (!response.Success || response.SnapshotPage is not { } page) throw new IOException(response.ErrorMessage ?? "Recorded snapshot is unavailable.");
            ValidateSnapshotResponse(query, page);
            if (page.Version.EntryKind != version.EntryKind || !IsSamePath(page.Version.SourcePath, version.SourcePath))
                throw new InvalidDataException("The recorded snapshot does not match the selected version.");
            snapshotPage = page; SnapshotEntries.Clear(); SelectedSnapshotEntry = null;
            foreach (var entry in page.Entries) SnapshotEntries.Add(new(entry.Name, entry.SourcePath, entry.EntryKind, entry.VersionId,
                entry.IsDeleted, entry.LogicalLength, entry.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
            SnapshotStatus = page.Version.EntryKind == RepositoryEntryKind.Folder
                ? $"Recorded folder {page.Version.VersionId}: entries {offset + (page.Entries.Count == 0 ? 0 : 1)}–{offset + page.Entries.Count}."
                : "This recorded version is a file.";
        }
        catch (OperationCanceledException) { if (!closed && requestIdentity == snapshotRequest) SnapshotStatus = "Snapshot loading was cancelled."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { if (!closed && requestIdentity == snapshotRequest) SnapshotStatus = $"Recorded snapshot unavailable: {ex.Message}. No newer version was substituted."; }
        finally { if (requestIdentity == snapshotRequest) { IsSnapshotBusy = false; NotifyPageCommands(); } }
    }
    private static void ValidateSnapshotResponse(RepositorySnapshotQuery query, RepositorySnapshotPage page)
    {
        if (page.Query != query || page.Version is null || page.Version.VersionId != query.VersionId || page.Version.FolderEntries is not null ||
            page.Version.ParentVersionIds is not null || page.Entries is null || page.Entries.Count > query.PageSize ||
            !Enum.IsDefined(page.Version.EntryKind) || string.IsNullOrWhiteSpace(page.Version.SourcePath) || !Path.IsPathFullyQualified(page.Version.SourcePath) ||
            page.Version.EntryKind == RepositoryEntryKind.File && (page.Entries.Count != 0 || page.NextOffset is not null) ||
            page.NextOffset is { } next && (page.Entries.Count == 0 || next != query.Offset + page.Entries.Count))
            throw new InvalidDataException("The returned snapshot does not match its immutable request.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FolderVersionEntry? previous = null;
        foreach (var entry in page.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.SourcePath) ||
                !Path.IsPathFullyQualified(entry.SourcePath) || !Enum.IsDefined(entry.EntryKind) || entry.LogicalLength < 0 ||
                !names.Add(entry.Name) || !string.Equals(Path.GetFileName(entry.SourcePath), entry.Name, StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(entry.SourcePath) is not { } parent || !IsSamePath(parent, page.Version.SourcePath))
                throw new InvalidDataException("Recorded folder entries are invalid.");
            RepositoryHistoryPaging.ValidateVersionId(entry.VersionId);
            if (previous is not null && (entry.EntryKind > previous.EntryKind || entry.EntryKind == previous.EntryKind &&
                StringComparer.OrdinalIgnoreCase.Compare(entry.Name, previous.Name) <= 0))
                throw new InvalidDataException("Recorded folder entries are out of order.");
            previous = entry;
        }
    }
    private Task<VersionInventoryVersionRow?> ResolveRecordedEntryAsync(VersionInventorySnapshotEntryRow entry)
    {
        var task = ResolveRecordedEntryCoreAsync(entry);
        TrackRead(task);
        return task;
    }
    private async Task<VersionInventoryVersionRow?> ResolveRecordedEntryCoreAsync(VersionInventorySnapshotEntryRow entry)
    {
        if (!IsPaged) return versionsById.GetValueOrDefault(entry.VersionId);
        if (closed || readSnapshot is null || historyQuery is null) return null;
        try
        {
            var targetIdentity = snapshotRequest;
            var query = new RepositorySnapshotQuery(historyQuery.RepositoryId, entry.VersionId, PageSize: historyQuery.PageSize);
            var response = await readSnapshot(query, readLifetime.Token).ConfigureAwait(true);
            if (closed || targetIdentity != snapshotRequest || SelectedSnapshotEntry != entry) return null;
            if (!response.Success || response.SnapshotPage is not { } page) throw new IOException(response.ErrorMessage ?? "The recorded child has been removed or is unavailable.");
            ValidateSnapshotResponse(query, page);
            if (page.Version.EntryKind != entry.EntryKind || !IsSamePath(page.Version.SourcePath, entry.Path)) throw new InvalidDataException("The child does not match this recorded folder entry.");
            return ToVersionRow(page.Version);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException)
        { if (!closed) SnapshotStatus = $"Recorded child unavailable: {ex.Message}. No newer version was substituted."; return null; }
    }
    public ValueTask DisposeAsync() => new(disposalTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        closed = true; snapshotRequest++; readLifetime.Cancel(); NotifyPageCommands();
        await Task.WhenAll(activeReads.ToArray()).ConfigureAwait(true);
        readLifetime.Dispose();
    }
}
