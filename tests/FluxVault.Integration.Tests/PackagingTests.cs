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
    public void Sparse_package_manifest_declares_compact_explorer_context_menu_identity()
    {
        var root = FindRepositoryRoot();
        var manifestPath = Path.Combine(root, "installer", "sparse-package", "AppxManifest.xml");

        Assert.True(File.Exists(manifestPath));
        var manifest = File.ReadAllText(manifestPath);

        Assert.Contains("<Identity Name=\"Yagasoft.FluxVault\"", manifest);
        Assert.Contains("<uap10:AllowExternalContent>true</uap10:AllowExternalContent>", manifest);
        Assert.DoesNotContain("EntryPoint=\"Windows.FullTrustApplication\"", manifest);
        Assert.Contains("<rescap:Capability Name=\"runFullTrust\" />", manifest);
        Assert.Contains("<rescap:Capability Name=\"unvirtualizedResources\" />", manifest);
        Assert.Contains("<com:Extension Category=\"windows.comServer\">", manifest);
        Assert.Contains("FluxVault.ExplorerCommand.dll", manifest);
        Assert.Contains("FluxVaultExplorerCommandHandler", manifest);
        Assert.Contains("<desktop4:Extension Category=\"windows.fileExplorerContextMenus\">", manifest);
        Assert.Contains("<desktop5:ItemType Type=\"*\">", manifest);
        Assert.Contains("<desktop5:ItemType Type=\"Directory\">", manifest);
        Assert.Contains("FluxVault01Add", manifest);
        Assert.Contains("FluxVault02ShowVersions", manifest);
        Assert.Contains("FluxVault03Remove", manifest);
    }

    [Fact]
    public void Compact_shell_extension_source_declares_verbs_and_forwarding_arguments()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "FluxVault.ExplorerCommand", "FluxVault.ExplorerCommand.vcxproj");
        var sourcePath = Path.Combine(root, "src", "FluxVault.ExplorerCommand", "FluxVaultExplorerCommand.cpp");

        Assert.True(File.Exists(projectPath));
        Assert.True(File.Exists(sourcePath));
        var project = File.ReadAllText(projectPath);
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("DynamicLibrary", project);
        Assert.Contains("FluxVault.ExplorerCommand.dll", project);
        Assert.Contains("IExplorerCommand", source);
        Assert.Contains("IExplorerCommandState", source);
        Assert.Contains("Add to FluxVault", source);
        Assert.Contains("Show FluxVault versions", source);
        Assert.Contains("Remove from FluxVault", source);
        Assert.Contains("--add-path", source);
        Assert.Contains("--show-versions", source);
        Assert.Contains("--remove-path", source);
        Assert.Contains("ShellExecuteW", source);
        Assert.Contains("FluxVault.App.exe", source);
    }

    [Fact]
    public void Compact_shell_extension_com_destructors_are_not_declared_as_overrides()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "FluxVault.ExplorerCommand", "FluxVaultExplorerCommand.cpp"));

        Assert.DoesNotContain("~FluxVaultExplorerCommand() override", source);
        Assert.DoesNotContain("~FluxVaultExplorerCommandFactory() override", source);
    }

    [Fact]
    public void Package_script_publishes_shell_extension_sparse_manifest_and_status_note()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "eng", "package.ps1"));

        Assert.Contains("FluxVault.ExplorerCommand.vcxproj", script);
        Assert.Contains("shell-extension", script);
        Assert.Contains("installer\\sparse-package", script);
        Assert.Contains("AppxManifest.xml", script);
        Assert.Contains("compact-menu-status.txt", script);
        Assert.Contains("MakeAppx.exe", script);
        Assert.Contains("/nv", script);
        Assert.Contains("SignTool.exe", script);
        Assert.Contains("MakeAppx.exe was found, but package creation failed", script);
    }

    [Fact]
    public void Package_script_preflights_visual_cpp_targets_before_native_build()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "eng", "package.ps1"));
        var nativeDefinition = File.ReadAllText(Path.Combine(root, "src", "FluxVault.ExplorerCommand", "FluxVault.ExplorerCommand.def"));
        var preflightIndex = script.IndexOf("Resolve-NativeMsBuild", StringComparison.Ordinal);
        var nativeBuildIndex = script.IndexOf("& $msbuild $shellExtensionProject", StringComparison.Ordinal);

        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", script);
        Assert.Contains("Microsoft.Cpp.Default.props", script);
        Assert.Contains("Visual C++ Build Tools are missing", script);
        Assert.True(preflightIndex >= 0, "The package script should resolve native MSBuild through a Visual C++ prerequisite preflight.");
        Assert.True(nativeBuildIndex >= 0, "The package script should still build the native shell extension when prerequisites exist.");
        Assert.True(preflightIndex < nativeBuildIndex, "The Visual C++ prerequisite preflight should run before invoking native MSBuild.");
        Assert.DoesNotContain("LIBRARY", nativeDefinition);
    }

    [Fact]
    public void Production_wix_installer_declares_service_recovery_upgrade_and_state_preservation()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "installer", "wix", "FluxVault.Installer", "FluxVault.Installer.wixproj");
        var packagePath = Path.Combine(root, "installer", "wix", "FluxVault.Installer", "Package.wxs");

        Assert.True(File.Exists(projectPath));
        Assert.True(File.Exists(packagePath));
        var project = File.ReadAllText(projectPath);
        var package = File.ReadAllText(packagePath);
        var normalizedPackage = package.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("WixToolset.Sdk/7.0.0", project);
        Assert.Contains("WixToolset.Util.wixext", project);
        Assert.Contains("<SuppressSpecificWarnings>1149</SuppressSpecificWarnings>", project);
        Assert.DoesNotContain("<SuppressAllWarnings>true</SuppressAllWarnings>", project);
        Assert.Contains("MajorUpgrade", package);
        Assert.Contains("UpgradeCode=\"4B89B6E7-D41E-49E6-BE42-09C10D6570D6\"", package);
        Assert.Contains("FluxVaultService", package);
        Assert.Contains("<ServiceInstall", package);
        Assert.Contains("<ServiceControl", package);
        Assert.Contains("<ServiceConfig", package);
        Assert.Contains("DelayedAutoStart=\"yes\"", package);
        Assert.Contains("util:ServiceConfig", package);
        Assert.DoesNotContain("util:ServiceConfig\n            ServiceName=\"FluxVaultService\"\n            DelayedAutoStart=\"yes\"", normalizedPackage);
        Assert.Contains("util:EventSource", package);
        Assert.Contains("Name=\"FluxVaultService\"", package);
        Assert.Contains("Log=\"Application\"", package);
        Assert.DoesNotContain("SYSTEM\\CurrentControlSet\\Services\\EventLog\\Application\\FluxVaultService", package);
        Assert.Contains("CommonAppDataFolder", package);
        Assert.Contains("Permanent=\"yes\"", package);
        Assert.Contains("NeverOverwrite=\"yes\"", package);
    }

    [Fact]
    public void Production_bundle_wraps_msi_and_signed_sparse_package()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "installer", "wix", "FluxVault.Bundle", "FluxVault.Bundle.wixproj");
        var bundlePath = Path.Combine(root, "installer", "wix", "FluxVault.Bundle", "Bundle.wxs");

        Assert.True(File.Exists(projectPath));
        Assert.True(File.Exists(bundlePath));
        var project = File.ReadAllText(projectPath);
        var bundle = File.ReadAllText(bundlePath);

        Assert.Contains("WixToolset.Sdk/7.0.0", project);
        Assert.Contains("WixToolset.Bal.wixext", project);
        Assert.Contains("<SuppressSpecificWarnings>1161</SuppressSpecificWarnings>", project);
        Assert.DoesNotContain("<SuppressAllWarnings>true</SuppressAllWarnings>", project);
        Assert.Contains("MsiPackage", bundle);
        Assert.Contains("FluxVault.Installer.msi", bundle);
        Assert.DoesNotContain("DisplayInternalUI", bundle);
        Assert.Contains("ExePackage", bundle);
        Assert.Contains("$(env.SystemRoot)\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", bundle);
        Assert.DoesNotContain("$(var.SystemFolder)", bundle);
        Assert.Contains("Add-AppxPackage", bundle);
        Assert.DoesNotContain("-AllowUnsigned", bundle);
        Assert.Contains("FluxVault.SparsePackage.msix", bundle);
        Assert.Contains("Yagasoft.FluxVault", bundle);
        Assert.Contains("Permanent=\"yes\"", bundle);
        Assert.DoesNotContain("DetectCondition=\"FluxVaultPackageName\"", bundle);
        Assert.DoesNotContain("UninstallArguments", bundle);
    }

    [Fact]
    public void Release_package_script_builds_signed_msi_bundle_and_sparse_package()
    {
        var root = FindRepositoryRoot();
        var scriptPath = Path.Combine(root, "eng", "release-package.ps1");

        Assert.True(File.Exists(scriptPath));
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("[switch]$RequireSigning", script);
        Assert.Contains("PackageCertificatePath is required", script);
        Assert.Contains("PackageCertificatePassword", script);
        Assert.Contains("FluxVault.Installer.wixproj", script);
        Assert.Contains("FluxVault.Bundle.wixproj", script);
        Assert.Contains("FluxVault.SparsePackage.msix", script);
        Assert.Contains("SignTool.exe", script);
        Assert.Contains("dotnet build", script);
        Assert.Contains("ReleasePackageRoot", script);
    }

    [Fact]
    public void Release_packaging_workflow_is_opt_in_and_not_a_pull_request_gate()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "release-package.yml");

        Assert.True(File.Exists(workflowPath));
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("workflow_dispatch", workflow);
        Assert.DoesNotContain("pull_request", workflow);
        Assert.Contains("eng\\release-package.ps1", workflow);
        Assert.Contains("PackagingTests", workflow);
        Assert.Contains("PACKAGE_CERTIFICATE", workflow);
        Assert.Contains("actions/upload-artifact", workflow);
        Assert.Contains("artifacts/release", workflow);
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
