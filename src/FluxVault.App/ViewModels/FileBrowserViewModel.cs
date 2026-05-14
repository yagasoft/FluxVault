using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Policies;

namespace FluxVault.App.ViewModels;

public sealed partial class FileBrowserViewModel(
    IFileBrowserFileSystem fileSystem,
    IFileBrowserShellLauncher? shellLauncher = null) : ObservableObject
{
    private readonly Dictionary<string, ProtectionSelectionRule> currentRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProtectionSelectionRule> baselineRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly IFileBrowserShellLauncher shellLauncher = shellLauncher ?? new WindowsFileBrowserShellLauncher();

    [ObservableProperty]
    private FileBrowserFolderNode? selectedFolder;

    [ObservableProperty]
    private FileBrowserFileRow? selectedFile;

    [ObservableProperty]
    private string selectedIncludeRegexText = string.Empty;

    [ObservableProperty]
    private string selectedExcludeRegexText = string.Empty;

    [ObservableProperty]
    private string selectedRegexStatus = "Select a protected folder or file to manage scoped regex rules.";

    [ObservableProperty]
    private WorkloadPolicyPresetId defaultWorkloadPreset = WorkloadPolicyPresetId.GeneralPurpose;

    [ObservableProperty]
    private WorkloadPolicyPresetId selectedWorkloadPreset = WorkloadPolicyPresetId.GeneralPurpose;

    [ObservableProperty]
    private string selectedWorkloadPresetDescription = "Select a protected folder or file to manage its workload preset.";

    private bool isLoadingSelectedWorkloadPreset;

    public ObservableCollection<FileBrowserFolderNode> Roots { get; } = [];

    public ObservableCollection<FileBrowserFileRow> Files { get; } = [];

    public ObservableCollection<PendingSelectionChangeRow> PendingChanges { get; } = [];

    public IReadOnlyList<WorkloadPolicyPresetOption> WorkloadPresets { get; } = WorkloadPolicyPresetCatalog.PresetOptions;

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
        currentRules.TryGetValue(folder.Path, out var existingRule);
        var currentMode = existingRule?.Mode == ProtectionSelectionMode.RegexScope ? null : folder.SelectionMode;
        var next = currentMode switch
        {
            null => (ProtectionSelectionMode?)ProtectionSelectionMode.RecursiveFolder,
            ProtectionSelectionMode.RecursiveFolder => ProtectionSelectionMode.ImmediateFiles,
            ProtectionSelectionMode.ImmediateFiles => null,
            _ => ProtectionSelectionMode.RecursiveFolder
        };

        if (next is null)
        {
            if (existingRule is not null && HasRegexRules(existingRule))
            {
                ReplaceSelectionRule(existingRule with
                {
                    Mode = ProtectionSelectionMode.RegexScope,
                    WorkloadPreset = null
                });
            }
            else
            {
                RemoveSelectionRule(folder.Path);
            }
        }
        else
        {
            var preset = WorkloadPolicyPresetCatalog.Get(DefaultWorkloadPreset);
            ReplaceSelectionRule((existingRule ?? new ProtectionSelectionRule(
                    StableRuleId("folder", folder.Path),
                    folder.Path,
                    next.Value,
                    preset.Compression,
                    preset.ResourceProfile,
                    IsEnabled: true,
                    WorkloadPreset: DefaultWorkloadPreset)) with
                {
                    Mode = next.Value,
                    Compression = preset.Compression,
                    ResourceProfile = preset.ResourceProfile,
                    WorkloadPreset = DefaultWorkloadPreset
                });
        }

        ApplySelectionToTree([folder]);
        ApplySelectionToTree(Roots);
    }

    public void ToggleFileSelection(FileBrowserFileRow file)
    {
        if (file.IsSelected)
        {
            var preset = WorkloadPolicyPresetCatalog.Get(DefaultWorkloadPreset);
            ReplaceSelectionRule(new ProtectionSelectionRule(
                StableRuleId("file", file.Path),
                file.Path,
                ProtectionSelectionMode.File,
                preset.Compression,
                preset.ResourceProfile,
                IsEnabled: true,
                WorkloadPreset: DefaultWorkloadPreset));
        }
        else
        {
            RemoveSelectionRule(file.Path);
        }

        RefreshTreeIndicators(Roots);
    }

    public void AddPathSelection(string path, bool isDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        if (currentRules.ContainsKey(fullPath))
        {
            return;
        }

        var mode = isDirectory ? ProtectionSelectionMode.ImmediateFiles : ProtectionSelectionMode.File;
        var preset = WorkloadPolicyPresetCatalog.Get(DefaultWorkloadPreset);
        ReplaceSelectionRule(new ProtectionSelectionRule(
            StableRuleId(isDirectory ? "folder" : "file", fullPath),
            fullPath,
            mode,
            preset.Compression,
            preset.ResourceProfile,
            IsEnabled: true,
            WorkloadPreset: DefaultWorkloadPreset));
        ApplySelectionToTree(Roots);
    }

    public bool RemovePathSelection(string path, bool isDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        if (currentRules.ContainsKey(fullPath))
        {
            RemoveSelectionRule(fullPath);
            ApplySelectionToTree(Roots);
            return true;
        }

        var inherited = FindNearestCoveringRule(fullPath, isDirectory);
        if (inherited is null)
        {
            return false;
        }

        var exclusion = isDirectory
            ? ProtectionScopedRegexRule.PathPrefix(
                StableRuleId("exclude-folder", fullPath),
                fullPath,
                ProtectionExclusionTarget.Both,
                "Explorer removal")
            : ProtectionScopedRegexRule.ExactPath(
                StableRuleId("exclude-file", fullPath),
                fullPath,
                ProtectionExclusionTarget.File,
                "Explorer removal");
        ReplaceSelectionRule(inherited with
        {
            ExcludeRegexRules = (inherited.ExcludeRegexRules ?? [])
                .Append(exclusion)
                .DistinctBy(rule => rule.Pattern, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        });
        ApplySelectionToTree(Roots);
        return true;
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
            ApplySelectionToNode(root);
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

    [RelayCommand]
    private void ApplySelectedRegexRules()
    {
        var path = SelectedFile?.Path ?? SelectedFolder?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            SelectedRegexStatus = "Select a folder or protected file before applying regex rules.";
            return;
        }

        var fullPath = Path.GetFullPath(path);
        currentRules.TryGetValue(fullPath, out var rule);
        if (rule is null && SelectedFile is not null)
        {
            SelectedRegexStatus = "Select the file for protection before applying file-local regex rules.";
            return;
        }

        var includeRules = ToScopedRegexRules("include", SelectedIncludeRegexText, ProtectionExclusionTarget.File);
        var excludeRules = ToScopedRegexRules("exclude", SelectedExcludeRegexText, ProtectionExclusionTarget.File);
        if (rule is null && includeRules.Count == 0 && excludeRules.Count == 0)
        {
            SelectedRegexStatus = "No regex rules are defined for this folder.";
            return;
        }

        if (rule is not null && rule.Mode == ProtectionSelectionMode.RegexScope && includeRules.Count == 0 && excludeRules.Count == 0)
        {
            RemoveSelectionRule(rule.Path);
            ApplySelectionToTree(Roots);
            SelectedRegexStatus = "Scoped regex rules cleared. Save selections to apply them.";
            return;
        }

        var targetRule = rule ?? new ProtectionSelectionRule(
            StableRuleId("regex", fullPath),
            fullPath,
            ProtectionSelectionMode.RegexScope,
            CompressionPreference.Zstd,
            ResourceProfile.Balanced,
            IsEnabled: true);
        ReplaceSelectionRule(targetRule with
        {
            IncludeRegexRules = includeRules,
            ExcludeRegexRules = excludeRules
        });
        ApplySelectionToTree(Roots);
        SelectedRegexStatus = "Scoped regex rules updated. Save selections to apply them.";
    }

    [RelayCommand]
    private void ClearSelectedRegexRules()
    {
        SelectedIncludeRegexText = string.Empty;
        SelectedExcludeRegexText = string.Empty;
        ApplySelectedRegexRules();
    }

    [RelayCommand]
    private void ShowSelectedFolderInExplorer()
    {
        if (SelectedFolder is not null)
        {
            shellLauncher.ShowFolder(SelectedFolder.Path);
        }
    }

    [RelayCommand]
    private void OpenSelectedFile()
    {
        if (SelectedFile is not null)
        {
            shellLauncher.OpenFile(SelectedFile.Path);
        }
    }

    [RelayCommand]
    private void ShowSelectedFileInExplorer()
    {
        if (SelectedFile is not null)
        {
            shellLauncher.ShowFileInExplorer(SelectedFile.Path);
        }
    }

    partial void OnSelectedFolderChanged(FileBrowserFolderNode? value)
    {
        if (value is not null)
        {
            SelectedFile = null;
            SelectFolder(value);
            LoadRegexText(value.Path);
            LoadWorkloadPresetText(value.Path);
        }
    }

    partial void OnSelectedFileChanged(FileBrowserFileRow? value)
    {
        if (value is not null)
        {
            LoadRegexText(value.Path);
            LoadWorkloadPresetText(value.Path);
        }
    }

    partial void OnSelectedWorkloadPresetChanged(WorkloadPolicyPresetId value)
    {
        if (isLoadingSelectedWorkloadPreset)
        {
            return;
        }

        var path = SelectedFile?.Path ?? SelectedFolder?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            SelectedWorkloadPresetDescription = "Select a protected folder or file to manage its workload preset.";
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!currentRules.TryGetValue(fullPath, out var rule)
            || rule.Mode == ProtectionSelectionMode.RegexScope)
        {
            SelectedWorkloadPresetDescription = "Add this item before assigning a workload preset.";
            return;
        }

        var preset = WorkloadPolicyPresetCatalog.Get(value);
        ReplaceSelectionRule(rule with
        {
            WorkloadPreset = value,
            Compression = preset.Compression,
            ResourceProfile = preset.ResourceProfile
        });
        RefreshTreeIndicators(Roots);
        SelectedWorkloadPresetDescription = preset.Description;
    }

    private void LoadFiles(FileBrowserFolderNode folder)
    {
        Files.Clear();
        SelectedFile = null;
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
        if (node.IsAccessible && currentRules.TryGetValue(node.Path, out var rule))
        {
            node.SelectionMode = rule.Mode == ProtectionSelectionMode.RegexScope ? null : rule.Mode;
            node.HasLocalRegexRules = HasRegexRules(rule);
        }
        else
        {
            node.SelectionMode = null;
            node.HasLocalRegexRules = false;
        }
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
                PendingChanges.Add(new PendingSelectionChangeRow(
                    "Added",
                    rule.Path,
                    FormatProfile(rule),
                    FormatRegexSummary([rule], rule.Path),
                    FormatMode(rule.Mode)));
            }
            else if (baseline.Mode != rule.Mode
                     || baseline.Compression != rule.Compression
                     || baseline.ResourceProfile != rule.ResourceProfile
                     || (baseline.WorkloadPreset ?? WorkloadPolicyPresetId.GeneralPurpose) != (rule.WorkloadPreset ?? WorkloadPolicyPresetId.GeneralPurpose)
                     || !RegexRulesEqual(baseline.IncludeRegexRules, rule.IncludeRegexRules)
                     || !RegexRulesEqual(baseline.ExcludeRegexRules, rule.ExcludeRegexRules))
            {
                PendingChanges.Add(new PendingSelectionChangeRow(
                    "Changed",
                    rule.Path,
                    FormatProfile(rule),
                    FormatRegexSummary([rule], rule.Path),
                    FormatChangeDetail(baseline, rule)));
            }
        }

        foreach (var baseline in baselineRules.Values.OrderBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!currentRules.ContainsKey(baseline.Path))
            {
                PendingChanges.Add(new PendingSelectionChangeRow(
                    "Removed",
                    baseline.Path,
                    FormatProfile(baseline),
                    FormatRegexSummary([baseline], baseline.Path),
                    FormatMode(baseline.Mode)));
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
        return rule with
        {
            Path = Path.GetFullPath(rule.Path),
            IncludeRegexRules = rule.IncludeRegexRules ?? [],
            ExcludeRegexRules = rule.ExcludeRegexRules ?? [],
            WorkloadPreset = rule.WorkloadPreset ?? WorkloadPolicyPresetId.GeneralPurpose
        };
    }

    private ProtectionSelectionRule? FindNearestCoveringRule(string path, bool isDirectory)
    {
        return currentRules.Values
            .Where(rule => Covers(rule, path, isDirectory))
            .OrderByDescending(rule => TrimPath(rule.Path).Length)
            .FirstOrDefault();
    }

    private static bool Covers(ProtectionSelectionRule rule, string path, bool isDirectory)
    {
        return rule.Mode switch
        {
            ProtectionSelectionMode.RecursiveFolder => IsSamePath(path, rule.Path) || IsUnderPath(path, rule.Path),
            ProtectionSelectionMode.ImmediateFiles when !isDirectory => IsDirectChildFile(path, rule.Path),
            ProtectionSelectionMode.File when !isDirectory => IsSamePath(path, rule.Path),
            _ => false
        };
    }

    private static bool IsDirectChildFile(string filePath, string folderPath)
    {
        var parent = Path.GetDirectoryName(filePath);
        return parent is not null && IsSamePath(parent, folderPath);
    }

    private static IReadOnlyList<ProtectionScopedRegexRule> ToScopedRegexRules(
        string prefix,
        string text,
        ProtectionExclusionTarget target)
    {
        return text
            .Split(["\r\n", "\n"], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select((pattern, index) => new ProtectionScopedRegexRule(
                $"{prefix}-{index + 1}",
                pattern,
                target,
                IsEnabled: true))
            .ToArray();
    }

    private void LoadRegexText(string path)
    {
        if (!currentRules.TryGetValue(Path.GetFullPath(path), out var rule))
        {
            SelectedIncludeRegexText = string.Empty;
            SelectedExcludeRegexText = string.Empty;
            SelectedRegexStatus = "Define regex here to filter protected descendant folders without selecting this folder.";
            return;
        }

        SelectedIncludeRegexText = string.Join(Environment.NewLine, (rule.IncludeRegexRules ?? []).Select(regex => regex.Pattern));
        SelectedExcludeRegexText = string.Join(Environment.NewLine, (rule.ExcludeRegexRules ?? []).Select(regex => regex.Pattern));
        SelectedRegexStatus = "Scoped regex rules are local to the selected folder or file.";
    }

    private void LoadWorkloadPresetText(string path)
    {
        isLoadingSelectedWorkloadPreset = true;
        try
        {
            if (!currentRules.TryGetValue(Path.GetFullPath(path), out var rule)
                || rule.Mode == ProtectionSelectionMode.RegexScope)
            {
                SelectedWorkloadPreset = DefaultWorkloadPreset;
                SelectedWorkloadPresetDescription = "This item is not directly selected. Add it before assigning a workload preset.";
                return;
            }

            var presetId = rule.WorkloadPreset ?? WorkloadPolicyPresetId.GeneralPurpose;
            var preset = WorkloadPolicyPresetCatalog.Get(presetId);
            SelectedWorkloadPreset = presetId;
            SelectedWorkloadPresetDescription = preset.Description;
        }
        finally
        {
            isLoadingSelectedWorkloadPreset = false;
        }
    }

    private static string FormatChangeDetail(ProtectionSelectionRule baseline, ProtectionSelectionRule rule)
    {
        var baselinePreset = baseline.WorkloadPreset ?? WorkloadPolicyPresetId.GeneralPurpose;
        var rulePreset = rule.WorkloadPreset ?? WorkloadPolicyPresetId.GeneralPurpose;
        if (baseline.Mode != rule.Mode)
        {
            return $"{FormatMode(baseline.Mode)} -> {FormatMode(rule.Mode)}";
        }

        if (baselinePreset != rulePreset)
        {
            return $"{WorkloadPolicyPresetCatalog.Get(baselinePreset).DisplayName} -> {WorkloadPolicyPresetCatalog.Get(rulePreset).DisplayName}";
        }

        return "Regex changed";
    }

    private static string FormatProfile(ProtectionSelectionRule rule)
    {
        if (rule.Mode == ProtectionSelectionMode.RegexScope)
        {
            return "No protection profile";
        }

        return WorkloadPolicyPresetCatalog.Get(rule.WorkloadPreset ?? WorkloadPolicyPresetId.GeneralPurpose).DisplayName;
    }

    private static string FormatMode(ProtectionSelectionMode mode)
    {
        return mode switch
        {
            ProtectionSelectionMode.RecursiveFolder => "Recursive folder",
            ProtectionSelectionMode.ImmediateFiles => "Immediate files",
            ProtectionSelectionMode.File => "File",
            ProtectionSelectionMode.RegexScope => "Regex scope",
            _ => mode.ToString()
        };
    }

    private static string FormatRegexSummary(IReadOnlyList<ProtectionSelectionRule> rules, string path)
    {
        var applicable = rules
            .Where(rule => CoversRegexPath(rule, path))
            .OrderBy(rule => TrimPath(rule.Path).Length)
            .ToArray();
        var include = applicable
            .SelectMany(rule => rule.IncludeRegexRules ?? [])
            .Where(rule => rule.IsEnabled)
            .Select(rule => rule.Pattern)
            .ToArray();
        var exclude = applicable
            .SelectMany(rule => rule.ExcludeRegexRules ?? [])
            .Where(rule => rule.IsEnabled)
            .Select(rule => rule.Pattern)
            .ToArray();
        if (include.Length == 0 && exclude.Length == 0)
        {
            return "None";
        }

        var parts = new List<string>();
        if (include.Length > 0)
        {
            parts.Add("Include: " + string.Join(", ", include));
        }

        if (exclude.Length > 0)
        {
            parts.Add("Exclude: " + string.Join(", ", exclude));
        }

        return string.Join("; ", parts);
    }

    private static bool CoversRegexPath(ProtectionSelectionRule rule, string path)
    {
        return rule.Mode switch
        {
            ProtectionSelectionMode.RegexScope or ProtectionSelectionMode.RecursiveFolder => IsSamePath(path, rule.Path) || IsUnderPath(path, rule.Path),
            ProtectionSelectionMode.ImmediateFiles => IsSamePath(path, rule.Path),
            ProtectionSelectionMode.File => IsSamePath(path, rule.Path),
            _ => false
        };
    }

    private static bool HasRegexRules(ProtectionSelectionRule rule)
    {
        return (rule.IncludeRegexRules?.Any(regex => regex.IsEnabled) ?? false)
               || (rule.ExcludeRegexRules?.Any(regex => regex.IsEnabled) ?? false);
    }

    private static bool RegexRulesEqual(
        IReadOnlyList<ProtectionScopedRegexRule>? left,
        IReadOnlyList<ProtectionScopedRegexRule>? right)
    {
        var leftRules = left ?? [];
        var rightRules = right ?? [];
        return leftRules.SequenceEqual(rightRules);
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
