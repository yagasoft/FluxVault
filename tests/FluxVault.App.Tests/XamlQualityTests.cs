using System.IO;
using System.Xml.Linq;

namespace FluxVault.App.Tests;

public sealed class XamlQualityTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Activity_pane_refresh_button_uses_compact_header_column()
    {
        var document = LoadXaml("src", "FluxVault.App", "ActivityPaneWindow.xaml");

        var refreshButton = document
            .Descendants(XamlNamespace + "Button")
            .Single(element => (string?)element.Attribute("Content") == "Refresh");

        Assert.Equal("1", (string?)refreshButton.Attribute("Grid.Column"));
        Assert.Equal("Right", (string?)refreshButton.Attribute("HorizontalAlignment"));
        Assert.DoesNotContain(refreshButton.Ancestors(), ancestor => ancestor.Name == XamlNamespace + "DockPanel");
    }

    [Fact]
    public void Options_controls_have_helpful_tooltips()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        var interactiveElements = document
            .Descendants()
            .Where(element => element.Name.Namespace == XamlNamespace
                              && element.Name.LocalName is "TextBox" or "ComboBox" or "CheckBox" or "Button")
            .ToArray();

        var missingTooltips = interactiveElements
            .Where(element => string.IsNullOrWhiteSpace((string?)element.Attribute("ToolTip")))
            .Select(Describe)
            .ToArray();

        Assert.Empty(missingTooltips);
    }

    [Fact]
    public void Options_tooltips_wrap_string_content_instead_of_clipping()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        var tooltipStyle = document
            .Descendants(XamlNamespace + "Style")
            .Single(element => (string?)element.Attribute("TargetType") == "ToolTip");
        var wrappingTextBlock = tooltipStyle
            .Descendants(XamlNamespace + "TextBlock")
            .SingleOrDefault(element => (string?)element.Attribute("Text") == "{Binding}");

        Assert.NotNull(wrappingTextBlock);
        Assert.Equal("Wrap", (string?)wrappingTextBlock.Attribute("TextWrapping"));
        Assert.Equal("460", (string?)wrappingTextBlock.Attribute("MaxWidth"));
    }

    [Fact]
    public void Options_dialog_exposes_explorer_context_menu_registration_actions()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        var register = document
            .Descendants(XamlNamespace + "Button")
            .Single(element => (string?)element.Attribute("Name") == "RegisterExplorerContextMenuButton");
        var unregister = document
            .Descendants(XamlNamespace + "Button")
            .Single(element => (string?)element.Attribute("Name") == "UnregisterExplorerContextMenuButton");

        Assert.Equal("{Binding RegisterExplorerContextMenuCommand}", (string?)register.Attribute("Command"));
        Assert.Equal("{Binding UnregisterExplorerContextMenuCommand}", (string?)unregister.Attribute("Command"));
        Assert.False(string.IsNullOrWhiteSpace((string?)register.Attribute("ToolTip")));
        Assert.False(string.IsNullOrWhiteSpace((string?)unregister.Attribute("ToolTip")));
        Assert.Contains(
            document.Descendants(XamlNamespace + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding ExplorerContextMenuStatus}");
    }

    [Fact]
    public void Options_dialog_removes_global_regex_editor_and_keeps_footer_buttons_sticky()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        var rootGrid = document
            .Descendants(XamlNamespace + "Grid")
            .First(element => (string?)element.Attribute("Name") == "OptionsShell");
        var footer = document
            .Descendants(XamlNamespace + "StackPanel")
            .Single(element => (string?)element.Attribute("Name") == "OptionsStickyFooter");
        var tabControl = document
            .Descendants(XamlNamespace + "TabControl")
            .Single();

        Assert.DoesNotContain(
            document.Descendants(XamlNamespace + "TextBlock"),
            element => (string?)element.Attribute("Text") == "Path exclusions");
        Assert.DoesNotContain(
            document.Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Content") == "Add exclusion");
        Assert.DoesNotContain(
            document.Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Content") == "Remove selected");
        Assert.Equal("4", (string?)footer.Attribute("Grid.Row"));
        Assert.Equal("Right", (string?)footer.Attribute("HorizontalAlignment"));
        Assert.Equal("2", (string?)tabControl.Attribute("Grid.Row"));
        Assert.Contains(
            rootGrid.Element(XamlNamespace + "Grid.RowDefinitions")?.Elements(XamlNamespace + "RowDefinition") ?? [],
            row => (string?)row.Attribute("Height") == "*");
    }

    [Fact]
    public void Options_tab_content_scrolls_without_hiding_save_and_close()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        var window = document.Root ?? throw new InvalidOperationException("Missing root.");
        var scrollViewers = document
            .Descendants(XamlNamespace + "ScrollViewer")
            .Where(element => (string?)element.Attribute("Name") is "BasicOptionsScrollViewer" or "AdvancedOptionsScrollViewer")
            .ToArray();

        Assert.True(int.Parse((string?)window.Attribute("MinHeight") ?? "0") >= 620);
        Assert.Equal(2, scrollViewers.Length);
        Assert.All(
            scrollViewers,
            scroll =>
            {
                Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
                Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
            });
    }

    [Fact]
    public void Options_labels_have_helpful_tooltips()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        string[] optionLabels =
        [
            "Keep every version for",
            "Keep hourly versions for days",
            "Keep daily versions for days",
            "Minimum versions per file",
            "Watcher poll seconds",
            "Reconciliation minutes",
            "Debounce fast / balanced / quiet",
            "Max hot delay fast / balanced / quiet",
            "Minimum same-file interval seconds",
            "Maximum concurrent captures",
            "Profile / default / hot-file",
            "Level / minimum KB",
            "Maintenance interval hours",
            "Restore rehearsal versions",
            "Default workload preset",
            "Skip extensions"
        ];

        foreach (var label in optionLabels)
        {
            var textBlock = document
                .Descendants(XamlNamespace + "TextBlock")
                .Single(element => (string?)element.Attribute("Text") == label);
            Assert.False(string.IsNullOrWhiteSpace((string?)textBlock.Attribute("ToolTip")), label);
        }
    }

    [Fact]
    public void Options_dialog_exposes_repository_maintenance_controls()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");

        Assert.Contains(
            document.Descendants(XamlNamespace + "TextBlock"),
            element => (string?)element.Attribute("Text") == "Repository maintenance");
        Assert.Contains(
            document.Descendants(XamlNamespace + "CheckBox"),
            element => (string?)element.Attribute("Content") == "Enable scheduled repository maintenance"
                       && (string?)element.Attribute("IsChecked") == "{Binding MaintenanceEnabled}");
        Assert.Contains(
            document.Descendants(XamlNamespace + "CheckBox"),
            element => (string?)element.Attribute("Content") == "Repair automatically from mirror"
                       && (string?)element.Attribute("IsChecked") == "{Binding MaintenanceAutoRepairFromMirror}");
        Assert.Contains(
            document.Descendants(XamlNamespace + "TextBox"),
            element => (string?)element.Attribute("Text") == "{Binding MaintenanceIntervalHours, UpdateSourceTrigger=PropertyChanged}");
        Assert.Contains(
            document.Descendants(XamlNamespace + "TextBox"),
            element => (string?)element.Attribute("Text") == "{Binding RestoreRehearsalVersionCount, UpdateSourceTrigger=PropertyChanged}");
    }

    [Fact]
    public void Options_dialog_exposes_codec_skip_extension_editor()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        var editor = document
            .Descendants(XamlNamespace + "TextBox")
            .Single(element => (string?)element.Attribute("Text") == "{Binding CodecSkipExtensionsText, UpdateSourceTrigger=PropertyChanged}");

        Assert.Equal("True", (string?)editor.Attribute("AcceptsReturn"));
        Assert.Equal("Auto", (string?)editor.Attribute("VerticalScrollBarVisibility"));
        Assert.False(string.IsNullOrWhiteSpace((string?)editor.Attribute("ToolTip")));
    }

    [Fact]
    public void Options_dialog_exposes_default_workload_preset_control()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        var selector = document
            .Descendants(XamlNamespace + "ComboBox")
            .Single(element => (string?)element.Attribute("SelectedValue") == "{Binding DefaultWorkloadPreset}");

        Assert.Equal("{Binding WorkloadPresets}", (string?)selector.Attribute("ItemsSource"));
        Assert.Equal("Id", (string?)selector.Attribute("SelectedValuePath"));
        Assert.Equal("DisplayName", (string?)selector.Attribute("DisplayMemberPath"));
        Assert.False(string.IsNullOrWhiteSpace((string?)selector.Attribute("ToolTip")));
    }

    [Fact]
    public void Main_header_constrains_status_text_away_from_command_buttons()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");

        Assert.Empty(document.Descendants(XamlNamespace + "DockPanel"));
        var serviceStatus = document
            .Descendants(XamlNamespace + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding ServiceStatus}");
        var commandPanel = document
            .Descendants(XamlNamespace + "StackPanel")
            .Single(element => (string?)element.Attribute("Grid.Column") == "1"
                && element
                .Descendants(XamlNamespace + "TextBlock")
                .Any(text => (string?)text.Attribute("Text") == "Export diagnostics"));

        Assert.Equal("CharacterEllipsis", (string?)serviceStatus.Attribute("TextTrimming"));
        Assert.Equal("0", (string?)serviceStatus.Attribute("MinWidth"));
        Assert.NotNull(serviceStatus.Element(XamlNamespace + "TextBlock.ToolTip"));
        Assert.Equal("1", (string?)commandPanel.Attribute("Grid.Column"));
    }

    [Fact]
    public void Usn_health_uses_wrapping_tooltip_content()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var usnHealth = document
            .Descendants(XamlNamespace + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding UsnHealth}");
        var tooltipText = usnHealth
            .Element(XamlNamespace + "TextBlock.ToolTip")
            ?.Element(XamlNamespace + "ToolTip")
            ?.Element(XamlNamespace + "TextBlock");

        Assert.NotNull(tooltipText);
        Assert.Equal("{Binding UsnHealthToolTip}", (string?)tooltipText.Attribute("Text"));
        Assert.Equal("Wrap", (string?)tooltipText.Attribute("TextWrapping"));
    }

    [Fact]
    public void Main_window_contains_three_pane_file_browser_tab()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var fileBrowserTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "File browser"));
        var paneGrid = fileBrowserTab
            .Descendants(XamlNamespace + "Grid")
            .Single(element => (string?)element.Attribute("Name") == "FileBrowserPaneGrid");

        Assert.Equal(3, paneGrid.Element(XamlNamespace + "Grid.ColumnDefinitions")?.Elements(XamlNamespace + "ColumnDefinition").Count());
    }

    [Fact]
    public void Repository_versions_grid_surfaces_lineage_column()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var repositoryTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "Repository"));

        Assert.Contains(
            repositoryTab.Descendants(XamlNamespace + "DataGridTextColumn"),
            column => (string?)column.Attribute("Header") == "Lineage"
                && (string?)column.Attribute("Binding") == "{Binding Lineage}");
    }

    [Fact]
    public void Main_window_contains_mirrors_workspace_and_repository_summary_link()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var mirrorsTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "Mirrors"));
        var protectionTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "Protection"));

        Assert.DoesNotContain(
            protectionTab.Descendants(XamlNamespace + "TextBox"),
            element => ((string?)element.Attribute("Text"))?.Contains("MirrorPath", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            protectionTab.Descendants(XamlNamespace + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding MirrorSummary}");
        Assert.Contains(
            protectionTab.Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding OpenMirrorsWorkspaceCommand}");
        Assert.Contains(
            mirrorsTab.Descendants(XamlNamespace + "DataGrid"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding MirrorNodes}"
                && (string?)element.Attribute("SelectedItem") == "{Binding SelectedMirrorNode}");
        Assert.Contains(
            mirrorsTab.Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding AddMirrorCommand}");
        Assert.Contains(
            mirrorsTab.Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding RemoveMirrorCommand}");
    }

    [Fact]
    public void Mirrors_workspace_does_not_expose_future_r2_controls()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var mirrorsTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "Mirrors"));
        var text = string.Join(
            " ",
            mirrorsTab
                .Descendants()
                .SelectMany(element => new[]
                {
                    (string?)element.Attribute("Text"),
                    (string?)element.Attribute("Content"),
                    (string?)element.Attribute("Header")
                })
                .OfType<string>());

        Assert.DoesNotContain("capacity", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rebalance", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("redundancy", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("required", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drain", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diagnostics_tab_exposes_repository_health_dashboard_and_manual_maintenance_actions()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var diagnosticsTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "Diagnostics"));

        Assert.Contains(
            diagnosticsTab.Descendants(XamlNamespace + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding RepositoryHealthStatus}");
        Assert.Contains(
            diagnosticsTab.Descendants(XamlNamespace + "DataGrid"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding RepositoryHealthRows}");
        Assert.Contains(
            diagnosticsTab.Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Content") == "Run scrub"
                && (string?)element.Attribute("Command") == "{Binding RunRepositoryScrubCommand}");
        Assert.Contains(
            diagnosticsTab.Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Content") == "Run restore rehearsal"
                && (string?)element.Attribute("Command") == "{Binding RunRestoreRehearsalCommand}");
        Assert.Contains(
            diagnosticsTab.Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding ExportDiagnosticsCommand}");
    }

    [Fact]
    public void File_browser_exposes_scoped_regex_editor_and_folder_indicator()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var fileBrowserTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "File browser"));

        Assert.Contains(
            fileBrowserTab.Descendants(XamlNamespace + "TextBox"),
            element => (string?)element.Attribute("Text") == "{Binding FileBrowser.SelectedIncludeRegexText, UpdateSourceTrigger=PropertyChanged}");
        Assert.Contains(
            fileBrowserTab.Descendants(XamlNamespace + "TextBox"),
            element => (string?)element.Attribute("Text") == "{Binding FileBrowser.SelectedExcludeRegexText, UpdateSourceTrigger=PropertyChanged}");
        Assert.Contains(
            fileBrowserTab.Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding FileBrowser.ApplySelectedRegexRulesCommand}");
        Assert.Contains(
            fileBrowserTab.Descendants(XamlNamespace + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding RegexIndicator}");
    }

    [Fact]
    public void File_browser_exposes_workload_preset_editor()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var fileBrowserTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "File browser"));
        var selector = fileBrowserTab
            .Descendants(XamlNamespace + "ComboBox")
            .Single(element => (string?)element.Attribute("SelectedValue") == "{Binding FileBrowser.SelectedWorkloadPreset}");

        Assert.Equal("{Binding FileBrowser.WorkloadPresets}", (string?)selector.Attribute("ItemsSource"));
        Assert.Equal("Id", (string?)selector.Attribute("SelectedValuePath"));
        Assert.Equal("DisplayName", (string?)selector.Attribute("DisplayMemberPath"));
        Assert.False(string.IsNullOrWhiteSpace((string?)selector.Attribute("ToolTip")));
        Assert.Contains(
            fileBrowserTab.Descendants(XamlNamespace + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding FileBrowser.SelectedWorkloadPresetDescription}");
    }

    [Fact]
    public void File_browser_folder_and_file_context_menus_use_shell_commands()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var fileBrowserTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "File browser"));
        var tree = fileBrowserTab.Descendants(XamlNamespace + "TreeView").Single();
        var filesGrid = fileBrowserTab
            .Descendants(XamlNamespace + "DataGrid")
            .Single(element => (string?)element.Attribute("Name") == "FileBrowserFilesGrid");

        Assert.Contains(
            tree.Descendants(XamlNamespace + "MenuItem"),
            item => (string?)item.Attribute("Header") == "Show in File Explorer"
                && (string?)item.Attribute("Command") == "{Binding FileBrowser.ShowSelectedFolderInExplorerCommand}");
        Assert.Contains(
            filesGrid.Descendants(XamlNamespace + "MenuItem"),
            item => (string?)item.Attribute("Header") == "Open in default app"
                && (string?)item.Attribute("Command") == "{Binding FileBrowser.OpenSelectedFileCommand}");
        Assert.Contains(
            filesGrid.Descendants(XamlNamespace + "MenuItem"),
            item => (string?)item.Attribute("Header") == "Show in File Explorer"
                && (string?)item.Attribute("Command") == "{Binding FileBrowser.ShowSelectedFileInExplorerCommand}");
        Assert.Equal("{Binding FileBrowser.SelectedFile}", (string?)filesGrid.Attribute("SelectedItem"));
    }

    [Fact]
    public void File_browser_uses_equal_width_scrollable_panes()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var fileBrowserTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "File browser"));
        var paneGrid = fileBrowserTab
            .Descendants(XamlNamespace + "Grid")
            .Single(element => (string?)element.Attribute("Name") == "FileBrowserPaneGrid");
        var widths = paneGrid
            .Element(XamlNamespace + "Grid.ColumnDefinitions")
            ?.Elements(XamlNamespace + "ColumnDefinition")
            .Select(element => (string?)element.Attribute("Width"))
            .ToArray();

        Assert.Equal(new[] { "*", "*", "*" }, widths ?? []);
        Assert.Empty(fileBrowserTab.Descendants(XamlNamespace + "ScrollViewer"));

        var tree = fileBrowserTab
            .Descendants(XamlNamespace + "TreeView")
            .Single();
        Assert.Equal("Auto", (string?)tree.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)tree.Attribute("ScrollViewer.VerticalScrollBarVisibility"));

        var grids = fileBrowserTab
            .Descendants(XamlNamespace + "DataGrid")
            .ToArray();
        Assert.All(
            grids,
            grid =>
            {
                Assert.Equal("Auto", (string?)grid.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
                Assert.Equal("Auto", (string?)grid.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
            });
    }

    [Fact]
    public void File_browser_folder_selection_uses_visual_indicator_not_text_labels()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");

        Assert.DoesNotContain(document
            .Descendants(XamlNamespace + "Button"),
            element => (string?)element.Attribute("Content") == "{Binding SelectionGlyph}");
        Assert.NotNull(document
            .Descendants(XamlNamespace + "TextBlock")
            .SingleOrDefault(element => (string?)element.Attribute("Text") == "{Binding SelectionIndicator}"));
    }

    [Fact]
    public void Health_tiles_are_in_footer_bar_not_header_strip()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var footer = document
            .Descendants(XamlNamespace + "UniformGrid")
            .SingleOrDefault(element => (string?)element.Attribute("Name") == "FooterHealthBar");

        Assert.NotNull(footer);
        Assert.Equal("4", (string?)footer.Attribute("Grid.Row"));
        Assert.DoesNotContain(document
            .Descendants(XamlNamespace + "UniformGrid"),
            element => (string?)element.Attribute("Grid.Row") == "2");
    }

    [Fact]
    public void Main_window_declares_operational_cockpit_runtime_shell()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var shell = document
            .Descendants(XamlNamespace + "Grid")
            .Single(element => (string?)element.Attribute("Name") == "OperationalCockpitShell");
        var commandBar = document
            .Descendants(XamlNamespace + "StackPanel")
            .Single(element => (string?)element.Attribute("Name") == "OperationalCockpitCommandBar");
        var workspace = document
            .Descendants(XamlNamespace + "TabControl")
            .Single(element => (string?)element.Attribute("Name") == "OperationalCockpitWorkspace");
        var footer = document
            .Descendants(XamlNamespace + "UniformGrid")
            .Single(element => (string?)element.Attribute("Name") == "FooterHealthBar");

        Assert.Equal("22", (string?)shell.Attribute("Margin"));
        Assert.Equal("1", (string?)commandBar.Attribute("Grid.Column"));
        Assert.Equal("Left", (string?)workspace.Attribute("TabStripPlacement"));
        Assert.Equal("{Binding SelectedWorkspaceIndex}", (string?)workspace.Attribute("SelectedIndex"));
        Assert.Contains(workspace.Descendants(XamlNamespace + "TabItem"), element => HasHeader(element, "File browser"));
        Assert.Equal("4", (string?)footer.Attribute("Grid.Row"));
    }

    [Fact]
    public void Main_window_contains_service_warning_and_toggle_controls()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var warning = document
            .Descendants(XamlNamespace + "Border")
            .Single(element => (string?)element.Attribute("Name") == "ServiceAvailabilityWarning");
        var warningText = warning
            .Descendants(XamlNamespace + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding ServiceWarningText}");
        var toggle = document
            .Descendants(XamlNamespace + "Button")
            .Single(element => (string?)element.Attribute("Name") == "ServiceControlToggleButton");

        Assert.Contains(
            warning.Descendants(XamlNamespace + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Binding") == "{Binding IsServiceWarningVisible}"
                && (string?)trigger.Attribute("Value") == "True");
        Assert.Equal("Wrap", (string?)warningText.Attribute("TextWrapping"));
        Assert.Equal("{Binding ToggleWindowsServiceCommand}", (string?)toggle.Attribute("Command"));
        Assert.Equal("{Binding IsServiceControlActionEnabled}", (string?)toggle.Attribute("IsEnabled"));
        Assert.Contains(
            toggle.Descendants(XamlNamespace + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding ServiceControlActionLabel}");
    }

    [Fact]
    public void Main_command_bar_prioritises_operational_cockpit_actions()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var commandBar = document
            .Descendants(XamlNamespace + "StackPanel")
            .Single(element => (string?)element.Attribute("Name") == "OperationalCockpitCommandBar");

        Assert.Equal(
            ["Refresh", "{Binding ServiceControlActionLabel}", "Run backup now", "Restore", "Options", "About", "Export diagnostics"],
            CommandButtonLabels(commandBar));
    }

    [Fact]
    public void Main_commands_and_navigation_include_icons_with_text()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var refreshButton = document
            .Descendants(XamlNamespace + "Button")
            .Single(element => (string?)element.Attribute("Command") == "{Binding RefreshCommand}");
        var fileBrowserTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => ((string?)element.Attribute("Header"))?.Contains("File browser", StringComparison.OrdinalIgnoreCase) == true);

        Assert.Contains(refreshButton.Descendants(XamlNamespace + "TextBlock"), text => (string?)text.Attribute("Text") == "Refresh");
        Assert.Contains(refreshButton.Descendants(XamlNamespace + "TextBlock"), text => (string?)text.Attribute("Text") == "\u27F3");
        Assert.Contains("\uD83D\uDCC1", ((string?)fileBrowserTab.Attribute("Header"))!);
    }

    [Fact]
    public void Protection_tab_no_longer_contains_watched_folder_browse_add_remove_controls()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var protectionTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "Protection"));
        var buttonLabels = protectionTab
            .Descendants(XamlNamespace + "Button")
            .Select(element => (string?)element.Attribute("Content"))
            .Where(value => value is not null)
            .ToArray();

        Assert.DoesNotContain("Browse", buttonLabels);
        Assert.DoesNotContain("Add", buttonLabels);
        Assert.DoesNotContain("Remove", buttonLabels);
    }

    [Fact]
    public void File_browser_interactive_controls_have_tooltips()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var fileBrowserTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "File browser"));
        var interactiveElements = fileBrowserTab
            .Descendants()
            .Where(element => element.Name.Namespace == XamlNamespace
                              && element.Name.LocalName is "Button" or "DataGrid")
            .ToArray();

        var missingTooltips = interactiveElements
            .Where(element => string.IsNullOrWhiteSpace((string?)element.Attribute("ToolTip")))
            .Select(Describe)
            .ToArray();

        Assert.Empty(missingTooltips);
    }

    [Fact]
    public void Main_header_contains_about_button_with_accessible_tooltip()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var aboutButton = document
            .Descendants(XamlNamespace + "Button")
            .Single(element => (string?)element.Attribute("Click") == "About_Click");

        Assert.Equal("About FluxVault.", (string?)aboutButton.Attribute("ToolTip"));
        Assert.Contains(
            aboutButton.Descendants(XamlNamespace + "TextBlock"),
            text => (string?)text.Attribute("Text") == "About");
        Assert.Contains(
            aboutButton.Descendants(XamlNamespace + "TextBlock"),
            text => (string?)text.Attribute("Text") == "ⓘ");
    }

    [Fact]
    public void About_window_contains_yagasoft_branding_links_and_roadmap()
    {
        var document = LoadXaml("src", "FluxVault.App", "AboutWindow.xaml");

        Assert.Contains(
            document.Descendants(XamlNamespace + "Image"),
            image => ((string?)image.Attribute("Source"))?.Contains("YagasoftLogo.png", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            document.Descendants(XamlNamespace + "TextBlock"),
            text => ((string?)text.Attribute("Text"))?.Contains("Yagasoft", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            document.Descendants(XamlNamespace + "TextBlock"),
            text => (string?)text.Attribute("Text") == "https://github.com/yagasoft/FluxVault");
        Assert.Contains(
            document.Descendants(XamlNamespace + "TextBlock"),
            text => (string?)text.Attribute("Text") == "https://yagasoft.com/");
        Assert.Contains(
            document.Descendants(XamlNamespace + "ItemsControl"),
            control => (string?)control.Attribute("ItemsSource") == "{Binding RoadmapMilestones}");
    }

    [Fact]
    public void File_browser_tree_has_no_broad_tooltip_and_keeps_selection_tooltip()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var fileBrowserTab = document
            .Descendants(XamlNamespace + "TabItem")
            .Single(element => HasHeader(element, "File browser"));
        var tree = fileBrowserTab
            .Descendants(XamlNamespace + "TreeView")
            .Single();
        var selectionButton = tree
            .Descendants(XamlNamespace + "Button")
            .Single(element => (string?)element.Attribute("Click") == "FolderSelection_Click");

        Assert.Null((string?)tree.Attribute("ToolTip"));
        Assert.Equal("{Binding SelectionToolTip}", (string?)selectionButton.Attribute("ToolTip"));
    }

    [Fact]
    public void File_browser_grids_are_named_and_wired_for_one_time_auto_fit()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var filesGrid = document
            .Descendants(XamlNamespace + "DataGrid")
            .Single(element => (string?)element.Attribute("Name") == "FileBrowserFilesGrid");
        var pendingGrid = document
            .Descendants(XamlNamespace + "DataGrid")
            .Single(element => (string?)element.Attribute("Name") == "FileBrowserPendingChangesGrid");
        var codeBehind = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "FluxVault.App", "MainWindow.xaml.cs"));

        Assert.Equal("FileBrowserGrid_Loaded", (string?)filesGrid.Attribute("Loaded"));
        Assert.Equal("FileBrowserGrid_Loaded", (string?)pendingGrid.Attribute("Loaded"));
        Assert.Contains("AutoFitDataGridColumnsOnce(FileBrowserFilesGrid", codeBehind);
        Assert.Contains("AutoFitDataGridColumnsOnce(FileBrowserPendingChangesGrid", codeBehind);
    }

    private static XDocument LoadXaml(params string[] relativePathParts)
    {
        var root = FindRepositoryRoot();
        return XDocument.Load(Path.Combine([root, .. relativePathParts]));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FluxVault.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string Describe(XElement element)
    {
        return $"{element.Name.LocalName}:{(string?)element.Attribute("Content") ?? (string?)element.Attribute("Text") ?? (string?)element.Attribute("Name") ?? element.ToString(SaveOptions.DisableFormatting)}";
    }

    private static string[] CommandButtonLabels(XElement commandBar)
    {
        return commandBar
            .Elements(XamlNamespace + "Button")
            .Select(button => button
                .Descendants(XamlNamespace + "TextBlock")
                .Select(element => (string?)element.Attribute("Text"))
                .OfType<string>()
                .Where(value => value is "Refresh"
                    or "{Binding ServiceControlActionLabel}"
                    or "Run backup now"
                    or "Restore"
                    or "Options"
                    or "About"
                    or "Export diagnostics")
                .Single())
            .ToArray();
    }

    private static bool HasHeader(XElement element, string headerText)
    {
        return ((string?)element.Attribute("Header"))?.Contains(headerText, StringComparison.OrdinalIgnoreCase) == true;
    }
}
