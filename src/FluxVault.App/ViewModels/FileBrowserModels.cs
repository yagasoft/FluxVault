using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.App.ViewModels;

public interface IFileBrowserFileSystem
{
    IReadOnlyList<FileBrowserFolderInfo> GetRoots();

    IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path);

    IReadOnlyList<FileBrowserFileInfo> GetFiles(string path);
}

public interface IFileBrowserShellLauncher
{
    void ShowFolder(string folderPath);

    void OpenFile(string filePath);

    void ShowFileInExplorer(string filePath);
}

public sealed record FileBrowserFolderInfo(
    string Path,
    string Name,
    bool IsAccessible,
    string? ErrorMessage);

public sealed record FileBrowserFileInfo(
    string Path,
    string Name,
    long Length);

public enum FileBrowserSelectionVisualState
{
    Empty = 0,
    Checked = 1,
    Indeterminate = 2,
    ChildSelected = 3
}

public sealed partial class FileBrowserFolderNode(
    string path,
    string name,
    bool isAccessible,
    string? errorMessage,
    bool isPhantom = false,
    string? restorableVersionId = null) : ObservableObject
{
    [ObservableProperty]
    private ProtectionSelectionMode? selectionMode;

    [ObservableProperty]
    private bool hasDescendantSelection;

    [ObservableProperty]
    private bool hasLoadedChildren;

    [ObservableProperty]
    private bool hasLocalRegexRules;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? path : name;

    public bool IsAccessible { get; } = isAccessible;

    public string? ErrorMessage { get; } = errorMessage;

    public bool IsPhantom { get; } = isPhantom;

    public string? RestorableVersionId { get; } = restorableVersionId;

    public bool CanUseShellActions => !IsPhantom && IsAccessible;

    public bool CanChangeSelection => !IsPhantom;

    public double ItemOpacity => IsPhantom ? 0.55 : 1.0;

    public ObservableCollection<FileBrowserFolderNode> Children { get; } = [];

    public FileBrowserSelectionVisualState SelectionVisualState => SelectionMode switch
    {
        ProtectionSelectionMode.RecursiveFolder => FileBrowserSelectionVisualState.Checked,
        ProtectionSelectionMode.ImmediateFiles => FileBrowserSelectionVisualState.Indeterminate,
        _ => HasDescendantSelection ? FileBrowserSelectionVisualState.ChildSelected : FileBrowserSelectionVisualState.Empty
    };

    public string SelectionIndicator => SelectionVisualState switch
    {
        FileBrowserSelectionVisualState.Checked => "☑",
        FileBrowserSelectionVisualState.Indeterminate => "◼",
        FileBrowserSelectionVisualState.ChildSelected => "◧",
        _ => "☐"
    };

    public string SelectionToolTip => SelectionVisualState switch
    {
        FileBrowserSelectionVisualState.Checked => "Recursive folder selection. This folder and all descendants are protected.",
        FileBrowserSelectionVisualState.Indeterminate => "Immediate files only. Files directly inside this folder are protected.",
        FileBrowserSelectionVisualState.ChildSelected => "A child folder or file below this folder has a manual selection.",
        _ => "Not selected. Click to cycle this folder through protection modes."
    };

    public string RegexIndicator => HasLocalRegexRules ? "R" : string.Empty;

    public string RegexToolTip => HasLocalRegexRules
        ? "This folder has local include or exclude regex rules."
        : "No local regex rules are defined on this folder.";

    partial void OnSelectionModeChanged(ProtectionSelectionMode? value)
    {
        OnPropertyChanged(nameof(SelectionVisualState));
        OnPropertyChanged(nameof(SelectionIndicator));
        OnPropertyChanged(nameof(SelectionToolTip));
    }

    partial void OnHasDescendantSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectionVisualState));
        OnPropertyChanged(nameof(SelectionIndicator));
        OnPropertyChanged(nameof(SelectionToolTip));
    }

    partial void OnHasLocalRegexRulesChanged(bool value)
    {
        OnPropertyChanged(nameof(RegexIndicator));
        OnPropertyChanged(nameof(RegexToolTip));
    }
}

public sealed partial class FileBrowserFileRow(
    string path,
    string name,
    long length,
    bool isSelected,
    bool isPhantom = false,
    string? restorableVersionId = null) : ObservableObject
{
    [ObservableProperty]
    private bool isSelected = isSelected;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public string Name { get; } = name;

    public long Length { get; } = length;

    public bool IsPhantom { get; } = isPhantom;

    public string? RestorableVersionId { get; } = restorableVersionId;

    public bool CanUseShellActions => !IsPhantom;

    public bool CanChangeSelection => !IsPhantom;

    public double ItemOpacity => IsPhantom ? 0.55 : 1.0;
}

public sealed record PendingSelectionChangeRow(
    string Change,
    string Path,
    string Profile,
    string Regex,
    string Detail);
