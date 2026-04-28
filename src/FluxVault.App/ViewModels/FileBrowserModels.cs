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

public sealed record FileBrowserFolderInfo(
    string Path,
    string Name,
    bool IsAccessible,
    string? ErrorMessage);

public sealed record FileBrowserFileInfo(
    string Path,
    string Name,
    long Length);

public sealed partial class FileBrowserFolderNode(
    string path,
    string name,
    bool isAccessible,
    string? errorMessage) : ObservableObject
{
    [ObservableProperty]
    private ProtectionSelectionMode? selectionMode;

    [ObservableProperty]
    private bool hasDescendantSelection;

    [ObservableProperty]
    private bool hasLoadedChildren;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? path : name;

    public bool IsAccessible { get; } = isAccessible;

    public string? ErrorMessage { get; } = errorMessage;

    public ObservableCollection<FileBrowserFolderNode> Children { get; } = [];

    public string SelectionGlyph => SelectionMode switch
    {
        ProtectionSelectionMode.RecursiveFolder => "Recursive",
        ProtectionSelectionMode.ImmediateFiles => "Files",
        _ => HasDescendantSelection ? "Child" : "None"
    };

    partial void OnSelectionModeChanged(ProtectionSelectionMode? value)
    {
        OnPropertyChanged(nameof(SelectionGlyph));
    }

    partial void OnHasDescendantSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectionGlyph));
    }
}

public sealed partial class FileBrowserFileRow(
    string path,
    string name,
    long length,
    bool isSelected) : ObservableObject
{
    [ObservableProperty]
    private bool isSelected = isSelected;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public string Name { get; } = name;

    public long Length { get; } = length;
}

public sealed record PendingSelectionChangeRow(
    string Change,
    string Path,
    string Detail);
