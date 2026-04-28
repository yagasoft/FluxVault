namespace FluxVault.Integration.Tests;

public sealed class PackagingTests
{
    [Fact]
    public void Developer_install_scripts_are_present()
    {
        var root = FindRepositoryRoot();

        Assert.True(File.Exists(Path.Combine(root, "eng", "install-service.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "eng", "uninstall-service.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "installer", "quickstart.md")));
        Assert.True(File.Exists(Path.Combine(root, "installer", "sample-config.json")));
    }

    [Fact]
    public void Install_script_configures_recovery_delayed_start_event_log_and_waits_for_transitions()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "eng", "install-service.ps1"));

        Assert.Contains("function Wait-ServiceStatus", script);
        Assert.Contains("function Wait-ServiceDeleted", script);
        Assert.Contains("New-EventLog", script);
        Assert.Contains("FluxVaultService", script);
        Assert.Contains("start= delayed-auto", script);
        Assert.Contains("failureflag", script);
        Assert.Contains("restart/60000/restart/60000", script);
    }

    [Fact]
    public void Install_script_resolves_publish_root_from_source_or_copied_package_location()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "eng", "install-service.ps1"));

        Assert.Contains("function Resolve-PublishRoot", script);
        Assert.Contains("Join-Path $PSScriptRoot \"service\\FluxVault.Service.exe\"", script);
        Assert.Contains("Join-Path $PSScriptRoot \"..\\artifacts\\publish\\service\\FluxVault.Service.exe\"", script);
    }

    [Fact]
    public void Uninstall_script_preserves_state_by_default_and_requires_explicit_cleanup_switches()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "eng", "uninstall-service.ps1"));

        Assert.Contains("[switch]$RemoveProgramData", script);
        Assert.Contains("[switch]$RemoveEventLogSource", script);
        Assert.Contains("function Wait-ServiceStatus", script);
        Assert.Contains("function Wait-ServiceDeleted", script);
        Assert.Contains("if ($RemoveProgramData)", script);
        Assert.Contains("if ($RemoveEventLogSource)", script);
    }

    [Fact]
    public void Developer_service_scripts_do_not_register_explorer_context_menu()
    {
        var root = FindRepositoryRoot();
        var installScript = File.ReadAllText(Path.Combine(root, "eng", "install-service.ps1"));
        var uninstallScript = File.ReadAllText(Path.Combine(root, "eng", "uninstall-service.ps1"));

        Assert.DoesNotContain("--restore-path", installScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--restore-path", uninstallScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--add-path", installScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--add-path", uninstallScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--remove-path", installScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--remove-path", uninstallScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Software\\Classes\\*\\shell", installScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Software\\Classes\\*\\shell", uninstallScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RegisterExplorerContextMenu", installScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RegisterExplorerContextMenu", uninstallScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void App_context_menu_service_declares_ordered_full_menu_verbs_and_compact_status()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "FluxVault.App", "Services", "ExplorerContextMenuService.cs"));

        Assert.Contains("Add to FluxVault", source);
        Assert.Contains("Show FluxVault versions", source);
        Assert.Contains("Remove from FluxVault", source);
        Assert.Contains("--add-path", source);
        Assert.Contains("--show-versions", source);
        Assert.Contains("--remove-path", source);
        Assert.Contains("FluxVault01Add", source);
        Assert.Contains("FluxVault02ShowVersions", source);
        Assert.Contains("FluxVault03Remove", source);
        Assert.Contains("IExplorerCommand", source);
        Assert.Contains("compact", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void App_icon_is_packaged_as_windows_icon_resource()
    {
        var root = FindRepositoryRoot();
        var iconPath = Path.Combine(root, "src", "FluxVault.App", "Assets", "FluxVault.ico");
        var project = File.ReadAllText(Path.Combine(root, "src", "FluxVault.App", "FluxVault.App.csproj"));

        Assert.True(File.Exists(iconPath));
        Assert.Contains("<ApplicationIcon>Assets\\FluxVault.ico</ApplicationIcon>", project);
        Assert.True(new FileInfo(iconPath).Length > 1024);
    }

    [Fact]
    public void Yagasoft_logo_is_packaged_without_replacing_app_icon()
    {
        var root = FindRepositoryRoot();
        var logoPath = Path.Combine(root, "src", "FluxVault.App", "Assets", "YagasoftLogo.png");
        var project = File.ReadAllText(Path.Combine(root, "src", "FluxVault.App", "FluxVault.App.csproj"));

        Assert.True(File.Exists(logoPath));
        Assert.Contains("<ApplicationIcon>Assets\\FluxVault.ico</ApplicationIcon>", project);
        Assert.Contains("<Content Include=\"Assets\\YagasoftLogo.png\">", project);
        Assert.True(new FileInfo(logoPath).Length > 1024);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FluxVault.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
