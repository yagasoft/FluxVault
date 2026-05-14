using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class FileBrowserViewModelTests
{
    [Fact]
    public void Folder_selection_cycles_recursive_immediate_none()
    {
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem(
            [Folder(@"D:\")],
            [Folder(@"D:\Work")],
            []));
        var folder = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        viewModel.LoadSelectionRules([]);

        viewModel.ToggleFolderSelection(folder);
        Assert.Equal(ProtectionSelectionMode.RecursiveFolder, folder.SelectionMode);

        viewModel.ToggleFolderSelection(folder);
        Assert.Equal(ProtectionSelectionMode.ImmediateFiles, folder.SelectionMode);

        viewModel.ToggleFolderSelection(folder);
        Assert.Null(folder.SelectionMode);
    }

    [Fact]
    public void Folder_selection_exposes_checkbox_style_visual_states()
    {
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        var root = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var child = new FileBrowserFolderNode(@"D:\Work\Child", "Child", isAccessible: true, errorMessage: null);
        root.Children.Add(child);
        viewModel.LoadSelectionRules([]);

        Assert.Equal(FileBrowserSelectionVisualState.Empty, root.SelectionVisualState);

        viewModel.ToggleFolderSelection(root);
        Assert.Equal(FileBrowserSelectionVisualState.Checked, root.SelectionVisualState);
        Assert.Contains("recursive", root.SelectionToolTip, StringComparison.OrdinalIgnoreCase);

        viewModel.ToggleFolderSelection(root);
        Assert.Equal(FileBrowserSelectionVisualState.Indeterminate, root.SelectionVisualState);
        Assert.Contains("immediate", root.SelectionToolTip, StringComparison.OrdinalIgnoreCase);

        viewModel.ToggleFolderSelection(root);
        viewModel.ToggleFolderSelection(child);
        viewModel.RefreshTreeIndicators([root]);
        Assert.Equal(FileBrowserSelectionVisualState.ChildSelected, root.SelectionVisualState);
        Assert.Contains("child", root.SelectionToolTip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manual_child_selection_reappears_when_recursive_parent_is_removed()
    {
        var root = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var child = new FileBrowserFolderNode(@"D:\Work\Child", "Child", isAccessible: true, errorMessage: null);
        root.Children.Add(child);
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules(
            [
                Rule("child", child.Path, ProtectionSelectionMode.ImmediateFiles)
            ]);

        viewModel.ToggleFolderSelection(root);
        Assert.Equal(ProtectionSelectionMode.RecursiveFolder, root.SelectionMode);
        Assert.Equal(ProtectionSelectionMode.ImmediateFiles, child.SelectionMode);

        viewModel.ToggleFolderSelection(root);
        Assert.Equal(ProtectionSelectionMode.ImmediateFiles, root.SelectionMode);
        Assert.Equal(ProtectionSelectionMode.ImmediateFiles, child.SelectionMode);
    }

    [Fact]
    public void Parent_reports_descendant_selection_indicator()
    {
        var root = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var child = new FileBrowserFolderNode(@"D:\Work\Child", "Child", isAccessible: true, errorMessage: null);
        root.Children.Add(child);
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules([]);

        viewModel.ToggleFolderSelection(child);
        viewModel.RefreshTreeIndicators([root]);

        Assert.True(root.HasDescendantSelection);
    }

    [Fact]
    public void Pending_changes_report_added_removed_and_changed_rules()
    {
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules(
            [
                Rule("removed", @"D:\Removed", ProtectionSelectionMode.RecursiveFolder),
                Rule("changed", @"D:\Changed", ProtectionSelectionMode.RecursiveFolder)
            ]);

        viewModel.ReplaceSelectionRule(Rule("changed", @"D:\Changed", ProtectionSelectionMode.ImmediateFiles));
        viewModel.ReplaceSelectionRule(Rule("added", @"D:\Added", ProtectionSelectionMode.File));
        viewModel.RemoveSelectionRule(@"D:\Removed");

        Assert.Contains(viewModel.PendingChanges, row => row.Change == "Added" && row.Path == Path.GetFullPath(@"D:\Added"));
        Assert.Contains(viewModel.PendingChanges, row => row.Change == "Removed" && row.Path == Path.GetFullPath(@"D:\Removed"));
        Assert.Contains(viewModel.PendingChanges, row => row.Change == "Changed" && row.Path == Path.GetFullPath(@"D:\Changed"));
    }

    [Fact]
    public void Explorer_add_folder_creates_immediate_folder_selection_and_is_idempotent()
    {
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules([]);

        viewModel.AddPathSelection(@"D:\Work\Docs", isDirectory: true);
        viewModel.AddPathSelection(@"D:\Work\Docs", isDirectory: true);

        var rule = Assert.Single(viewModel.GetSelectionRules());
        Assert.Equal(Path.GetFullPath(@"D:\Work\Docs"), rule.Path);
        Assert.Equal(ProtectionSelectionMode.ImmediateFiles, rule.Mode);
    }

    [Fact]
    public void New_folder_selection_uses_configured_default_workload_preset()
    {
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []))
        {
            DefaultWorkloadPreset = WorkloadPolicyPresetId.DeveloperWorkspace
        };
        viewModel.LoadSelectionRules([]);

        viewModel.AddPathSelection(@"D:\Work\Repo", isDirectory: true);

        var rule = Assert.Single(viewModel.GetSelectionRules());
        Assert.Equal(WorkloadPolicyPresetId.DeveloperWorkspace, rule.WorkloadPreset);
    }

    [Fact]
    public void Selected_rule_workload_preset_change_is_pending_until_saved()
    {
        var folder = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules([Rule("work", @"D:\Work", ProtectionSelectionMode.RecursiveFolder) with
        {
            WorkloadPreset = WorkloadPolicyPresetId.GeneralPurpose
        }]);

        viewModel.SelectedFolder = folder;
        viewModel.SelectedWorkloadPreset = WorkloadPolicyPresetId.CadBim;

        var rule = Assert.Single(viewModel.GetSelectionRules());
        Assert.Equal(WorkloadPolicyPresetId.CadBim, rule.WorkloadPreset);
        Assert.Contains(viewModel.PendingChanges, row => row.Change == "Changed" && row.Detail.Contains("CAD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Explorer_add_file_creates_file_selection_and_is_idempotent()
    {
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules([]);

        viewModel.AddPathSelection(@"D:\Work\Docs\brief.docx", isDirectory: false);
        viewModel.AddPathSelection(@"D:\Work\Docs\brief.docx", isDirectory: false);

        var rule = Assert.Single(viewModel.GetSelectionRules());
        Assert.Equal(Path.GetFullPath(@"D:\Work\Docs\brief.docx"), rule.Path);
        Assert.Equal(ProtectionSelectionMode.File, rule.Mode);
    }

    [Fact]
    public void Explorer_remove_direct_selection_removes_rule()
    {
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules([Rule("docs", @"D:\Work\Docs", ProtectionSelectionMode.ImmediateFiles)]);

        viewModel.RemovePathSelection(@"D:\Work\Docs", isDirectory: true);

        Assert.Empty(viewModel.GetSelectionRules());
    }

    [Fact]
    public void Explorer_remove_inherited_selection_adds_scoped_exclusion_to_nearest_parent()
    {
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules([Rule("root", @"D:\Work", ProtectionSelectionMode.RecursiveFolder)]);

        viewModel.RemovePathSelection(@"D:\Work\Docs\brief.docx", isDirectory: false);

        var rule = Assert.Single(viewModel.GetSelectionRules());
        Assert.Equal(Path.GetFullPath(@"D:\Work"), rule.Path);
        Assert.Contains(rule.ExcludeRegexRules ?? [], regex => regex.Pattern.Contains("Docs") && regex.Pattern.Contains("brief\\.docx"));
    }

    [Fact]
    public void Folder_node_indicates_local_scoped_regex_rules()
    {
        var node = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules(
            [
                Rule("work", @"D:\Work", ProtectionSelectionMode.RecursiveFolder) with
                {
                    IncludeRegexRules =
                    [
                        new ProtectionScopedRegexRule("include-docx", @"\.docx$", ProtectionExclusionTarget.File)
                    ]
                }
            ]);

        viewModel.RefreshTreeIndicators([node]);

        Assert.True(node.HasLocalRegexRules);
        Assert.Equal("R", node.RegexIndicator);
    }

    [Fact]
    public void Regex_can_be_defined_on_unselected_folder_as_scope_only_rule()
    {
        var node = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules([]);

        viewModel.SelectedFolder = node;
        viewModel.SelectedIncludeRegexText = @"\.docx$";
        viewModel.SelectedExcludeRegexText = @"\\draft";
        viewModel.ApplySelectedRegexRulesCommand.Execute(null);

        var rule = Assert.Single(viewModel.GetSelectionRules());
        Assert.Equal(ProtectionSelectionMode.RegexScope, rule.Mode);
        Assert.Equal(Path.GetFullPath(@"D:\Work"), rule.Path);
        Assert.Single(rule.IncludeRegexRules ?? []);
        Assert.Single(rule.ExcludeRegexRules ?? []);
        Assert.Contains(viewModel.PendingChanges, row =>
            row.Change == "Added"
            && row.Path == Path.GetFullPath(@"D:\Work")
            && row.Profile == "No protection profile"
            && row.Regex.Contains(@"\.docx$", StringComparison.Ordinal));
    }

    [Fact]
    public void File_checkbox_toggle_adds_and_removes_individual_file_rule()
    {
        var folder = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var viewModel = new FileBrowserViewModel(
            new FakeFileBrowserFileSystem([], [], [new FileBrowserFileInfo(@"D:\Work\brief.docx", "brief.docx", 10)]));
        viewModel.LoadSelectionRules([]);
        viewModel.SelectFolder(folder);
        var row = Assert.Single(viewModel.Files);

        row.IsSelected = true;

        var rule = Assert.Single(viewModel.GetSelectionRules());
        Assert.Equal(ProtectionSelectionMode.File, rule.Mode);
        Assert.Equal(Path.GetFullPath(@"D:\Work\brief.docx"), rule.Path);

        row.IsSelected = false;

        Assert.Empty(viewModel.GetSelectionRules());
    }

    [Fact]
    public void File_browser_context_commands_launch_selected_paths_through_shell_abstraction()
    {
        var shell = new FakeFileBrowserShellLauncher();
        var folder = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var viewModel = new FileBrowserViewModel(
            new FakeFileBrowserFileSystem([], [], [new FileBrowserFileInfo(@"D:\Work\brief.docx", "brief.docx", 10)]),
            shell);
        viewModel.LoadSelectionRules([]);
        viewModel.SelectFolder(folder);
        viewModel.SelectedFile = Assert.Single(viewModel.Files);

        viewModel.ShowSelectedFolderInExplorerCommand.Execute(null);
        viewModel.OpenSelectedFileCommand.Execute(null);
        viewModel.ShowSelectedFileInExplorerCommand.Execute(null);

        Assert.Equal([Path.GetFullPath(@"D:\Work")], shell.ShownFolders);
        Assert.Equal([Path.GetFullPath(@"D:\Work\brief.docx")], shell.OpenedFiles);
        Assert.Equal([Path.GetFullPath(@"D:\Work\brief.docx")], shell.SelectedFiles);
    }

    [Fact]
    public async Task Main_window_save_sends_compiled_file_browser_rules_only_after_save()
    {
        var client = new FakeFluxVaultServiceClient(Status());
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();

        viewModel.FileBrowser.ReplaceSelectionRule(Rule("docs", @"D:\Work\Docs", ProtectionSelectionMode.RecursiveFolder));
        Assert.DoesNotContain(FluxVaultIpcCommand.SaveConfiguration, client.Commands);

        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        var saved = Assert.Single(client.SavedConfigurations);
        Assert.Single(saved.SelectionRules);
        var watched = Assert.Single(saved.WatchedFolders);
        Assert.Equal(Path.GetFullPath(@"D:\Work\Docs"), watched.Path);
        Assert.True(watched.Recursive);
    }

    private static ProtectionSelectionRule Rule(string id, string path, ProtectionSelectionMode mode)
    {
        return new ProtectionSelectionRule(
            id,
            Path.GetFullPath(path),
            mode,
            CompressionPreference.Zstd,
            ResourceProfile.Balanced,
            IsEnabled: true);
    }

    private static FileBrowserFolderInfo Folder(string path)
    {
        return new FileBrowserFolderInfo(Path.GetFullPath(path), Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path, true, null);
    }

    private static FluxVaultServiceStatus Status()
    {
        return new FluxVaultServiceStatus(
            IsServiceRunning: true,
            Configuration: FluxVaultConfiguration.CreateDefault(@"D:\Vault"),
            LastMessage: "Ready",
            LastCaptureUtc: null,
            WatchedFolders: [],
            RecentVersions: []);
    }

    private sealed class FakeFileBrowserFileSystem(
        IReadOnlyList<FileBrowserFolderInfo> roots,
        IReadOnlyList<FileBrowserFolderInfo> folders,
        IReadOnlyList<FileBrowserFileInfo> files) : IFileBrowserFileSystem
    {
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots()
        {
            return roots;
        }

        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path)
        {
            return folders;
        }

        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path)
        {
            return files;
        }
    }

    private sealed class FakeFileBrowserShellLauncher : IFileBrowserShellLauncher
    {
        public List<string> ShownFolders { get; } = [];

        public List<string> OpenedFiles { get; } = [];

        public List<string> SelectedFiles { get; } = [];

        public void ShowFolder(string folderPath)
        {
            ShownFolders.Add(Path.GetFullPath(folderPath));
        }

        public void OpenFile(string filePath)
        {
            OpenedFiles.Add(Path.GetFullPath(filePath));
        }

        public void ShowFileInExplorer(string filePath)
        {
            SelectedFiles.Add(Path.GetFullPath(filePath));
        }
    }

    private sealed class FakeFluxVaultServiceClient(FluxVaultServiceStatus status) : IFluxVaultServiceClient
    {
        public List<FluxVaultIpcCommand> Commands { get; } = [];
        public List<FluxVaultConfiguration> SavedConfigurations { get; } = [];

        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            if (request.Command == FluxVaultIpcCommand.SaveConfiguration)
            {
                SavedConfigurations.Add(request.Configuration ?? throw new InvalidOperationException("Missing config."));
                return Task.FromResult(FluxVaultIpcResponse.Ok());
            }

            return Task.FromResult(FluxVaultIpcResponse.WithStatus(status));
        }
    }
}
