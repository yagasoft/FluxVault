using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;

namespace FluxVault.App.ViewModels;

public sealed partial class FileBrowserViewModel(IFileBrowserFileSystem fileSystem) : ObservableObject
{
    private readonly Dictionary<string, ProtectionSelectionRule> currentRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProtectionSelectionRule> baselineRules = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private FileBrowserFolderNode? selectedFolder;

    public ObservableCollection<FileBrowserFolderNode> Roots { get; } = [];

    public ObservableCollection<FileBrowserFileRow> Files { get; } = [];

    public ObservableCollection<PendingSelectionChangeRow> PendingChanges { get; } = [];

    public event EventHandler? SelectionRulesChanged;

    public void LoadRoots()
    {
        Roots.Clear();
        foreach (var root in fileSystem.GetRoots())
        {
            var node = ToNode(root);
            ApplySelectionToNode(node);
            Roots.Add(node);
        }

        RefreshTreeIndicators(Roots);
    }

    public void LoadSelectionRules(IReadOnlyList<ProtectionSelectionRule> rules)
    {
        baselineRules.Clear();
        currentRules.Clear();
        foreach (var rule in rules.Select(Normalise).Where(rule => rule.IsEnabled))
        {
            baselineRules[rule.Path] = rule;
            currentRules[rule.Path] = rule;
        }

        RefreshPendingChanges();
        ApplySelectionToTree(Roots);
    }

    public IReadOnlyList<ProtectionSelectionRule> GetSelectionRules()
    {
        return currentRules.Values
            .OrderBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void SelectFolder(FileBrowserFolderNode folder)
    {
        SelectedFolder = folder;
        LoadChildren(folder);
        LoadFiles(folder);
    }

    public void LoadChildren(FileBrowserFolderNode folder)
    {
        if (folder.HasLoadedChildren || !folder.IsAccessible)
        {
            return;
        }

        folder.Children.Clear();
        foreach (var child in fileSystem.GetChildFolders(folder.Path))
        {
            var node = ToNode(child);
            ApplySelectionToNode(node);
            folder.Children.Add(node);
        }

        folder.HasLoadedChildren = true;
        RefreshTreeIndicators(Roots);
    }

    public void ToggleFolderSelection(FileBrowserFolderNode folder)
    {
        var next = folder.SelectionMode switch
        {
            null => (ProtectionSelectionMode?)ProtectionSelectionMode.RecursiveFolder,
            ProtectionSelectionMode.RecursiveFolder => ProtectionSelectionMode.ImmediateFiles,
            ProtectionSelectionMode.ImmediateFiles => null,
            _ => ProtectionSelectionMode.RecursiveFolder
        };

        if (next is null)
        {
            RemoveSelectionRule(folder.Path);
        }
        else
        {
            ReplaceSelectionRule(new ProtectionSelectionRule(
                StableRuleId("folder", folder.Path),
                folder.Path,
                next.Value,
                CompressionPreference.Zstd,
                ResourceProfile.Balanced,
                IsEnabled: true));
        }

        ApplySelectionToTree([folder]);
        ApplySelectionToTree(Roots);
    }

    public void ToggleFileSelection(FileBrowserFileRow file)
    {
        if (file.IsSelected)
        {
            ReplaceSelectionRule(new ProtectionSelectionRule(
                StableRuleId("file", file.Path),
                file.Path,
                ProtectionSelectionMode.File,
                CompressionPreference.Zstd,
                ResourceProfile.Balanced,
                IsEnabled: true));
        }
        else
        {
            RemoveSelectionRule(file.Path);
        }

        RefreshTreeIndicators(Roots);
    }

    public void ReplaceSelectionRule(ProtectionSelectionRule rule)
    {
        var normalised = Normalise(rule);
        currentRules[normalised.Path] = normalised;
        RefreshPendingChanges();
        SelectionRulesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveSelectionRule(string path)
    {
        currentRules.Remove(Path.GetFullPath(path));
        RefreshPendingChanges();
        SelectionRulesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshTreeIndicators(IEnumerable<FileBrowserFolderNode> roots)
    {
        foreach (var root in roots)
        {
            _ = RefreshNodeIndicator(root);
        }
    }

    [RelayCommand]
    private void RefreshRoots()
    {
        LoadRoots();
    }

    [RelayCommand]
    private void ToggleSelectedFolder()
    {
        if (SelectedFolder is not null)
        {
            ToggleFolderSelection(SelectedFolder);
        }
    }

    partial void OnSelectedFolderChanged(FileBrowserFolderNode? value)
    {
        if (value is not null)
        {
            SelectFolder(value);
        }
    }

    private void LoadFiles(FileBrowserFolderNode folder)
    {
        Files.Clear();
        if (!folder.IsAccessible)
        {
            return;
        }

        foreach (var file in fileSystem.GetFiles(folder.Path))
        {
            var row = new FileBrowserFileRow(
                file.Path,
                file.Name,
                file.Length,
                currentRules.ContainsKey(Path.GetFullPath(file.Path)));
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FileBrowserFileRow.IsSelected))
                {
                    ToggleFileSelection(row);
                }
            };
            Files.Add(row);
        }
    }

