using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class FileBrowserViewModelTests
{
    [Fact]
    public void Folder_selection_cycles_recursive_immediate_deselected()
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
    public void Folder_deselect_all_clears_and_restores_full_subtree_rules()
    {
        var root = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var child = new FileBrowserFolderNode(@"D:\Work\Child", "Child", isAccessible: true, errorMessage: null);
        root.Children.Add(child);
        var regexRule = Rule("regex", @"D:\Work\RegexScope", ProtectionSelectionMode.RegexScope) with
        {
            IncludeRegexRules =
            [
                new ProtectionScopedRegexRule("include-docx", @"\.docx$", ProtectionExclusionTarget.File)
            ]
        };
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules(
            [
                Rule("root", root.Path, ProtectionSelectionMode.ImmediateFiles),
                Rule("child", child.Path, ProtectionSelectionMode.RecursiveFolder),
                Rule("file", @"D:\Work\Child\brief.docx", ProtectionSelectionMode.File),
                regexRule
            ]);

        viewModel.ToggleFolderSelection(root);

        Assert.Empty(viewModel.GetSelectionRules());
        Assert.Null(root.SelectionMode);
        Assert.Null(child.SelectionMode);

        viewModel.ToggleFolderSelection(root);

        var restored = viewModel.GetSelectionRules();
        Assert.Contains(restored, rule => rule.Path == root.Path && rule.Mode == ProtectionSelectionMode.ImmediateFiles);
        Assert.Contains(restored, rule => rule.Path == child.Path && rule.Mode == ProtectionSelectionMode.RecursiveFolder);
        Assert.Contains(restored, rule => rule.Path == Path.GetFullPath(@"D:\Work\Child\brief.docx") && rule.Mode == ProtectionSelectionMode.File);
        Assert.Contains(restored, rule =>
            rule.Path == Path.GetFullPath(@"D:\Work\RegexScope")
            && rule.Mode == ProtectionSelectionMode.RegexScope
            && Assert.Single(rule.IncludeRegexRules ?? []).Pattern == @"\.docx$");
    }

    [Fact]
    public void Folder_deselect_restore_snapshot_is_cleared_when_rules_reload()
    {
        var root = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var child = new FileBrowserFolderNode(@"D:\Work\Child", "Child", isAccessible: true, errorMessage: null);
        root.Children.Add(child);
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.LoadSelectionRules(
            [
                Rule("root", root.Path, ProtectionSelectionMode.ImmediateFiles),
                Rule("child", child.Path, ProtectionSelectionMode.RecursiveFolder)
            ]);
        viewModel.ToggleFolderSelection(root);

        viewModel.LoadSelectionRules([]);
        viewModel.ToggleFolderSelection(root);

        var rule = Assert.Single(viewModel.GetSelectionRules());
        Assert.Equal(root.Path, rule.Path);
        Assert.Equal(ProtectionSelectionMode.RecursiveFolder, rule.Mode);
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
    public void Phantom_tracked_file_is_merged_and_shell_actions_are_disabled()
    {
        var shell = new FakeFileBrowserShellLauncher();
        var folder = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []), shell);
        viewModel.LoadSelectionRules([]);
        viewModel.LoadTrackedEntries(
            [
                Tracked("deleted", @"D:\Work\missing.txt", RepositoryEntryKind.File, isDeleted: true)
            ]);

        viewModel.SelectFolder(folder);
        viewModel.SelectedFile = Assert.Single(viewModel.Files);
        viewModel.OpenSelectedFileCommand.Execute(null);
        viewModel.ShowSelectedFileInExplorerCommand.Execute(null);

        Assert.True(viewModel.SelectedFile.IsPhantom);
        Assert.False(viewModel.SelectedFile.CanUseShellActions);
        Assert.Empty(shell.OpenedFiles);
        Assert.Empty(shell.SelectedFiles);
    }

    [Fact]
    public void Address_navigation_selects_tracked_phantom_file()
    {
        var viewModel = new FileBrowserViewModel(new MutableFileBrowserFileSystem(
            [Folder(@"D:\")],
            new Dictionary<string, IReadOnlyList<FileBrowserFolderInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(@"D:\")] = [Folder(@"D:\Work")]
            },
            new Dictionary<string, IReadOnlyList<FileBrowserFileInfo>>(StringComparer.OrdinalIgnoreCase)));
        viewModel.LoadSelectionRules([]);
        viewModel.LoadTrackedEntries(
            [
                Tracked("deleted", @"D:\Work\missing.txt", RepositoryEntryKind.File, isDeleted: true)
            ]);
        viewModel.LoadRoots();

        var navigated = viewModel.NavigateToPath(@"D:\Work\missing.txt");

        Assert.True(navigated);
        Assert.NotNull(viewModel.SelectedFile);
        Assert.Equal(Path.GetFullPath(@"D:\Work\missing.txt"), viewModel.SelectedFile.Path);
        Assert.True(viewModel.SelectedFile.IsPhantom);
    }

    [Fact]
    public void Address_navigation_expands_ancestors_to_selected_folder()
    {
        var viewModel = new FileBrowserViewModel(new MutableFileBrowserFileSystem(
            [Folder(@"D:\")],
            new Dictionary<string, IReadOnlyList<FileBrowserFolderInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(@"D:\")] = [Folder(@"D:\Work")],
                [Path.GetFullPath(@"D:\Work")] = [Folder(@"D:\Work\Project")]
            },
            new Dictionary<string, IReadOnlyList<FileBrowserFileInfo>>(StringComparer.OrdinalIgnoreCase)));
        viewModel.LoadSelectionRules([]);
        viewModel.LoadRoots();

        var navigated = viewModel.NavigateToPath(@"D:\Work\Project");

        Assert.True(navigated);
        var root = Assert.Single(viewModel.Roots);
        var work = Assert.Single(root.Children);
        Assert.True(root.IsExpanded);
        Assert.True(work.IsExpanded);
        Assert.NotNull(viewModel.SelectedFolder);
        Assert.Equal(Path.GetFullPath(@"D:\Work\Project"), viewModel.SelectedFolder.Path);
    }

    [Fact]
    public void Refresh_preserves_selected_folder_reloads_files_and_refreshes_loaded_children()
    {
        var fileSystem = new MutableFileBrowserFileSystem(
            [Folder(@"D:\")],
            new Dictionary<string, IReadOnlyList<FileBrowserFolderInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(@"D:\")] = [Folder(@"D:\Work")],
                [Path.GetFullPath(@"D:\Work")] = [Folder(@"D:\Work\Old")]
            },
            new Dictionary<string, IReadOnlyList<FileBrowserFileInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(@"D:\Work")] = [new FileBrowserFileInfo(@"D:\Work\old.txt", "old.txt", 10)]
            });
        var viewModel = new FileBrowserViewModel(fileSystem);
        viewModel.LoadSelectionRules([]);
        viewModel.LoadRoots();
        var root = Assert.Single(viewModel.Roots);
        viewModel.LoadChildren(root);
        var work = Assert.Single(root.Children);
        viewModel.SelectFolder(work);
        viewModel.LoadChildren(work);

        fileSystem.ChildrenByPath[Path.GetFullPath(@"D:\Work")] =
            [Folder(@"D:\Work\New")];
        fileSystem.FilesByPath[Path.GetFullPath(@"D:\Work")] =
            [new FileBrowserFileInfo(@"D:\Work\new.txt", "new.txt", 20)];

        viewModel.RefreshBrowser();

        Assert.NotNull(viewModel.SelectedFolder);
        Assert.Equal(Path.GetFullPath(@"D:\Work"), viewModel.SelectedFolder.Path);
        Assert.Equal("New", Assert.Single(viewModel.SelectedFolder.Children).Name);
        Assert.Equal("new.txt", Assert.Single(viewModel.Files).Name);
    }

    [Fact]
    public void Tracked_entry_refresh_updates_visible_phantoms_without_rebuilding_selection_or_address_edit()
    {
        var fileSystem = new MutableFileBrowserFileSystem(
            [Folder(@"D:\")],
            new Dictionary<string, IReadOnlyList<FileBrowserFolderInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(@"D:\")] = [Folder(@"D:\Work")]
            },
            new Dictionary<string, IReadOnlyList<FileBrowserFileInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(@"D:\Work")] = [new FileBrowserFileInfo(@"D:\Work\live.txt", "live.txt", 10)]
            });
        var viewModel = new FileBrowserViewModel(fileSystem);
        viewModel.LoadSelectionRules([]);
        viewModel.LoadRoots();
        var root = Assert.Single(viewModel.Roots);
        viewModel.LoadChildren(root);
        var work = Assert.Single(root.Children);
        viewModel.SelectFolder(work);
        viewModel.SelectedFile = Assert.Single(viewModel.Files);
        var selectedFolder = viewModel.SelectedFolder;
        var selectedFile = viewModel.SelectedFile;
        viewModel.BeginAddressPathEdit();
        viewModel.AddressPath = @"D:\Typed\While\Refresh";

        viewModel.LoadTrackedEntries(
            [
                Tracked("deleted", @"D:\Work\missing.txt", RepositoryEntryKind.File, isDeleted: true)
            ]);

        Assert.Same(selectedFolder, viewModel.SelectedFolder);
        Assert.Same(selectedFile, viewModel.SelectedFile);
        Assert.Equal(@"D:\Typed\While\Refresh", viewModel.AddressPath);
        Assert.Contains(viewModel.Files, file => file.Path == Path.GetFullPath(@"D:\Work\missing.txt") && file.IsPhantom);
    }

    [Fact]
    public void Refresh_preserves_expanded_folders()
    {
        var fileSystem = new MutableFileBrowserFileSystem(
            [Folder(@"D:\")],
            new Dictionary<string, IReadOnlyList<FileBrowserFolderInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(@"D:\")] = [Folder(@"D:\Work")],
                [Path.GetFullPath(@"D:\Work")] = [Folder(@"D:\Work\Project")]
            },
            new Dictionary<string, IReadOnlyList<FileBrowserFileInfo>>(StringComparer.OrdinalIgnoreCase));
        var viewModel = new FileBrowserViewModel(fileSystem);
        viewModel.LoadSelectionRules([]);
        viewModel.LoadRoots();
        var root = Assert.Single(viewModel.Roots);
        viewModel.LoadChildren(root);
        var work = Assert.Single(root.Children);
        root.IsExpanded = true;
        work.IsExpanded = true;

        viewModel.RefreshBrowser();

        root = Assert.Single(viewModel.Roots);
        work = Assert.Single(root.Children);
        Assert.True(root.IsExpanded);
        Assert.True(work.IsExpanded);
    }

    [Fact]
    public void Selection_does_not_overwrite_address_path_while_address_is_being_edited()
    {
        var folder = new FileBrowserFolderNode(@"D:\Work", "Work", isAccessible: true, errorMessage: null);
        var viewModel = new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], []));
        viewModel.AddressPath = @"D:\Typed";

        viewModel.BeginAddressPathEdit();
        viewModel.SelectedFolder = folder;

        Assert.Equal(@"D:\Typed", viewModel.AddressPath);

        viewModel.EndAddressPathEdit();
        viewModel.SelectedFolder = new FileBrowserFolderNode(@"D:\Other", "Other", isAccessible: true, errorMessage: null);

        Assert.Equal(Path.GetFullPath(@"D:\Other"), viewModel.AddressPath);
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

    [Fact]
    public async Task Main_window_save_requires_confirmation_before_purging_removed_selection()
    {
        var source = Path.GetFullPath(@"D:\Work\Docs");
        var baseline = Rule("docs", source, ProtectionSelectionMode.RecursiveFolder);
        var client = new FakeFluxVaultServiceClient(Status(FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
        {
            SelectionRules = [baseline],
            WatchedFolders = ProtectionSelectionCompiler.Compile([baseline])
        }));
        var confirmation = new FakeProtectionRemovalConfirmation(confirm: false);
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], [])),
            new FakeWindowsServiceController(),
            new FakeRestoreDestinationPicker(),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true),
            protectionRemovalConfirmation: confirmation);
        await viewModel.RefreshAsync();

        viewModel.FileBrowser.RemovePathSelection(source, isDirectory: true);
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.ConfirmCount);
        Assert.DoesNotContain(FluxVaultIpcCommand.SaveConfiguration, client.Commands);
    }

    [Fact]
    public async Task Main_window_save_sends_confirmed_removed_selection_purge_scope()
    {
        var source = Path.GetFullPath(@"D:\Work\Docs");
        var baseline = Rule("docs", source, ProtectionSelectionMode.RecursiveFolder);
        var client = new FakeFluxVaultServiceClient(Status(FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
        {
            SelectionRules = [baseline],
            WatchedFolders = ProtectionSelectionCompiler.Compile([baseline])
        }));
        var confirmation = new FakeProtectionRemovalConfirmation(confirm: true);
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], [])),
            new FakeWindowsServiceController(),
            new FakeRestoreDestinationPicker(),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true),
            protectionRemovalConfirmation: confirmation);
        await viewModel.RefreshAsync();

        viewModel.FileBrowser.RemovePathSelection(source, isDirectory: true);
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        var request = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.True(request.PurgeRemovedSelections);
        var scope = Assert.Single(request.RemovedSelections!);
        Assert.Equal(source, scope.SourcePath);
        Assert.Equal(RepositoryPurgeScopeKind.RecursiveFolder, scope.Kind);
    }

    [Fact]
    public async Task Main_window_save_pipe_failure_after_removed_selection_confirmation_preserves_pending_changes()
    {
        var source = Path.GetFullPath(@"D:\Work\Docs");
        var baseline = Rule("docs", source, ProtectionSelectionMode.RecursiveFolder);
        var client = new FakeFluxVaultServiceClient(Status(FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
        {
            SelectionRules = [baseline],
            WatchedFolders = ProtectionSelectionCompiler.Compile([baseline])
        }));
        var confirmation = new FakeProtectionRemovalConfirmation(confirm: true);
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], [])),
            new FakeWindowsServiceController(),
            new FakeRestoreDestinationPicker(),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true),
            protectionRemovalConfirmation: confirmation);
        await viewModel.RefreshAsync();

        client.NextException = new IOException("pipe closed");
        viewModel.FileBrowser.RemovePathSelection(source, isDirectory: true);
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.ConfirmCount);
        Assert.Empty(client.SavedConfigurations);
        Assert.Contains(viewModel.FileBrowser.PendingChanges, change => change.Change == "Removed" && change.Path == source);
        Assert.Contains("unavailable", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pipe closed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Main_window_save_reports_failed_purge_without_reverting_removed_selection()
    {
        var source = Path.GetFullPath(@"D:\Work\Docs");
        var baseline = Rule("docs", source, ProtectionSelectionMode.RecursiveFolder);
        var client = new FakeFluxVaultServiceClient(Status(FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
        {
            SelectionRules = [baseline],
            WatchedFolders = ProtectionSelectionCompiler.Compile([baseline])
        }))
        {
            SaveResponse = FluxVaultIpcResponse.WithPurge(new RepositoryPurgeResult(
                0,
                0,
                0,
                [],
                Success: false,
                ErrorMessage: "metadata offline"))
        };
        var confirmation = new FakeProtectionRemovalConfirmation(confirm: true);
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], [])),
            new FakeWindowsServiceController(),
            new FakeRestoreDestinationPicker(),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true),
            protectionRemovalConfirmation: confirmation);
        await viewModel.RefreshAsync();

        viewModel.FileBrowser.RemovePathSelection(source, isDirectory: true);
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        Assert.Single(client.SavedConfigurations);
        Assert.Empty(viewModel.FileBrowser.PendingChanges);
        Assert.Contains("purge failed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata offline", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("history may remain", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Main_window_save_does_not_prompt_or_purge_regex_only_removal()
    {
        var source = Path.GetFullPath(@"D:\Work\Docs");
        var baseline = Rule("regex", source, ProtectionSelectionMode.RegexScope);
        var client = new FakeFluxVaultServiceClient(Status(FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
        {
            SelectionRules = [baseline],
            WatchedFolders = []
        }));
        var confirmation = new FakeProtectionRemovalConfirmation(confirm: false);
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], [])),
            new FakeWindowsServiceController(),
            new FakeRestoreDestinationPicker(),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true),
            protectionRemovalConfirmation: confirmation);
        await viewModel.RefreshAsync();

        viewModel.FileBrowser.RemovePathSelection(source, isDirectory: true);
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        Assert.Equal(0, confirmation.ConfirmCount);
        var request = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.False(request.PurgeRemovedSelections);
        Assert.Empty(request.RemovedSelections ?? []);
    }

    [Fact]
    public async Task Main_window_save_does_not_prompt_when_selection_expands()
    {
        var source = Path.GetFullPath(@"D:\Work\Docs");
        var baseline = Rule("docs", source, ProtectionSelectionMode.ImmediateFiles);
        var client = new FakeFluxVaultServiceClient(Status(FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
        {
            SelectionRules = [baseline],
            WatchedFolders = ProtectionSelectionCompiler.Compile([baseline])
        }));
        var confirmation = new FakeProtectionRemovalConfirmation(confirm: false);
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new FakeFileBrowserFileSystem([], [], [])),
            new FakeWindowsServiceController(),
            new FakeRestoreDestinationPicker(),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true),
            protectionRemovalConfirmation: confirmation);
        await viewModel.RefreshAsync();

        viewModel.FileBrowser.ReplaceSelectionRule(baseline with { Mode = ProtectionSelectionMode.RecursiveFolder });
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        Assert.Equal(0, confirmation.ConfirmCount);
        var request = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.False(request.PurgeRemovedSelections);
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

    private static FluxVaultServiceStatus Status(FluxVaultConfiguration? configuration = null)
    {
        return new FluxVaultServiceStatus(
            IsServiceRunning: true,
            Configuration: configuration ?? FluxVaultConfiguration.CreateDefault(@"D:\Vault"),
            LastMessage: "Ready",
            LastCaptureUtc: null,
            WatchedFolders: [],
            RecentVersions: []);
    }

    private static RepositoryVersionSummary Tracked(
        string versionId,
        string path,
        RepositoryEntryKind entryKind,
        bool isDeleted)
    {
        return new RepositoryVersionSummary(
            versionId,
            Path.GetFullPath(path),
            DateTimeOffset.UtcNow,
            CaptureConsistency.BestEffort,
            10,
            ChunkCount: entryKind == RepositoryEntryKind.File ? 1 : 0,
            EntryKind: entryKind,
            IsDeleted: isDeleted);
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

    private sealed class MutableFileBrowserFileSystem(
        IReadOnlyList<FileBrowserFolderInfo> roots,
        Dictionary<string, IReadOnlyList<FileBrowserFolderInfo>> childrenByPath,
        Dictionary<string, IReadOnlyList<FileBrowserFileInfo>> filesByPath) : IFileBrowserFileSystem
    {
        public Dictionary<string, IReadOnlyList<FileBrowserFolderInfo>> ChildrenByPath { get; } = childrenByPath;

        public Dictionary<string, IReadOnlyList<FileBrowserFileInfo>> FilesByPath { get; } = filesByPath;

        public IReadOnlyList<FileBrowserFolderInfo> GetRoots()
        {
            return roots;
        }

        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path)
        {
            return ChildrenByPath.TryGetValue(Path.GetFullPath(path), out var folders) ? folders : [];
        }

        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path)
        {
            return FilesByPath.TryGetValue(Path.GetFullPath(path), out var fileRows) ? fileRows : [];
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

    private sealed class FakeFluxVaultServiceClient(FluxVaultServiceStatus initialStatus) : IFluxVaultServiceClient
    {
        private FluxVaultServiceStatus status = initialStatus;

        public List<FluxVaultIpcCommand> Commands { get; } = [];
        public List<FluxVaultConfiguration> SavedConfigurations { get; } = [];
        public List<FluxVaultIpcRequest> Requests { get; } = [];

        public Exception? NextException { get; set; }

        public FluxVaultIpcResponse? SaveResponse { get; set; }

        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            Requests.Add(request);
            if (NextException is not null)
            {
                var exception = NextException;
                NextException = null;
                throw exception;
            }

            if (request.Command == FluxVaultIpcCommand.SaveConfiguration)
            {
                var configuration = request.Configuration ?? throw new InvalidOperationException("Missing config.");
                SavedConfigurations.Add(configuration);
                status = status with { Configuration = configuration };
                return Task.FromResult(SaveResponse ?? FluxVaultIpcResponse.Ok());
            }

            return Task.FromResult(FluxVaultIpcResponse.WithStatus(status));
        }
    }

    private sealed class FakeProtectionRemovalConfirmation(bool confirm) : IProtectionRemovalConfirmation
    {
        public int ConfirmCount { get; private set; }

        public IReadOnlyList<RepositoryPurgeScope>? LastScopes { get; private set; }

        public bool ConfirmPurge(IReadOnlyList<RepositoryPurgeScope> scopes)
        {
            ConfirmCount++;
            LastScopes = scopes;
            return confirm;
        }
    }

    private sealed class FakeRestoreDestinationPicker : IRestoreDestinationPicker
    {
        public string? PickDestination(VersionRow version)
        {
            return null;
        }

        public string? PickFolderDestination(string sourcePath)
        {
            return null;
        }
    }

    private sealed class FakeRestoreOverwriteConfirmation(bool confirmOverwrite) : IRestoreOverwriteConfirmation
    {
        public bool ConfirmOverwrite(string destinationPath)
        {
            return confirmOverwrite;
        }
    }

    private sealed class FakeWindowsServiceController : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new FluxVaultWindowsServiceStatus(
                "FluxVaultService",
                FluxVaultWindowsServiceState.Running,
                "FluxVault service is running."));
        }

        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default)
        {
            var status = new FluxVaultWindowsServiceStatus(
                "FluxVaultService",
                FluxVaultWindowsServiceState.Running,
                "FluxVault service is running.");
            return Task.FromResult(new FluxVaultWindowsServiceActionResult(true, status, status.Message));
        }

        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default)
        {
            var status = new FluxVaultWindowsServiceStatus(
                "FluxVaultService",
                FluxVaultWindowsServiceState.Stopped,
                "FluxVault service is stopped.");
            return Task.FromResult(new FluxVaultWindowsServiceActionResult(true, status, status.Message));
        }
    }
}
