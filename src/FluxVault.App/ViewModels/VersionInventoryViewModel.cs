using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Storage;

namespace FluxVault.App.ViewModels;

public sealed partial class VersionInventoryViewModel : ObservableObject
{
    private readonly Func<VersionInventoryVersionRow, Task> restoreVersion;
    private readonly Func<VersionInventoryVersionRow, Task> openVersionPreview;
    private readonly Dictionary<string, VersionInventoryVersionRow> versionsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<VersionInventoryVersionRow> allVersionRows;
    private string currentFocusPath;
    private RepositoryEntryKind? currentFocusKind;

    public VersionInventoryViewModel(
        string folderPath,
        IReadOnlyList<RepositoryVersionSummary> versions,
        Func<VersionInventoryVersionRow, Task> restoreVersion,
        Func<VersionInventoryVersionRow, Task> openVersionPreview,
        string? focusPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(versions);
        this.restoreVersion = restoreVersion;
        this.openVersionPreview = openVersionPreview;
        FolderPath = Path.GetFullPath(folderPath);
        var normalisedFocusPath = string.IsNullOrWhiteSpace(focusPath)
            ? null
            : Path.GetFullPath(focusPath);
        var filteredVersions = versions
            .Where(version => normalisedFocusPath is null
                ? IsUnderOrSameFolder(version.SourcePath, FolderPath) || IsSamePath(version.SourcePath, FolderPath)
                : SourcePathMatchesFocus(version.SourcePath, normalisedFocusPath))
            .OrderByDescending(version => version.CapturedAtUtc)
            .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
            .ToArray();

        allVersionRows = filteredVersions.Select(ToVersionRow).ToArray();
        foreach (var version in allVersionRows)
        {
            versionsById[version.VersionId] = version;
        }

        foreach (var group in filteredVersions
                     .Where(version => version.EntryKind == RepositoryEntryKind.File)
                     .GroupBy(version => Path.GetFullPath(version.SourcePath), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var orderedVersions = group
                .OrderByDescending(version => version.CapturedAtUtc)
                .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
                .Select(ToVersionRow)
                .ToArray();
            var latest = orderedVersions[0];
            Files.Add(new VersionInventoryFileRow(
                latest.File,
                latest.Path,
                latest.VersionId,
                $"{latest.Lineage} - {latest.Consistency}",
                latest.CapturedAt,
                latest.Bytes,
                orderedVersions));
        }

        currentFocusPath = normalisedFocusPath ?? FolderPath;
        currentFocusKind = normalisedFocusPath is null
            ? null
            : DetermineFocusKind(allVersionRows, normalisedFocusPath);
        RefreshVisibleVersions(currentFocusPath, currentFocusKind);
        SelectedVersion = Versions.FirstOrDefault();
    }

    public string FolderPath { get; }

    public ObservableCollection<VersionInventoryFileRow> Files { get; } = [];

    public ObservableCollection<VersionInventoryVersionRow> Versions { get; } = [];

    public ObservableCollection<VersionInventorySnapshotEntryRow> SnapshotEntries { get; } = [];

    [ObservableProperty]
    private VersionInventoryVersionRow? selectedVersion;

    [ObservableProperty]
    private VersionInventorySnapshotEntryRow? selectedSnapshotEntry;

    [ObservableProperty]
    private bool isPreviewBusy;

    [ObservableProperty]
    private string previewStatus = string.Empty;

    partial void OnSelectedVersionChanged(VersionInventoryVersionRow? value)
    {
        SnapshotEntries.Clear();
        SelectedSnapshotEntry = null;
        PreviewSelectedVersionCommand.NotifyCanExecuteChanged();
        if (value?.FolderEntries is null)
        {
            return;
        }

        foreach (var entry in value.FolderEntries
                     .OrderByDescending(entry => entry.EntryKind)
                     .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            SnapshotEntries.Add(new VersionInventorySnapshotEntryRow(
                entry.Name,
                Path.GetFullPath(entry.SourcePath),
                entry.EntryKind,
                entry.VersionId,
                entry.IsDeleted,
                entry.LogicalLength,
                entry.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        }
    }

    partial void OnSelectedSnapshotEntryChanged(VersionInventorySnapshotEntryRow? value)
    {
        OpenSelectedSnapshotEntryCommand.NotifyCanExecuteChanged();
        PreviewSelectedSnapshotEntryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPreviewBusyChanged(bool value)
    {
        OpenVersionPreviewCommand.NotifyCanExecuteChanged();
        PreviewSelectedVersionCommand.NotifyCanExecuteChanged();
        PreviewSelectedSnapshotEntryCommand.NotifyCanExecuteChanged();
        OpenSelectedSnapshotEntryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task RestoreVersionAsync(VersionInventoryVersionRow? version)
    {
        if (version is not null)
        {
            await restoreVersion(version).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenVersionPreview))]
    private async Task OpenVersionPreviewAsync(VersionInventoryVersionRow? version)
    {
        if (version is { EntryKind: RepositoryEntryKind.File })
        {
            await OpenPreviewAsync(version).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedVersionAsync()
    {
        if (SelectedVersion is not null)
        {
            await restoreVersion(SelectedVersion).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewSelectedVersion))]
    private async Task PreviewSelectedVersionAsync()
    {
        if (SelectedVersion is { EntryKind: RepositoryEntryKind.File } version)
        {
            await OpenPreviewAsync(version).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedSnapshotEntryAsync()
    {
        if (SelectedSnapshotEntry is not null && versionsById.TryGetValue(SelectedSnapshotEntry.VersionId, out var version))
        {
            await restoreVersion(version).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewSelectedSnapshotEntry))]
    private async Task PreviewSelectedSnapshotEntryAsync()
    {
        if (SelectedSnapshotEntry is { EntryKind: RepositoryEntryKind.File } entry
            && versionsById.TryGetValue(entry.VersionId, out var version))
        {
            await OpenPreviewAsync(version).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedSnapshotEntry))]
    private async Task OpenSelectedSnapshotEntryAsync()
    {
        if (SelectedSnapshotEntry is null || !versionsById.TryGetValue(SelectedSnapshotEntry.VersionId, out var version))
        {
            return;
        }

        if (SelectedSnapshotEntry.EntryKind == RepositoryEntryKind.Folder)
        {
            NavigateToFolderVersion(version);
            return;
        }

        await OpenPreviewAsync(version).ConfigureAwait(true);
    }

    private bool CanOpenVersionPreview(VersionInventoryVersionRow? version)
    {
        return !IsPreviewBusy && version is { EntryKind: RepositoryEntryKind.File };
    }

    private bool CanPreviewSelectedVersion()
    {
        return !IsPreviewBusy && SelectedVersion is { EntryKind: RepositoryEntryKind.File };
    }

    private bool CanPreviewSelectedSnapshotEntry()
    {
        return !IsPreviewBusy && SelectedSnapshotEntry is { EntryKind: RepositoryEntryKind.File };
    }

    private bool CanOpenSelectedSnapshotEntry()
    {
        return !IsPreviewBusy && SelectedSnapshotEntry is not null;
    }

    private async Task OpenPreviewAsync(VersionInventoryVersionRow version)
    {
        if (IsPreviewBusy)
        {
            return;
        }

        IsPreviewBusy = true;
        PreviewStatus = "Preparing preview...";
        try
        {
            await openVersionPreview(version).ConfigureAwait(true);
        }
        finally
        {
            PreviewStatus = string.Empty;
            IsPreviewBusy = false;
        }
    }

    private void NavigateToFolderVersion(VersionInventoryVersionRow version)
    {
        currentFocusPath = version.Path;
        currentFocusKind = RepositoryEntryKind.Folder;
        RefreshVisibleVersions(currentFocusPath, currentFocusKind);
        SelectedVersion = Versions.FirstOrDefault(row => row.VersionId == version.VersionId)
            ?? Versions.FirstOrDefault();
    }

    private void RefreshVisibleVersions(string focusPath, RepositoryEntryKind? focusKind)
    {
        Versions.Clear();
        var rows = focusKind is null
            ? allVersionRows
            : allVersionRows
                .Where(version => version.EntryKind == focusKind.Value)
                .Where(version => IsSamePath(version.Path, focusPath));
        foreach (var row in rows
                     .OrderByDescending(version => version.CapturedAt, StringComparer.Ordinal)
                     .ThenByDescending(version => version.VersionId, StringComparer.Ordinal))
        {
            Versions.Add(row);
        }
    }

    private static VersionInventoryVersionRow ToVersionRow(RepositoryVersionSummary version)
    {
        return new VersionInventoryVersionRow(
            version.VersionId,
            Path.GetFullPath(version.SourcePath),
            Path.GetFileName(version.SourcePath),
            version.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            FormatConsistency(version.Consistency),
            version.LogicalLength,
            FormatLineage(version.OperationType),
            version.ChunkCount,
            version.EntryKind,
            version.IsDeleted,
            version.FolderEntries);
    }

    private static bool IsUnderOrSameFolder(string path, string folder)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            return false;
        }

        var trimmedFolder = TrimPath(folder);
        return string.Equals(TrimPath(parent), trimmedFolder, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(trimmedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SourcePathMatchesFocus(string path, string focusPath)
    {
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(TrimPath(fullPath), TrimPath(focusPath), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var focusRoot = TrimPath(focusPath) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(focusRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static RepositoryEntryKind DetermineFocusKind(
        IReadOnlyList<VersionInventoryVersionRow> versions,
        string focusPath)
    {
        return versions
            .Where(version => IsSamePath(version.Path, focusPath))
            .OrderByDescending(version => version.EntryKind == RepositoryEntryKind.Folder)
            .Select(version => version.EntryKind)
            .FirstOrDefault();
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(TrimPath(left), TrimPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string FormatConsistency(CaptureConsistency consistency)
    {
        return consistency switch
        {
            CaptureConsistency.AppConsistent => "App consistent",
            CaptureConsistency.CrashConsistent => "Crash consistent",
            CaptureConsistency.BestEffort => "Best effort",
            _ => consistency.ToString()
        };
    }

    private static string FormatLineage(VersionOperationType operationType)
    {
        return operationType switch
        {
            VersionOperationType.Capture => "Captured",
            VersionOperationType.Restore => "Restored",
            VersionOperationType.InheritedCopy => "Inherited copy",
            VersionOperationType.RemoteSync => "Remote sync",
            VersionOperationType.Delete => "Deleted",
            _ => operationType.ToString()
        };
    }
}

public sealed partial class VersionInventoryFileRow(
    string file,
    string path,
    string latestVersionId,
    string status,
    string latestCapturedAt,
    long bytes,
    IReadOnlyList<VersionInventoryVersionRow> versions) : ObservableObject
{
    [ObservableProperty]
    private bool isExpanded;

    public string File { get; } = file;

    public string Path { get; } = path;

    public string LatestVersionId { get; } = latestVersionId;

    public string Status { get; } = status;

    public string LatestCapturedAt { get; } = latestCapturedAt;

    public long Bytes { get; } = bytes;

    public IReadOnlyList<VersionInventoryVersionRow> Versions { get; } = versions;
}

public sealed record VersionInventoryVersionRow(
    string VersionId,
    string Path,
    string File,
    string CapturedAt,
    string Consistency,
    long Bytes,
    string Lineage,
    int ChunkCount,
    RepositoryEntryKind EntryKind = RepositoryEntryKind.File,
    bool IsDeleted = false,
    IReadOnlyList<FolderVersionEntry>? FolderEntries = null)
{
    public string Kind => EntryKind.ToString();

    public string State => IsDeleted ? "Deleted" : "Live";
}

public sealed record VersionInventorySnapshotEntryRow(
    string Name,
    string Path,
    RepositoryEntryKind EntryKind,
    string VersionId,
    bool IsDeleted,
    long Bytes,
    string CapturedAt)
{
    public string Kind => EntryKind.ToString();

    public string State => IsDeleted ? "Deleted" : "Live";
}