    private void ApplySelectionToTree(IEnumerable<FileBrowserFolderNode> roots)
    {
        foreach (var root in roots)
        {
            ApplySelectionToNode(root);
            ApplySelectionToTree(root.Children);
        }

        RefreshTreeIndicators(roots);
        if (SelectedFolder is not null)
        {
            LoadFiles(SelectedFolder);
        }
    }

    private void ApplySelectionToNode(FileBrowserFolderNode node)
    {
        node.SelectionMode = node.IsAccessible && currentRules.TryGetValue(node.Path, out var rule) ? rule.Mode : null;
    }

    private bool RefreshNodeIndicator(FileBrowserFolderNode node)
    {
        var descendantSelected = false;
        foreach (var child in node.Children)
        {
            descendantSelected |= child.SelectionMode is not null || RefreshNodeIndicator(child);
        }

        descendantSelected |= currentRules.Values.Any(rule => IsUnderPath(rule.Path, node.Path) && !IsSamePath(rule.Path, node.Path));
        node.HasDescendantSelection = descendantSelected;
        return descendantSelected;
    }

    private void RefreshPendingChanges()
    {
        PendingChanges.Clear();
        foreach (var rule in currentRules.Values.OrderBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!baselineRules.TryGetValue(rule.Path, out var baseline))
            {
                PendingChanges.Add(new PendingSelectionChangeRow("Added", rule.Path, rule.Mode.ToString()));
            }
            else if (baseline.Mode != rule.Mode
                     || baseline.Compression != rule.Compression
                     || baseline.ResourceProfile != rule.ResourceProfile)
            {
                PendingChanges.Add(new PendingSelectionChangeRow("Changed", rule.Path, $"{baseline.Mode} -> {rule.Mode}"));
            }
        }

        foreach (var baseline in baselineRules.Values.OrderBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!currentRules.ContainsKey(baseline.Path))
            {
                PendingChanges.Add(new PendingSelectionChangeRow("Removed", baseline.Path, baseline.Mode.ToString()));
            }
        }
    }

    private static FileBrowserFolderNode ToNode(FileBrowserFolderInfo info)
    {
        var node = new FileBrowserFolderNode(info.Path, info.Name, info.IsAccessible, info.ErrorMessage);
        if (info.IsAccessible)
        {
            node.Children.Add(new FileBrowserFolderNode(info.Path, "Loading...", isAccessible: false, errorMessage: null));
        }

        return node;
    }

    private static ProtectionSelectionRule Normalise(ProtectionSelectionRule rule)
    {
        return rule with { Path = Path.GetFullPath(rule.Path) };
    }

    private static string StableRuleId(string prefix, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return $"{prefix}-{Convert.ToHexString(bytes, 0, 6).ToLowerInvariant()}";
    }

    private static bool IsUnderPath(string path, string root)
    {
        var trimmedRoot = TrimPath(root) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(TrimPath(left), TrimPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
