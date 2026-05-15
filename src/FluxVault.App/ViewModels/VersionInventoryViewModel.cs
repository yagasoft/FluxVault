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

        foreach (var group in versions
                     .Where(version => IsUnderOrSameFolder(version.SourcePath, FolderPath))
                     .Where(version => normalisedFocusPath is null
                                       || SourcePathMatchesFocus(version.SourcePath, normalisedFocusPath))
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
    }

    public string FolderPath { get; }

    public ObservableCollection<VersionInventoryFileRow> Files { get; } = [];

    [RelayCommand]
    private async Task RestoreVersionAsync(VersionInventoryVersionRow? version)
    {
        if (version is not null)
        {
            await restoreVersion(version).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenVersionPreviewAsync(VersionInventoryVersionRow? version)
    {
        if (version is not null)
        {
            await openVersionPreview(version).ConfigureAwait(true);
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
            version.ChunkCount);
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
    int ChunkCount);
