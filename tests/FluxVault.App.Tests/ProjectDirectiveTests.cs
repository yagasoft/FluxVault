using System.IO;

namespace FluxVault.App.Tests;

public sealed class ProjectDirectiveTests
{
    [Fact]
    public void Root_agents_file_records_configuration_options_directive()
    {
        var path = Path.Combine(FindRepositoryRoot(), "AGENTS.md");

        var text = File.ReadAllText(path);

        Assert.Contains("parameterise appropriate and reasonable configurations", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Options dialogue", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dormant", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Root_agents_file_records_local_code_index_directive()
    {
        var path = Path.Combine(FindRepositoryRoot(), "AGENTS.md");

        var text = File.ReadAllText(path);

        Assert.Contains(@"D:\Drive\Work (1)\Code\FluxVault", text, StringComparison.Ordinal);
        Assert.Contains(@"scripts\fluxvault-index.ps1 search ""<query>""", text, StringComparison.Ordinal);
        Assert.Contains(@"scripts\fluxvault-index.ps1 search ""<query>"" --semantic-mode always", text, StringComparison.Ordinal);
        Assert.Contains(@"scripts\fluxvault-index.ps1 doctor", text, StringComparison.Ordinal);
        Assert.Contains(@"scripts\fluxvault-index.ps1 sync", text, StringComparison.Ordinal);
        Assert.Contains(@"scripts\fluxvault-index.ps1 watch", text, StringComparison.Ordinal);
        Assert.Contains(@"scripts\fluxvault-index.ps1 install-startup-watcher", text, StringComparison.Ordinal);
        Assert.Contains("FluxVault_Codex", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\Codex\FluxVault", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\Codex\code-indexer", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\Codex\code-indexer\venv", text, StringComparison.Ordinal);
        Assert.Contains("central CUDA PyTorch runtime", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FluxVault project state, logs, watcher lock files, and watcher launchers", text, StringComparison.Ordinal);
        Assert.Contains("YsTrader_Codex", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\Codex\YsTrader", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fluxvault_index_shims_record_central_indexer_profile()
    {
        var root = FindRepositoryRoot();
        var wrapperPath = Path.Combine(root, "scripts", "fluxvault-index.ps1");
        var profilePath = Path.Combine(root, "scripts", "fluxvault-index.json");

        Assert.True(File.Exists(wrapperPath), "FluxVault index wrapper script should exist.");
        Assert.True(File.Exists(profilePath), "FluxVault index profile should exist.");

        var wrapperText = File.ReadAllText(wrapperPath);
        var profileText = File.ReadAllText(profilePath);

        Assert.Contains(@"D:\Codex\code-indexer\code-indexer.ps1", wrapperText, StringComparison.Ordinal);
        Assert.Contains("FLUXVAULT_CODEX_RUNTIME_ROOT", wrapperText, StringComparison.Ordinal);
        Assert.Contains("fluxvault-index.json", wrapperText, StringComparison.Ordinal);
        Assert.DoesNotContain("YsTrader", wrapperText, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(@"""projectName"": ""FluxVault""", profileText, StringComparison.Ordinal);
        Assert.Contains(@"""projectSlug"": ""fluxvault""", profileText, StringComparison.Ordinal);
        Assert.Contains(@"""sqlDatabase"": ""FluxVault_Codex""", profileText, StringComparison.Ordinal);
        Assert.Contains(@"""runtimeRoot"": ""D:\\Codex\\FluxVault""", profileText, StringComparison.Ordinal);
        Assert.Contains(@"""sharedRuntimeRoot"": ""D:\\Codex\\code-indexer""", profileText, StringComparison.Ordinal);
        Assert.Contains(@"""sharedCacheRoot"": ""D:\\Codex\\code-indexer\\cache""", profileText, StringComparison.Ordinal);
        Assert.Contains(@"""fullTextCatalog"": ""ftcat_fluxvault""", profileText, StringComparison.Ordinal);
        Assert.Contains(@"""startupRegistryName"": ""FluxVaultCodexIndexWatcher""", profileText, StringComparison.Ordinal);
        Assert.Contains(@"""startupLauncherName"": ""Start-FluxVaultCodeIndexWatcher.cmd""", profileText, StringComparison.Ordinal);
        Assert.Contains(@""".slnx""", profileText, StringComparison.Ordinal);
        Assert.Contains(@""".xaml""", profileText, StringComparison.Ordinal);
        Assert.Contains(@""".wxs""", profileText, StringComparison.Ordinal);
        Assert.DoesNotContain("YsTrader", profileText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Roadmap_tracker_marks_later_security_and_fleet_foundation_as_implemented()
    {
        var path = Path.Combine(FindRepositoryRoot(), "docs", "roadmap-tracker.md");

        var text = File.ReadAllText(path);

        Assert.Contains("| LATER-001 | Later | Client-side encryption and enterprise/fleet features | Implemented |", text, StringComparison.Ordinal);
        Assert.Contains("SecurityPostureConfiguration", text, StringComparison.Ordinal);
        Assert.Contains("EnterpriseFleetConfiguration", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Requirements_product_goal_reflects_direct_cloud_adapter_foundation()
    {
        var path = Path.Combine(FindRepositoryRoot(), "docs", "requirements.md");

        var text = File.ReadAllText(path);

        Assert.DoesNotContain("direct cloud adapters arrive later", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("direct cloud adapter foundation", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FluxVault.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find FluxVault repository root.");
    }
}
