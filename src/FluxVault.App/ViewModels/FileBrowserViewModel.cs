using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Policies;

namespace FluxVault.App.ViewModels;

public sealed partial class FileBrowserViewModel(
    IFileBrowserFileSystem fileSystem,
    IFileBrowserShellLauncher? shellLauncher = null) : ObservableObject
{
    private readonly Dictionary<string, ProtectionSelectionRule> currentRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProtectionSelectionRule> baselineRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RepositoryVersionSummary> trackedEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProtectionSelectionRule[]> deselectedSubtreeSnapshots = new(StringComparer.OrdinalIgnoreCase);
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
    private string addressPath = string.Empty;

    [ObservableProperty]
    private string navigationStatus = "Enter a file or folder path, then press Enter or Go.";

    [ObservableProperty]
    private WorkloadPolicyPresetId defaultWorkloadPreset = WorkloadPolicyPresetId.GeneralPurpose;

    [ObservableProperty]
    private WorkloadPolicyPresetId selectedWorkloadPreset = WorkloadPolicyPresetId.GeneralPurpose;

    [ObservableProperty]
    private string selectedWorkloadPresetDescription = "Select a protected folder or file to manage its workload preset.";

    private bool isEditingAddressPath;
    private bool isNavigatingAddressPath;
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

    public void LoadTrackedEntries(IReadOnlyList<RepositoryVersionSummary> entries)
    {
        trackedEntries.Clear();
        foreach (var entry in entries)
        {
            trackedEntries[ToTrackedEntryKey(entry.SourcePath, entry.EntryKind)] = entry;
        }

        if (Roots.Count > 0)
        {
            RefreshBrowser();
        }
    }

    public void RefreshBrowser()
    {
        var selectedFolderPath = SelectedFolder?.Path;
        var selectedFilePath = SelectedFile?.Path;
        var expandedFolderPaths = EnumerateFolders(Roots)
            .Where(folder => folder.IsExpanded)
            .Select(folder => folder.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loadedFolderPaths = EnumerateFolders(Roots)
            .Where(folder => folder.HasLoadedChildren)
            .Select(folder => folder.Path)
            .Concat(expandedFolderPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PathDepth)
            .ToArray();

        LoadRoots();
        foreach (var folderPath in loadedFolderPaths)
        {
            var folder = FindFolder(folderPath);
            if (folder is not null)
            {
                folder.IsExpanded = expandedFolderPaths.Contains(folder.Path);
                LoadChildren(folder);
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedFolderPath))
        {
            var selectedFolderNode = FindFolder(selectedFolderPath);
            if (selectedFolderNode is not null)
            {
                SelectedFolder = selectedFolderNode;
                LoadFiles(selectedFolderNode);
                if (!string.IsNullOrWhiteSpace(selectedFilePath))
                {
                    SelectedFile = Files.FirstOrDefault(file =>
                        string.Equals(file.Path, selectedFilePath, StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                SelectedFolder = null;
                Files.Clear();
                SelectedFile = null;
            }
        }

        RefreshTreeIndicators(Roots);
    }

    public void LoadSelectionRules(IReadOnlyList<ProtectionSelectionRule> rules)
    {
        baselineRules.Clear();
        currentRules.Clear();
        deselectedSubtreeSnapshots.Clear();
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

    public void BeginAddressPathEdit()
    {
        isEditingAddressPath = true;
    }

    public void EndAddressPathEdit()
    {
        isEditingAddressPath = false;
    }

    public void LoadChildren(FileBrowserFolderNode folder)
    {
        if (folder.HasLoadedChildren || !folder.IsAccessible)
        {
            return;
        }

        folder.Children.Clear();
        if (!folder.IsPhantom)
        {
            var liveChildPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in fileSystem.GetChildFolders(folder.Path))
            {
                liveChildPaths.Add(Path.GetFullPath(child.Path));
                var node = ToNode(child);
                ApplySelectionToNode(node);
                folder.Children.Add(node);
            }

            foreach (var child in GetPhantomChildFolders(folder.Path, liveChildPaths))
            {
                ApplySelectionToNode(child);
                folder.Children.Add(child);
            }
        }

        folder.HasLoadedChildren = true;
        RefreshTreeIndicators(Roots);
    }

    public void ToggleFolderSelection(FileBrowserFolderNode folder)
    {
        if (!folder.CanChangeSelection)
        {
            return;
        }

        if (!HasSubtreeSelectionRules(folder.Path)
            && deselectedSubtreeSnapshots.TryGetValue(folder.Path, out var restoredSnapshot))
        {
            foreach (var rule in restoredSnapshot)
            {
                currentRules[rule.Path] = rule;
            }

            deselectedSubtreeSnapshots.Remove(folder.Path);
            RefreshPendingChanges();
            SelectionRulesChanged?.Invoke(this, EventArgs.Empty);
            ApplySelectionToTree([folder]);
            ApplySelectionToTree(Roots);
            return;
        }

        currentRules.TryGetValue(folder.Path, out var existingRule);
        var currentMode = existingRule?.Mode == ProtectionSelectionMode.RegexScope ? null : existingRule?.Mode;
        var next = currentMode switch
        {
            null => (ProtectionSelectionMode?)ProtectionSelectionMode.RecursiveFolder,
            ProtectionSelectionMode.RecursiveFolder => ProtectionSelectionMode.ImmediateFiles,
            ProtectionSelectionMode.ImmediateFiles => null,
            _ => ProtectionSelectionMode.RecursiveFolder
        };

        if (next is null)
        {
            var snapshot = GetSubtreeSelectionRules(folder.Path);
            if (snapshot.Length > 0)
            {
                deselectedSubtreeSnapshots[folder.Path] = snapshot;
            }

            RemoveSubtreeSelectionRules(folder.Path);
        }
        else
        {
            deselectedSubtreeSnapshots.Remove(folder.Path);
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

    private ProtectionSelectionRule[] GetSubtreeSelectionRules(string folderPath)
    {
        return currentRules.Values
            .Where(rule => IsSamePath(rule.Path, folderPath) || IsUnderPath(rule.Path, folderPath))
            .OrderBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool HasSubtreeSelectionRules(string folderPath)
    {
        return currentRules.Keys.Any(path => IsSamePath(path, folderPath) || IsUnderPath(path, folderPath));
    }

    private void RemoveSubtreeSelectionRules(string folderPath)
    {
        var removedAny = false;
        foreach (var path in currentRules.Keys
                     .Where(path => IsSamePath(path, folderPath) || IsUnderPath(path, folderPath))
                     .ToArray())
        {
            removedAny |= currentRules.Remove(path);
        }

        if (removedAny)
        {
            RefreshPendingChanges();
            SelectionRulesChanged?.Invoke(this, EventArgs.Empty);
        }
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
        RefreshBrowser();
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
        if (SelectedFolder is { CanUseShellActions: true })
        {
            shellLauncher.ShowFolder(SelectedFolder.Path);
        }
    }

    [RelayCommand]
    private void OpenSelectedFile()
    {
        if (SelectedFile is { CanUseShellActions: true })
        {
            shellLauncher.OpenFile(SelectedFile.Path);
        }
    }

    [RelayCommand]
    private void ShowSelectedFileInExplorer()
    {
        if (SelectedFile is { CanUseShellActions: true })
        {
            shellLauncher.ShowFileInExplorer(SelectedFile.Path);
        }
    }

    [RelayCommand]
    private void NavigateAddress()
    {
        var requestedPath = AddressPath;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            NavigationStatus = "Enter a file or folder path before navigating.";
            return;
        }

        if (!NavigateToPath(requestedPath))
        {
            NavigationStatus = $"Path is not available or tracked: {requestedPath}";
        }
    }

    partial void OnSelectedFolderChanged(FileBrowserFolderNode? value)
    {
        if (value is not null)
        {
            SelectedFile = null;
            UpdateAddressPathFromSelection(value.Path);
            SelectFolder(value);
            LoadRegexText(value.Path);
            LoadWorkloadPresetText(value.Path);
        }
    }

    partial void OnSelectedFileChanged(FileBrowserFileRow? value)
    {
        if (value is not null)
        {
            UpdateAddressPathFromSelection(value.Path);
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

        var liveFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!folder.IsPhantom)
        {
            foreach (var file in fileSystem.GetFiles(folder.Path))
            {
                liveFilePaths.Add(Path.GetFullPath(file.Path));
                Files.Add(CreateFileRow(
                    file.Path,
                    file.Name,
                    file.Length,
                    isPhantom: false,
                    restorableVersionId: null));
            }
        }

        foreach (var entry in GetPhantomChildFiles(folder.Path, liveFilePaths))
        {
            Files.Add(CreateFileRow(
                entry.SourcePath,
                Path.GetFileName(entry.SourcePath),
                entry.LogicalLength,
                isPhantom: true,
                restorableVersionId: entry.VersionId));
        }
    }

    public bool NavigateToPath(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            NavigationStatus = $"Invalid path: {path}";
            return false;
        }

        var wasNavigatingAddressPath = isNavigatingAddressPath;
        isNavigatingAddressPath = true;
        try
        {
            if (TryNavigateToFolder(fullPath))
            {
                NavigationStatus = $"Selected folder: {fullPath}";
                return true;
            }

            var fileEntry = FindTrackedEntry(fullPath, RepositoryEntryKind.File);
            if (File.Exists(fullPath) || fileEntry is not null)
            {
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(parent) && TryNavigateToFolder(parent))
                {
                    SelectedFile = Files.FirstOrDefault(file => IsSamePath(file.Path, fullPath));
                    if (SelectedFile is not null)
                    {
                        NavigationStatus = $"Selected file: {fullPath}";
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            isNavigatingAddressPath = wasNavigatingAddressPath;
        }
    }

    private FileBrowserFileRow CreateFileRow(
        string path,
        string name,
        long length,
        bool isPhantom,
        string? restorableVersionId)
    {
        var row = new FileBrowserFileRow(
            path,
            name,
            length,
            currentRules.ContainsKey(Path.GetFullPath(path)),
            isPhantom,
            restorableVersionId);
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FileBrowserFileRow.IsSelected) && row.CanChangeSelection)
            {
                ToggleFileSelection(row);
            }
        };
        return row;
    }

    private IEnumerable<FileBrowserFolderNode> GetPhantomChildFolders(string parentPath, ISet<string> liveChildPaths)
    {
        return trackedEntries.Values
            .Where(entry => entry.EntryKind == RepositoryEntryKind.Folder)
            .Where(entry => IsImmediateChild(entry.SourcePath, parentPath))
            .Where(entry => entry.IsDeleted || !Directory.Exists(entry.SourcePath))
            .Where(entry => !liveChildPaths.Contains(Path.GetFullPath(entry.SourcePath)))
            .OrderBy(entry => Path.GetFileName(entry.SourcePath), StringComparer.OrdinalIgnoreCase)
            .Select(entry => new FileBrowserFolderNode(
                entry.SourcePath,
                Path.GetFileName(TrimPath(entry.SourcePath)),
                isAccessible: true,
                errorMessage: null,
                isPhantom: true,
                restorableVersionId: entry.VersionId));
    }

    private IEnumerable<RepositoryVersionSummary> GetPhantomChildFiles(string parentPath, ISet<string> liveFilePaths)
    {
        return trackedEntries.Values
            .Where(entry => entry.EntryKind == RepositoryEntryKind.File)
            .Where(entry => IsImmediateChild(entry.SourcePath, parentPath))
            .Where(entry => entry.IsDeleted || !File.Exists(entry.SourcePath))
            .Where(entry => !liveFilePaths.Contains(Path.GetFullPath(entry.SourcePath)))
            .OrderBy(entry => Path.GetFileName(entry.SourcePath), StringComparer.OrdinalIgnoreCase);
    }

    private bool TryNavigateToFolder(string folderPath)
    {
        if (Roots.Count == 0)
        {
            LoadRoots();
        }

        var fullPath = Path.GetFullPath(folderPath);
        var root = Roots.FirstOrDefault(root => IsSamePath(fullPath, root.Path) || IsUnderPath(fullPath, root.Path));
        if (root is null)
        {
            return false;
        }

        var pathChain = BuildFolderPathChain(root.Path, fullPath);
        var current = root;
        foreach (var candidatePath in pathChain.Skip(1))
        {
            current.IsExpanded = true;
            LoadChildren(current);
            var next = current.Children.FirstOrDefault(child => IsSamePath(child.Path, candidatePath));
            if (next is null)
            {
                return false;
            }

            current = next;
        }

        SelectedFolder = current;
        return true;
    }

    private void UpdateAddressPathFromSelection(string path)
    {
        if (!isEditingAddressPath || isNavigatingAddressPath)
        {
            AddressPath = path;
        }
    }

    private RepositoryVersionSummary? FindTrackedEntry(string path, RepositoryEntryKind entryKind)
    {
        return trackedEntries.TryGetValue(ToTrackedEntryKey(path, entryKind), out var entry) ? entry : null;
    }

    private static IReadOnlyList<string> BuildFolderPathChain(string rootPath, string targetPath)
    {
        var root = TrimPath(rootPath);
        var current = TrimPath(targetPath);
        var chain = new Stack<string>();
        while (!string.IsNullOrWhiteSpace(current) && !IsSamePath(current, root))
        {
            chain.Push(current);
            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        chain.Push(root);
        return chain.ToArray();
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

    private FileBrowserFolderNode? FindFolder(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return EnumerateFolders(Roots)
            .FirstOrDefault(folder => string.Equals(folder.Path, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<FileBrowserFolderNode> EnumerateFolders(IEnumerable<FileBrowserFolderNode> folders)
    {
        foreach (var folder in folders)
        {
            yield return folder;
            foreach (var child in EnumerateFolders(folder.Children))
            {
                yield return child;
            }
        }
    }

    private static int PathDepth(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Count(character => character is '\\' or '/');
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
        var trimmedRoot = TrimPath(root);
        var rootPrefix = EndsWithDirectorySeparator(trimmedRoot)
            ? trimmedRoot
            : trimmedRoot + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImmediateChild(string childPath, string parentPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(childPath));
        return parent is not null && IsSamePath(parent, parentPath);
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(TrimPath(left), TrimPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return trimmed;
    }

    private static bool EndsWithDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
               || path.EndsWith(Path.AltDirectorySeparatorChar);
    }

    private static string ToTrackedEntryKey(string path, RepositoryEntryKind entryKind)
    {
        return $"{entryKind}:{TrimPath(path)}";
    }
}
